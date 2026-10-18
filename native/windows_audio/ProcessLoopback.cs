// Suni process-loopback bridge. Uses Windows Core Audio, no driver or injection.
// ABI references: Microsoft win32metadata mmdeviceapi/audioclient headers.
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICompletion {
    [PreserveSig] int ActivateCompleted(IActivation operation);
}
[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivation {
    [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}
[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgile {}
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient {
    [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, ref WaveFormat format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int mode, ref WaveFormat format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICaptureClient {
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
[StructLayout(LayoutKind.Sequential, Pack=2)]
public struct WaveFormat {
    public ushort Tag, Channels;
    public uint Rate, BytesPerSecond;
    public ushort BlockAlign, Bits, Extra;
}
[StructLayout(LayoutKind.Sequential)]
public struct ActivationParams { public uint Type, Pid, Mode; }
// PROPVARIANT: union at offset 8; BLOB pointer aligned to offset 16 on x64.
[StructLayout(LayoutKind.Explicit, Size=24)]
public struct PropVariant {
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint Size;
    [FieldOffset(16)] public IntPtr Data;
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class Completion : ICompletion, IAgile {
    public readonly ManualResetEvent Completed = new ManualResetEvent(false);
    public int Result = unchecked((int)0x80004005);
    public IAudioClient Client;
    public int ActivateCompleted(IActivation operation) {
        try {
            object instance;
            int call = operation.GetActivateResult(out Result, out instance);
            if (call < 0) Result = call;
            if (Result >= 0) Client = (IAudioClient)instance;
        } catch (Exception ex) { Result = Marshal.GetHRForException(ex); }
        finally { Completed.Set(); }
        return 0;
    }
}
public static class Program {
    [DllImport("Mmdevapi.dll", ExactSpelling=true, CharSet=CharSet.Unicode)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, ref PropVariant parameters, ICompletion completion, out IActivation operation);
    static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [MTAThread]
    public static int Main(string[] args) {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: SuniProcessLoopback.exe <pid>"); return 2; }
        uint pid;
        if (!uint.TryParse(args[0], out pid) || pid == 0) { Console.Error.WriteLine("Invalid PID"); return 2; }
        IAudioClient client = null;
        ICaptureClient capture = null;
        IActivation operation = null;
        Completion completion = new Completion();
        IntPtr blob = Marshal.AllocHGlobal(12);
        bool started = false;
        string stage = "activation";
        // EOF also stops capture if the Python parent crashes or disconnects.
        Thread input = new Thread(delegate() {
            try { Console.OpenStandardInput().ReadByte(); } catch {}
            StopEvent.Set();
        });
        input.IsBackground = true;
        input.Start();
        try {
            Process target = Process.GetProcessById((int)pid);
            ActivationParams parameters = new ActivationParams { Type=1, Pid=pid, Mode=0 };
            Marshal.StructureToPtr(parameters, blob, false);
            PropVariant variant = new PropVariant { Type=65, Size=12, Data=blob };
            Guid iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, completion, out operation));
            int activated = WaitHandle.WaitAny(new WaitHandle[] { completion.Completed, StopEvent }, 10000);
            if (activated == 1) return 0;
            if (activated == WaitHandle.WaitTimeout) throw new TimeoutException("Process loopback activation timed out");
            Check(completion.Result);
            client = completion.Client;
            stage = "initialize";
            WaveFormat format = new WaveFormat { Tag=1, Channels=2, Rate=44100, Bits=16, BlockAlign=4, BytesPerSecond=176400, Extra=0 };
            // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM
            Check(client.Initialize(0, 0x80060000, 0, 0, ref format, IntPtr.Zero));
            Guid captureIid = typeof(ICaptureClient).GUID;
            object service;
            Check(client.GetService(ref captureIid, out service));
            capture = (ICaptureClient)service;
            using (AutoResetEvent samples = new AutoResetEvent(false))
            using (BinaryWriter output = new BinaryWriter(Console.OpenStandardOutput())) {
                Check(client.SetEventHandle(samples.SafeWaitHandle.DangerousGetHandle()));
                Check(client.Start());
                started = true;
                output.Write(new byte[] { 83, 76, 80, 49 }); // SLP1
                output.Write(format.Rate); output.Write(format.Channels); output.Write(format.Bits);
                output.Flush();
                stage = "capture";
                WaitHandle[] handles = new WaitHandle[] { StopEvent, samples };
                while (!StopEvent.WaitOne(0)) {
                    if (target.HasExited) throw new InvalidOperationException("Target process exited");
                    int ready = WaitHandle.WaitAny(handles, 500);
                    if (ready == 0) break;
                    if (ready == WaitHandle.WaitTimeout) continue;
                    uint pending;
                    Check(capture.GetNextPacketSize(out pending));
                    while (pending > 0 && !StopEvent.WaitOne(0)) {
                        IntPtr data; uint frames, flags; ulong position, qpc;
                        Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc));
                        try {
                            byte[] pcm = new byte[checked((int)frames * 4)];
                            if ((flags & 2) == 0 && data != IntPtr.Zero) Marshal.Copy(data, pcm, 0, pcm.Length);
                            output.Write(frames); output.Write(qpc); output.Write(flags); output.Write(pcm);
                            output.Flush();
                        } finally { Check(capture.ReleaseBuffer(frames)); }
                        Check(capture.GetNextPacketSize(out pending));
                    }
                }
            }
            return 0;
        } catch (IOException) { return 0; } // Closed pipe: parent gone/stopped.
        catch (Exception ex) { Console.Error.WriteLine(stage + ": " + ex.Message + " (0x" + Marshal.GetHRForException(ex).ToString("X8") + ")"); return 1; }
        finally {
            if (started && client != null) client.Stop();
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            // Windows retains the callback if activation has not completed.
            // Keep its managed state alive through this process's lifetime.
            GC.KeepAlive(completion); GC.KeepAlive(operation);
            Marshal.FreeHGlobal(blob);
        }
    }
}
