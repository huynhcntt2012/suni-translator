using System.Runtime.InteropServices;

namespace Suni.Audio.Interop;

public readonly record struct ProcessEntry(int Pid, int ParentPid, string ExeName);

/// <summary>Ảnh chụp cây tiến trình (Toolhelp32) để gom audio session về tiến trình gốc (SDD 3.2).</summary>
public sealed class ProcessTree
{
    private readonly Dictionary<int, ProcessEntry> _byPid;

    public ProcessTree(IEnumerable<ProcessEntry> entries) =>
        _byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());

    public ProcessEntry? Get(int pid) => _byPid.TryGetValue(pid, out var e) ? e : null;

    /// <summary>
    /// Đi lên cha chừng nào cha còn cùng tên exe (vd. utility process của Chrome → chrome.exe gốc).
    /// So tên exe để tránh nhầm khi PID cha đã bị hệ điều hành tái sử dụng.
    /// </summary>
    public int FindRoot(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var current))
            return pid;

        var visited = new HashSet<int> { pid };
        while (_byPid.TryGetValue(current.ParentPid, out var parent)
               && visited.Add(parent.Pid)
               && string.Equals(parent.ExeName, current.ExeName, StringComparison.OrdinalIgnoreCase))
            current = parent;
        return current.Pid;
    }

    /// <summary>
    /// PID nên truyền cho process loopback để thu tiến trình đang phát <paramref name="sessionPid"/>:
    /// cha trực tiếp nếu cùng tên exe (bắt được cả các tiến trình anh em, vd. utility process của Chrome), không thì chính nó.
    /// Không leo cao hơn: INCLUDE_TARGET_PROCESS_TREE chỉ gồm tiến trình đích và CON TRỰC TIẾP — thu tiến trình ông
    /// cho ra im lặng (đo thực tế, xem docs/spikes/P0-audio-capture.md).
    /// </summary>
    public int FindCaptureTarget(int sessionPid)
    {
        if (_byPid.TryGetValue(sessionPid, out var e)
            && _byPid.TryGetValue(e.ParentPid, out var parent)
            && parent.Pid != sessionPid
            && string.Equals(parent.ExeName, e.ExeName, StringComparison.OrdinalIgnoreCase))
            return parent.Pid;
        return sessionPid;
    }

    public bool IsSelfOrDescendantOf(int pid, int ancestorPid)
    {
        var visited = new HashSet<int>();
        for (var p = pid; visited.Add(p); )
        {
            if (p == ancestorPid)
                return true;
            if (!_byPid.TryGetValue(p, out var e) || e.ParentPid == 0)
                return false;
            p = e.ParentPid;
        }
        return false;
    }

    public static ProcessTree Snapshot()
    {
        var entries = new List<ProcessEntry>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle)
            return new ProcessTree(entries);
        try
        {
            var pe = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            for (var ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
                entries.Add(new ProcessEntry((int)pe.ProcessId, (int)pe.ParentProcessId, pe.ExeFile));
        }
        finally
        {
            CloseHandle(snap);
        }
        return new ProcessTree(entries);
    }

    private const uint Th32csSnapProcess = 0x2;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
