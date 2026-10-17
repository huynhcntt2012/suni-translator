# Windows audio boundary

Current executable uses the native WASAPI backend supplied by PyAudioWPatch/PortAudio through `engine/audio.py`. No custom C++ process-loopback implementation is shipped in 0.1. Do not mark per-process capture or isolated Voice Replacement complete based on this directory.

Future native worker contract: target PID, Windows build capability probe, endpoint IDs, explicit routing state, monotonic timestamps, 16 kHz float PCM over named pipe, bounded buffers, endpoint-change/error messages, and idempotent restore. Replacement requires tested virtual endpoint isolation rather than volume muting.
