using System.Diagnostics;
using System.Runtime.InteropServices;
using Suni.Audio.Interop;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Audio;

/// <summary>
/// Liệt kê ứng dụng có audio session trên mọi thiết bị phát đang hoạt động (FR-01),
/// gom về tiến trình gốc cùng tên exe (SDD 3.2), loại trừ chính ứng dụng và tiến trình con của nó.
/// </summary>
public sealed class AudioSourceEnumerator : IAudioSourceEnumerator
{
    public const string SystemAudioName = "Toàn bộ âm thanh hệ thống";

    public Task<IReadOnlyList<AudioSource>> GetSourcesAsync(CancellationToken ct = default) =>
        RunMta(() =>
        {
            var tree = ProcessTree.Snapshot();
            var self = Environment.ProcessId;
            var selfParent = tree.Get(self)?.ParentPid;

            // Windows giữ session "Inactive" của tiến trình đã thoát một thời gian → bỏ, không thì thu nhầm PID đã chết (im lặng + SourceLost).
            var groups = EnumerateSessions()
                .Where(s => s.Pid != 0 && tree.Get(s.Pid) is not null && !tree.IsSelfOrDescendantOf(s.Pid, self))
                .GroupBy(s => tree.FindRoot(s.Pid));

            var apps = groups.Select(g =>
            {
                var exe = tree.Get(g.Key)?.ExeName ?? tree.Get(g.First().Pid)?.ExeName;
                var name = g.Select(s => s.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@'))
                           ?? DescribeProcess(g.Key)
                           ?? Path.GetFileNameWithoutExtension(exe)
                           ?? $"PID {g.Key}";
                // Thu theo session đang phát (ưu tiên active); process loopback chỉ gồm con trực tiếp của PID đích.
                var session = g.FirstOrDefault(s => s.IsActive);
                if (session == default)
                    session = g.First();
                // Không thu qua tiến trình cha của chính Suni: con trực tiếp của nó gồm cả Suni → thu lại giọng đọc (TTS) của mình.
                var target = tree.FindCaptureTarget(session.Pid);
                if (target == selfParent)
                    target = session.Pid;
                return new AudioSource(
                    CaptureMode.ProcessLoopback, name, g.Key, exe,
                    g.Select(s => s.IconPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                    IsActive: g.Any(s => s.IsActive), SessionCount: g.Count(),
                    CapturePid: target);
            })
            .OrderByDescending(a => a.IsActive)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase);

            IReadOnlyList<AudioSource> result =
            [
                new AudioSource(
                    WasapiLoopbackCapture.IsProcessLoopbackSupported ? CaptureMode.SystemExcludeSelf : CaptureMode.EndpointLoopback,
                    SystemAudioName, IsActive: true),
                .. apps,
            ];
            return result;
        }, ct);

    internal readonly record struct SessionInfo(int Pid, bool IsActive, string? DisplayName, string? IconPath);

    internal static List<SessionInfo> EnumerateSessions()
    {
        var result = new List<SessionInfo>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, CoreAudio.DeviceStateActive, out var devices) < 0)
                return result;
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) < 0)
                    continue;
                try
                {
                    ReadDeviceSessions(device, result);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(devices);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    private static void ReadDeviceSessions(IMMDevice device, List<SessionInfo> result)
    {
        var iid = CoreAudio.IidAudioSessionManager2;
        if (device.Activate(ref iid, CoreAudio.ClsctxAll, 0, out var obj) < 0 || obj is not IAudioSessionManager2 manager)
            return;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) < 0)
                return;
            sessions.GetCount(out var n);
            for (var j = 0; j < n; j++)
            {
                if (sessions.GetSession(j, out var s) < 0)
                    continue;
                try
                {
                    if (s.IsSystemSoundsSession() == 0 || s.GetProcessId(out var pid) < 0)
                        continue;
                    s.GetState(out var state);
                    if (state == 2) // Expired
                        continue;
                    s.GetDisplayName(out var name);
                    s.GetIconPath(out var icon);
                    result.Add(new SessionInfo((int)pid, state == 1, name, icon));
                }
                finally
                {
                    Marshal.ReleaseComObject(s);
                }
            }
            Marshal.ReleaseComObject(sessions);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static string? DescribeProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var description = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // tiến trình đã thoát hoặc chạy quyền cao hơn
        }
    }

    /// <summary>Gọi COM trên thread MTA riêng, tránh phụ thuộc apartment của thread gọi (UI là STA).</summary>
    private static Task<T> RunMta<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "Suni audio enumerate" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task.WaitAsync(ct);
    }
}
