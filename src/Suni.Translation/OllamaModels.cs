using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suni.Translation;

public sealed record OllamaModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);

internal sealed record TagsResponse([property: JsonPropertyName("models")] List<OllamaModel>? Models);

public sealed record OllamaModelInfo([property: JsonPropertyName("capabilities")] List<string>? Capabilities)
{
    public bool SupportsThinking => Capabilities?.Contains("thinking") == true;
}

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record ChatRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<ChatMessage> Messages { get; init; }
    [JsonPropertyName("stream")] public bool Stream { get; init; }

    /// <summary>JSON Schema cho structured output.</summary>
    [JsonPropertyName("format")] public JsonElement? Format { get; init; }

    /// <summary>Chỉ gửi khi model có capability "thinking" (SRS 5.2).</summary>
    [JsonPropertyName("think")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Think { get; init; }

    [JsonPropertyName("keep_alive")] public string KeepAlive { get; init; } = "30m";
    [JsonPropertyName("options")] public Dictionary<string, object>? Options { get; init; }
}

public sealed record ChatResponse(
    [property: JsonPropertyName("message")] ChatMessage? Message,
    [property: JsonPropertyName("done")] bool Done);

public sealed record ConnectionCheck(bool Ok, int RoundTripMs, int ModelCount, string? Error);
