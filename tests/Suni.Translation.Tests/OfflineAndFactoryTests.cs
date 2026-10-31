using System.Net;
using Suni.Core.Settings;

namespace Suni.Translation.Tests;

public class OfflineAndFactoryTests
{
    /// <summary>Handler cuối giả: ghi lại request đã thực sự "rời máy".</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("http://127.0.0.1:11434/api/tags")]
    [InlineData("http://192.168.1.96:12121/api/tags")]
    [InlineData("http://ollama-box:11434/api/tags")]
    public async Task OfflineStrict_AllowsLocalAndLan(string url)
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        using var http = new HttpClient(new OfflineStrictHandler { InnerHandler = inner });

        await http.GetAsync(url);

        Assert.Single(inner.Sent);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/chat")]
    [InlineData("http://8.8.8.8:11434/api/tags")]
    public async Task OfflineStrict_BlocksInternet_BeforeAnythingIsSent(string url)
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        using var http = new HttpClient(new OfflineStrictHandler { InnerHandler = inner });

        await Assert.ThrowsAsync<OfflineModeViolationException>(() => http.GetAsync(url));
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task OfflineStrict_CanBeDisabled()
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        using var http = new HttpClient(new OfflineStrictHandler(() => false) { InnerHandler = inner });

        await http.GetAsync("https://api.example.com/");

        Assert.Single(inner.Sent);
    }

    [Theory]
    [InlineData("", "qwen3.5:9b")]
    [InlineData("http://127.0.0.1:11434", "")]
    [InlineData("not a url", "qwen3.5:9b")]
    public async Task Factory_ReturnsNull_WhenNotConfigured(string url, string model)
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        var factory = new OllamaTranslationEngineFactory(new HttpClient(inner));

        Assert.Null(await factory.CreateAsync(new TranslationSettings { OllamaUrl = url, Model = model }));
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task Factory_ChecksThinkingCapability_AndWarmsUpModel()
    {
        var inner = new RecordingHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/show" => Json("""{"capabilities":["completion","thinking"]}"""),
            "/api/chat" => Json("""{"message":{"role":"assistant","content":"{\"translation\":\"Xin chào.\"}"},"done":true}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var factory = new OllamaTranslationEngineFactory(new HttpClient(inner));

        var engine = await factory.CreateAsync(new TranslationSettings { OllamaUrl = "http://127.0.0.1:11434", Model = "qwen3.5:9b" });

        Assert.IsType<OllamaTranslationEngine>(engine);
        for (var i = 0; i < 50 && inner.Sent.Count < 2; i++)
            await Task.Delay(20); // warm-up chạy nền
        Assert.Equal(["/api/show", "/api/chat"], inner.Sent.Select(u => u.AbsolutePath));
    }

    [Fact]
    public async Task Factory_StillCreatesEngine_WhenServerUnreachable()
    {
        var inner = new RecordingHandler(_ => throw new HttpRequestException("refused"));
        var factory = new OllamaTranslationEngineFactory(new HttpClient(inner));

        var engine = await factory.CreateAsync(new TranslationSettings { OllamaUrl = "http://127.0.0.1:11434", Model = "m" });

        Assert.NotNull(engine); // lỗi sẽ được báo theo từng câu và pipeline ngắt mạch
    }
}
