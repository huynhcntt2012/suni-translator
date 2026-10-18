# Windows audio bridge

System endpoint capture uses PyAudioWPatch/PortAudio WASAPI in `engine/audio.py`.

Per-process capture is implemented by `ProcessLoopback.cs`: an isolated x64 .NET Framework bridge calling Windows Core Audio COM APIs (ActivateAudioInterfaceAsync, process-tree INCLUDE, IAudioCaptureClient). It does not capture a microphone, inject into the target, change audio routing, or mute unrelated apps. Build with `scripts/build_audio.ps1`; Windows 11 already includes the Framework runtime/compiler used here. This replaces the initially proposed custom C++ binary while retaining real native Windows API capture.

STDIN newline/EOF stops the bridge. STDOUT is binary SLP1 PCM protocol; STDERR holds errors. Native activation times out after 10 seconds; Python Stop also enforces bridge shutdown. See `docs/ARCHITECTURE.md` for wire format.

Microsoft references:

- https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback
- https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
- https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/mmdeviceapi.h

Voice Replacement is still unavailable and must not be simulated by volume muting.
