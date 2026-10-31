using Suni.Asr;
using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Pipeline;

/// <summary>Giữ một ASR worker whisper.cpp; tạo lại khi đổi model/thiết bị hoặc sau khi bị Invalidate.</summary>
public sealed class WhisperWorkerProvider(Func<string> workerPath, Func<string> modelDirectory) : IAsrEngineProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AsrWorkerClient? _client;
    private AsrOptions? _options;

    /// <summary>Runtime thực tế của worker hiện tại ("Vulkan", "Cpu"...), null nếu chưa nạp.</summary>
    public string? Runtime => _client?.Runtime;

    public async Task<IAsrEngine> GetAsync(AsrSettings settings, CancellationToken ct = default)
    {
        var options = new AsrOptions(
            settings.Engine,
            $"ggml-{settings.Model}.bin",
            modelDirectory(),
            settings.Device,
            settings.ComputeType,
            settings.Threads > 0 ? settings.Threads : Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
            settings.BeamSize,
            settings.AudioContext);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _options == options && _client.WorkerProcess is { HasExited: false })
                return _client;

            await DisposeClientAsync().ConfigureAwait(false);
            var client = new AsrWorkerClient(workerPath());
            try
            {
                await client.InitializeAsync(options, ct).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _client = client;
            _options = options;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _options = null;
        if (client is not null)
            _ = client.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return; // app và DI container đều có thể dispose
        await DisposeClientAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private int _disposed;

    private async Task DisposeClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _options = null;
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
    }
}
