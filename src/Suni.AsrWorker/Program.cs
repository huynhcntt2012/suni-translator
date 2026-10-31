using System.IO.Pipes;
using Suni.Asr.Protocol;
using Suni.AsrWorker;

// ASR worker (engine whisper.cpp). App spawn process này với SUNI_ASR_PIPE và SUNI_ASR_TOKEN (SDD 4.2).
// Xử lý tuần tự: model không thread-safe và CPU đã bão hoà khi decode.

var pipeName = Environment.GetEnvironmentVariable(PipeNames.PipeEnvironmentVariable);
var token = Environment.GetEnvironmentVariable(PipeNames.TokenEnvironmentVariable);
if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("Thiếu SUNI_ASR_PIPE / SUNI_ASR_TOKEN. Worker chỉ được khởi chạy bởi Suni.App.");
    return 2;
}

await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(10_000);
await FrameCodec.WriteJsonAsync(pipe, MessageType.Hello,
    new HelloMessage(token, "whisper.cpp", typeof(WhisperCppEngine).Assembly.GetName().Version?.ToString() ?? "0", ["cpu", "vulkan"]));

using var engine = new WhisperCppEngine();
TranscribeMessage? pending = null;

while (await FrameCodec.ReadAsync(pipe) is { } frame)
{
    switch (frame.Type)
    {
        case MessageType.Ping:
            await FrameCodec.WriteAsync(pipe, MessageType.Pong, ReadOnlyMemory<byte>.Empty);
            break;

        case MessageType.Init:
            try
            {
                var loadMs = engine.Load(frame.ReadJson<InitMessage>());
                await FrameCodec.WriteJsonAsync(pipe, MessageType.InitOk, new InitOkMessage(loadMs, engine.Runtime));
            }
            catch (Exception ex)
            {
                await FrameCodec.WriteJsonAsync(pipe, MessageType.InitError,
                    new ErrorMessage(null, (ex as WorkerException)?.Code ?? "ASR_MODEL_MISSING", ex.Message));
            }
            break;

        case MessageType.Transcribe:
            pending = frame.ReadJson<TranscribeMessage>();
            break;

        case MessageType.Pcm when pending is not null:
            var request = pending;
            pending = null;
            try
            {
                var pcm = frame.ReadPcm();
                if (pcm.Length != request.SampleCount)
                    throw new WorkerException("ASR_BAD_REQUEST", $"Nhận {pcm.Length} mẫu, chờ {request.SampleCount}.");
                await FrameCodec.WriteJsonAsync(pipe, MessageType.Result, engine.Transcribe(request, pcm));
            }
            catch (Exception ex)
            {
                await FrameCodec.WriteJsonAsync(pipe, MessageType.Error,
                    new ErrorMessage(request.SegmentId, (ex as WorkerException)?.Code ?? "ASR_FAILED", ex.Message));
            }
            break;

        case MessageType.Shutdown:
            return 0;
    }
}
return 0;
