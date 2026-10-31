using System.Diagnostics;
using System.Runtime.InteropServices;
using Suni.Audio.Interop;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;

namespace Suni.Audio;

/// <summary>Thống kê để chẩn đoán (Diagnostics, báo cáo spike).</summary>
public sealed record CaptureStats(
    long Frames,
    long Packets,
    long SilentPackets,
    long Discontinuities,
    long TimestampErrors,
    double MaxPacketGapMs,
    double? FirstPacketAfterStartMs);

/// <summary>
/// Thu âm WASAPI loopback viết bằng C# (COM interop):
/// process loopback (include/exclude cây tiến trình) hoặc endpoint loopback.
/// Thread capture riêng (MTA, ưu tiên cao); <see cref="FramesAvailable"/> được gọi trên thread đó.
/// Định dạng đầu ra luôn float32 interleaved — WASAPI tự chuyển đổi (AUTOCONVERTPCM).
/// </summary>
public sealed class WasapiLoopbackCapture : IAudioCapture
{
    private enum Kind { Process, Endpoint }

    private const long BufferDuration100ns = 200_000; // 20 ms, theo mẫu ApplicationLoopback của Microsoft
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly Kind _kind;
    private readonly int _pid;
    private readonly bool _includeTree;
    private readonly string? _deviceId;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private readonly AutoResetEvent _audioEvent = new(false);
    private Process? _watchedProcess;

    private long _frames, _packets, _silentPackets, _discontinuities, _timestampErrors;
    private double _maxGapMs;
    private double? _firstPacketMs;

    private WasapiLoopbackCapture(Kind kind, int pid, bool includeTree, string? deviceId, AudioFormat format)
    {
        _kind = kind;
        _pid = pid;
        _includeTree = includeTree;
        _deviceId = deviceId;
        Format = format;
    }

    /// <summary>Thu riêng một ứng dụng (<paramref name="includeTree"/> = true) hoặc mọi thứ TRỪ nó (false).</summary>
    public static WasapiLoopbackCapture ForProcess(int pid, bool includeTree, int sampleRate = 48_000, int channels = 2) =>
        new(Kind.Process, pid, includeTree, null, new AudioFormat(sampleRate, channels));

    /// <summary>Toàn hệ thống trừ chính ứng dụng này (ADR-04).</summary>
    public static WasapiLoopbackCapture ForSystemExcludingSelf() =>
        ForProcess(Environment.ProcessId, includeTree: false);

    /// <summary>Endpoint loopback; <paramref name="deviceId"/> null = thiết bị phát mặc định.</summary>
    public static WasapiLoopbackCapture ForEndpoint(string? deviceId = null, int sampleRate = 48_000, int channels = 2) =>
        new(Kind.Endpoint, 0, false, deviceId, new AudioFormat(sampleRate, channels));

    public static bool IsProcessLoopbackSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public AudioFormat Format { get; }

    public event PcmFramesHandler? FramesAvailable;

    public event Action<CaptureState, ErrorCode?>? StateChanged;

    public CaptureStats Stats => new(
        Interlocked.Read(ref _frames), Interlocked.Read(ref _packets), Interlocked.Read(ref _silentPackets),
        Interlocked.Read(ref _discontinuities), Interlocked.Read(ref _timestampErrors), _maxGapMs, _firstPacketMs);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_thread is not null)
            throw new InvalidOperationException("Capture đã được khởi động.");
        if (_kind == Kind.Process && !IsProcessLoopbackSupported)
            throw new AudioCaptureException(ErrorCode.CapUnsupportedOs, "Process loopback cần Windows build 20348 trở lên.");

        // Windows vẫn kích hoạt thành công với PID không tồn tại (chỉ trả về im lặng) → phải tự kiểm tra trước.
        WatchTargetProcess();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started))
        {
            Name = "Suni WASAPI capture",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return started.Task.WaitAsync(ct);
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        _audioEvent.Set();
        if (_thread is { } t)
            await Task.Run(() => t.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _watchedProcess?.Dispose();
        _audioEvent.Dispose();
    }

    private void Run(TaskCompletionSource started)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var formatPtr = nint.Zero;
        try
        {
            client = _kind == Kind.Process ? ActivateProcessLoopback() : ActivateEndpoint();

            var wf = WaveFormatEx.Float32(Format.SampleRate, Format.Channels);
            formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(wf, formatPtr, false);

            const uint flags = CoreAudio.StreamFlagsLoopback | CoreAudio.StreamFlagsEventCallback
                             | CoreAudio.StreamFlagsAutoConvertPcm | CoreAudio.StreamFlagsSrcDefaultQuality;
            Check(client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration100ns, 0, formatPtr, 0), "IAudioClient.Initialize");
            Check(client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");

            var iid = CoreAudio.IidAudioCaptureClient;
            Check(client.GetService(ref iid, out var svc), "GetService(IAudioCaptureClient)");
            capture = (IAudioCaptureClient)svc!;

            Check(client.Start(), "IAudioClient.Start");
            started.TrySetResult();
            StateChanged?.Invoke(CaptureState.Running, null);

            Loop(capture);
            StateChanged?.Invoke(CaptureState.Stopped, null);
        }
        catch (AudioCaptureException ex)
        {
            if (!started.TrySetException(ex))
                StateChanged?.Invoke(ex.Code == ErrorCode.CapDeviceChanged ? CaptureState.DeviceChanged : CaptureState.Error, ex.Code);
        }
        catch (Exception ex)
        {
            var wrapped = new AudioCaptureException(ErrorCode.CapActivationFailed, ex.Message, ex);
            if (!started.TrySetException(wrapped))
                StateChanged?.Invoke(CaptureState.Error, ErrorCode.CapActivationFailed);
        }
        finally
        {
            try { client?.Stop(); } catch { /* đã hỏng thì bỏ qua */ }
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (formatPtr != 0) Marshal.FreeHGlobal(formatPtr);
        }
    }

    private void Loop(IAudioCaptureClient capture)
    {
        var silence = Array.Empty<float>();
        var startTicks = Stopwatch.GetTimestamp();
        var lastPacketTicks = 0L;
        var channels = Format.Channels;

        while (!_stopRequested)
        {
            // Process loopback có thể không phát event khi nguồn im lặng → timeout ngắn để vẫn kiểm tra stop.
            _audioEvent.WaitOne(100);

            while (!_stopRequested)
            {
                Check(capture.GetNextPacketSize(out var packetFrames), "GetNextPacketSize");
                if (packetFrames == 0)
                    break;

                Check(capture.GetBuffer(out var data, out var frames, out var bufferFlags, out _, out _), "GetBuffer");
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    if (lastPacketTicks == 0)
                        _firstPacketMs = Stopwatch.GetElapsedTime(startTicks, now).TotalMilliseconds;
                    else
                        _maxGapMs = Math.Max(_maxGapMs, Stopwatch.GetElapsedTime(lastPacketTicks, now).TotalMilliseconds);
                    lastPacketTicks = now;

                    var samples = (int)frames * channels;
                    var discontinuity = (bufferFlags & CoreAudio.BufferFlagsDataDiscontinuity) != 0;
                    if (discontinuity) Interlocked.Increment(ref _discontinuities);
                    if ((bufferFlags & CoreAudio.BufferFlagsTimestampError) != 0) Interlocked.Increment(ref _timestampErrors);

                    ReadOnlySpan<float> span;
                    if ((bufferFlags & CoreAudio.BufferFlagsSilent) != 0)
                    {
                        Interlocked.Increment(ref _silentPackets);
                        if (silence.Length < samples)
                            silence = new float[samples];
                        span = silence.AsSpan(0, samples);
                    }
                    else
                    {
                        unsafe { span = new ReadOnlySpan<float>((void*)data, samples); }
                    }

                    // Sample clock (ADR-10): đếm frame đã nhận, không dùng device position.
                    FramesAvailable?.Invoke(span, Interlocked.Read(ref _frames), discontinuity);
                    Interlocked.Add(ref _frames, frames);
                    Interlocked.Increment(ref _packets);
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }
    }

    private IAudioClient ActivateProcessLoopback()
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = (uint)_pid,
            ProcessLoopbackMode = _includeTree ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree,
        };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            var prop = new PropVariantBlob
            {
                Vt = PropVariantBlob.VtBlob,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            };
            var iid = CoreAudio.IidAudioClient;
            var handler = new ActivationHandler();
            Check(CoreAudio.ActivateAudioInterfaceAsync(CoreAudio.VirtualProcessLoopbackDevice, ref iid, ref prop, handler, out var op),
                  "ActivateAudioInterfaceAsync");
            try
            {
                if (!handler.Completed.Wait(ActivationTimeout))
                    throw new AudioCaptureException(ErrorCode.CapActivationFailed, "Hết thời gian chờ kích hoạt process loopback.");
                Check(handler.ActivateResult, "ActivateCompleted");
                return (IAudioClient)(handler.Interface ?? throw new AudioCaptureException(ErrorCode.CapActivationFailed, "Không nhận được IAudioClient."));
            }
            finally
            {
                Marshal.ReleaseComObject(op);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private IAudioClient ActivateEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            IMMDevice device;
            if (_deviceId is null)
                Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device), "GetDefaultAudioEndpoint");
            else
                Check(enumerator.GetDevice(_deviceId, out device), "GetDevice");

            var iid = CoreAudio.IidAudioClient;
            Check(device.Activate(ref iid, CoreAudio.ClsctxAll, 0, out var obj), "IMMDevice.Activate");
            Marshal.ReleaseComObject(device);
            return (IAudioClient)obj!;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void WatchTargetProcess()
    {
        if (_kind != Kind.Process || !_includeTree)
            return;

        Process process;
        try
        {
            process = Process.GetProcessById(_pid);
        }
        catch (ArgumentException)
        {
            throw new AudioCaptureException(ErrorCode.CapProcessNotFound, $"Không tìm thấy tiến trình PID {_pid}.");
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => StateChanged?.Invoke(CaptureState.SourceLost, ErrorCode.CapProcessNotFound);
            _watchedProcess = process;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Không mở được handle (tiến trình quyền cao hơn): vẫn thu, chỉ mất cảnh báo SourceLost.
            process.Dispose();
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr >= 0)
            return;
        var code = hr switch
        {
            CoreAudio.AudclntEDeviceInvalidated => ErrorCode.CapDeviceChanged,
            _ => ErrorCode.CapActivationFailed,
        };
        throw new AudioCaptureException(code, $"{what} thất bại: HRESULT 0x{hr:X8}", Marshal.GetExceptionForHR(hr));
    }

    /// <summary>Callback hoàn tất kích hoạt; phải là agile object (CCW .NET mặc định free-threaded).</summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new();
        public int ActivateResult { get; private set; }
        public object? Interface { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out var hr, out var obj);
                ActivateResult = hr;
                Interface = obj;
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
            return 0;
        }
    }
}

public sealed class AudioCaptureException(ErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorCode Code { get; } = code;
}
