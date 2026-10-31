using System.Runtime.InteropServices;

namespace Suni.Audio.Interop;

// Khai báo COM tối thiểu cho WASAPI (mmdeviceapi.h, audioclient.h, audiopolicy.h, audioclientactivationparams.h).
// Thứ tự method trong mỗi interface PHẢI khớp vtable của header Windows SDK.

internal static class CoreAudio
{
    public const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";

    public const uint ClsctxAll = 0x17;
    public const uint DeviceStateActive = 0x1;

    public const uint StreamFlagsLoopback = 0x0002_0000;
    public const uint StreamFlagsEventCallback = 0x0004_0000;
    public const uint StreamFlagsSrcDefaultQuality = 0x0800_0000;
    public const uint StreamFlagsAutoConvertPcm = 0x8000_0000;

    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;

    public const int AudclntEDeviceInvalidated = unchecked((int)0x8889_0004);
    public const int AudclntEServiceNotRunning = unchecked((int)0x8889_0010);

    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }

internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

internal enum AudioClientShareMode { Shared = 0, Exclusive = 1 }

internal enum AudioClientActivationType { Default = 0, ProcessLoopback = 1 }

internal enum ProcessLoopbackMode { IncludeTargetProcessTree = 0, ExcludeTargetProcessTree = 1 }

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

/// <summary>PROPVARIANT với VT_BLOB, layout x64 (24 byte).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlob
{
    public const ushort VtBlob = 65;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public uint BlobSize;
    [FieldOffset(16)] public nint BlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public const ushort FormatIeeeFloat = 3;

    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize;

    public static WaveFormatEx Float32(int sampleRate, int channels) => new()
    {
        FormatTag = FormatIeeeFloat,
        Channels = (ushort)channels,
        SamplesPerSec = (uint)sampleRate,
        BitsPerSample = 32,
        BlockAlign = (ushort)(channels * 4),
        AvgBytesPerSec = (uint)(sampleRate * channels * 4),
        CbSize = 0,
    };
}

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject;

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(AudioClientShareMode shareMode, uint streamFlags, long hnsBufferDuration,
                                 long hnsPeriodicity, nint format, nint audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long hnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, nint format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint eventHandle);
    [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint numFramesToRead, out uint flags,
                                out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate([In] ref Guid iid, uint clsCtx, nint activationParams,
                               [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(nint sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(nint sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint duckNotification);
    [PreserveSig] int UnregisterDuckNotification(nint duckNotification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state); // 0 Inactive, 1 Active, 2 Expired
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, nint eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, nint eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam([In] ref Guid groupingParam, nint eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(nint client);
    [PreserveSig] int UnregisterAudioSessionNotification(nint client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession(); // S_OK = system sounds, S_FALSE = không
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
