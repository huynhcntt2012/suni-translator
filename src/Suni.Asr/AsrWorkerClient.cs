using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Suni.Asr.Protocol;
using Suni.Core.Abstractions;

namespace Suni.Asr;

/// <summary>
/// <see cref="IAsrEngine"/> chạy trong process worker riêng (SDD 4.2): spawn worker, tạo named pipe
/// chỉ user hiện tại kết nối được, xác thực token phiên, rồi gửi từng đoạn PCM tuần tự.
/// Khởi động lại khi worker chết là việc của WorkerSupervisor (P1).
/// </summary>
public sealed class AsrWorkerClient : IAsrEngine
{
    private readonly string _workerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private NamedPipeServerStream? _pipe;

    public AsrWorkerClient(string workerPath) => _workerPath = workerPath;

    public HelloMessage? Hello { get; private set; }

    public string? Runtime { get; private set; }

    /// <summary>Process worker (để đo RAM/CPU trong Diagnostics).</summary>
    public Process? WorkerProcess => _process;

    public async Task<AsrInfo> InitializeAsync(AsrOptions options, CancellationToken ct = default)
    {
        await StartWorkerAsync(ct).ConfigureAwait(false);

        var init = new InitMessage(options.Model, options.ModelDirectory, options.Device, options.ComputeType,
                                   options.Threads, options.BeamSize, options.AudioContext);
        await FrameCodec.WriteJsonAsync(_pipe!, MessageType.Init, init, ct).ConfigureAwait(false);

        var reply = await ReadAsync(ct).ConfigureAwait(false);
        switch (reply.Type)
        {
            case MessageType.InitOk:
                var ok = reply.ReadJson<InitOkMessage>();
                Runtime = ok.Runtime;
                return new AsrInfo(Hello!.Engine, Hello.Version, options.Model, ok.LoadMs);
            case MessageType.InitError:
                var err = reply.ReadJson<ErrorMessage>();
                throw new AsrWorkerException(err.Code, err.Message);
            default:
                throw new AsrWorkerException("ASR_PROTOCOL", $"Phản hồi không mong đợi: {reply.Type}");
        }
    }

    public async Task<AsrResult> TranscribeAsync(AsrRequest request, CancellationToken ct = default)
    {
        if (_pipe is null)
            throw new InvalidOperationException("Chưa khởi tạo worker.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var msg = new TranscribeMessage(request.SegmentId, request.Language, request.InitialPrompt, request.Pcm16kMono.Length);
            await FrameCodec.WriteJsonAsync(_pipe, MessageType.Transcribe, msg, ct).ConfigureAwait(false);
            await FrameCodec.WritePcmAsync(_pipe, request.Pcm16kMono, ct).ConfigureAwait(false);

            var reply = await ReadAsync(ct).ConfigureAwait(false);
            if (reply.Type == MessageType.Error)
            {
                var err = reply.ReadJson<ErrorMessage>();
                throw new AsrWorkerException(err.Code, err.Message);
            }
            if (reply.Type != MessageType.Result)
                throw new AsrWorkerException("ASR_PROTOCOL", $"Phản hồi không mong đợi: {reply.Type}");

            var r = reply.ReadJson<ResultMessage>();
            if (r.SegmentId != request.SegmentId)
                throw new AsrWorkerException("ASR_PROTOCOL", $"Kết quả lệch segment: {r.SegmentId} ≠ {request.SegmentId}");
            return new AsrResult(r.SegmentId, r.Text, r.Language, r.LanguageProbability, [],
                                 r.AvgProbability, r.NoSpeechProbability, r.ElapsedMs);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("Không tìm thấy ASR worker.", _workerPath);

        var pipeName = PipeNames.ForSession(Guid.NewGuid());
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                          PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var psi = new ProcessStartInfo(_workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
        };
        psi.Environment[PipeNames.PipeEnvironmentVariable] = pipeName;
        psi.Environment[PipeNames.TokenEnvironmentVariable] = token;
        _process = Process.Start(psi) ?? throw new AsrWorkerException("ASR_WORKER_CRASHED", "Không khởi động được worker.");
        _process.ErrorDataReceived += (_, _) => { }; // tránh đầy buffer stderr
        _process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

        var hello = await ReadAsync(timeout.Token).ConfigureAwait(false);
        if (hello.Type != MessageType.Hello)
            throw new AsrWorkerException("ASR_PROTOCOL", "Worker không gửi hello.");
        Hello = hello.ReadJson<HelloMessage>();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hello.Token), Encoding.ASCII.GetBytes(token)))
            throw new AsrWorkerException("ASR_PROTOCOL", "Token worker không khớp.");
    }

    private async Task<Frame> ReadAsync(CancellationToken ct) =>
        await FrameCodec.ReadAsync(_pipe!, ct).ConfigureAwait(false)
        ?? throw new AsrWorkerException("ASR_WORKER_CRASHED", $"Worker đã đóng pipe (exit code {(_process is { HasExited: true } p ? p.ExitCode : null)}).");

    public async ValueTask DisposeAsync()
    {
        if (_pipe is { IsConnected: true })
        {
            try
            {
                await FrameCodec.WriteAsync(_pipe, MessageType.Shutdown, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // worker đã chết
            }
        }
        if (_process is { HasExited: false })
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        _process?.Dispose();
        if (_pipe is not null)
            await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

public sealed class AsrWorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
