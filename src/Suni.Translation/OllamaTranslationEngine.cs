using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Translation;

public sealed record OllamaTranslationOptions(string Model, TimeSpan Timeout, int MaxRetries, bool SupportsThinking);

/// <summary>
/// Dịch một segment qua Ollama, có timeout và retry exponential backoff (SDD 5.4).
/// Chỉ retry lỗi tạm thời (mạng, timeout, 5xx); không retry 4xx hay output sai schema.
/// </summary>
public sealed class OllamaTranslationEngine(IOllamaClient client, OllamaTranslationOptions options, TimeProvider? time = null)
    : ITranslationEngine
{
    private static readonly JsonElement TranslationSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"translation":{"type":"string"}},"required":["translation"]}
        """).RootElement.Clone();

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var chat = BuildRequest(request);
        ErrorCode error = ErrorCode.LlmUnreachable;

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], _time, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            try
            {
                var res = await client.ChatAsync(chat, timeout.Token).ConfigureAwait(false);
                return ResponseParser.TryParse(res.Message?.Content, request.Text, out var translation)
                    ? new TranslationResult(request.SegmentId, translation, null, (int)sw.ElapsedMilliseconds)
                    : Fail(request, ErrorCode.LlmBadOutput, sw);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = ErrorCode.LlmTimeout;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail(request, ErrorCode.LlmModelNotFound, sw);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && (int)code is >= 400 and < 500)
            {
                return Fail(request, ErrorCode.LlmBadOutput, sw);
            }
            catch (HttpRequestException)
            {
                error = ErrorCode.LlmUnreachable;
            }
            catch (JsonException)
            {
                return Fail(request, ErrorCode.LlmBadOutput, sw);
            }
        }
        return Fail(request, error, sw);
    }

    /// <summary>Nạp model vào bộ nhớ Ollama bằng một yêu cầu rất ngắn (không retry, không tính vào thống kê).</summary>
    public Task WarmUpAsync(string targetLang, CancellationToken ct) =>
        client.ChatAsync(BuildRequest(new TranslationRequest("warmup", "Hello.", "en", targetLang, TranslationStyle.Natural, [], [])), ct);

    public ChatRequest BuildRequest(TranslationRequest request) => new()
    {
        Model = options.Model,
        Stream = false,
        Format = TranslationSchema,
        Think = options.SupportsThinking ? false : null,
        Options = new() { ["temperature"] = 0.1, ["num_predict"] = 256 },
        Messages =
        [
            new ChatMessage("system", PromptBuilder.BuildSystem(request)),
            new ChatMessage("user", PromptBuilder.BuildUser(request)),
        ],
    };

    private static TranslationResult Fail(TranslationRequest r, ErrorCode code, Stopwatch sw) =>
        new(r.SegmentId, null, code, (int)sw.ElapsedMilliseconds);
}
