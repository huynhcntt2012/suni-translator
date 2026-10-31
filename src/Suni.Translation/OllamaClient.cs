using System.Diagnostics;
using System.Net.Http.Json;

namespace Suni.Translation;

public interface IOllamaClient
{
    Uri BaseUri { get; }
    Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct = default);
    Task<OllamaModelInfo> ShowAsync(string model, CancellationToken ct = default);
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
    Task<ConnectionCheck> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>Client HTTP cho Ollama: GET /api/tags, POST /api/show, POST /api/chat.</summary>
public sealed class OllamaClient(HttpClient http, Uri baseUri) : IOllamaClient
{
    public Uri BaseUri { get; } = baseUri;

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct = default)
    {
        var tags = await http.GetFromJsonAsync<TagsResponse>(new Uri(BaseUri, "/api/tags"), ct).ConfigureAwait(false);
        return tags?.Models ?? [];
    }

    public async Task<OllamaModelInfo> ShowAsync(string model, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(new Uri(BaseUri, "/api/show"), new { model }, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<OllamaModelInfo>(ct).ConfigureAwait(false) ?? new OllamaModelInfo(null);
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(new Uri(BaseUri, "/api/chat"), request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ChatResponse>(ct).ConfigureAwait(false)
               ?? throw new HttpRequestException("Ollama trả về body rỗng.");
    }

    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var models = await ListModelsAsync(ct).ConfigureAwait(false);
            return new ConnectionCheck(true, (int)sw.ElapsedMilliseconds, models.Count, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new ConnectionCheck(false, (int)sw.ElapsedMilliseconds, 0, ex.Message);
        }
    }
}
