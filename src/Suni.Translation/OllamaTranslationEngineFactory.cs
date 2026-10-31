using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Translation;

/// <summary>
/// Tạo <see cref="OllamaTranslationEngine"/> từ cấu hình: hỏi <c>/api/show</c> để biết model có "thinking"
/// (chỉ khi có mới gửi <c>think:false</c>), rồi nạp sẵn model vào Ollama ở nền (lần gọi đầu có thể mất 5–10 s).
/// </summary>
public sealed class OllamaTranslationEngineFactory(HttpClient http) : ITranslationEngineFactory
{
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarmUpTimeout = TimeSpan.FromSeconds(120);

    public async Task<ITranslationEngine?> CreateAsync(TranslationSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Model) || !OllamaUrlClassifier.TryParse(settings.OllamaUrl, out var uri))
            return null;

        var client = new OllamaClient(http, uri!);
        var supportsThinking = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ShowTimeout);
            supportsThinking = (await client.ShowAsync(settings.Model, timeout.Token).ConfigureAwait(false)).SupportsThinking;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                                   && !ct.IsCancellationRequested)
        {
            // Server chưa sẵn sàng: vẫn tạo engine, lỗi sẽ được báo theo từng câu và ngắt mạch xử lý.
        }

        var engine = new OllamaTranslationEngine(client, new OllamaTranslationOptions(
            settings.Model, TimeSpan.FromSeconds(settings.TimeoutSec), settings.Retry, supportsThinking));
        _ = WarmUpAsync(engine, settings);
        return engine;
    }

    private static async Task WarmUpAsync(OllamaTranslationEngine engine, TranslationSettings settings)
    {
        try
        {
            using var timeout = new CancellationTokenSource(WarmUpTimeout);
            await engine.WarmUpAsync(settings.TargetLang, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // chỉ là tối ưu; lỗi thật sẽ hiện khi dịch câu đầu tiên
        }
    }
}
