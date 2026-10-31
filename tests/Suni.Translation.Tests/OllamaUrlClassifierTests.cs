using Suni.Core.Domain;

namespace Suni.Translation.Tests;

public class OllamaUrlClassifierTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434", OllamaEndpointKind.Local)]
    [InlineData("http://localhost:11434", OllamaEndpointKind.Local)]
    [InlineData("http://[::1]:11434", OllamaEndpointKind.Local)]
    [InlineData("http://192.168.1.96:12121", OllamaEndpointKind.Lan)]
    [InlineData("http://10.0.0.5:11434", OllamaEndpointKind.Lan)]
    [InlineData("http://172.20.1.1:11434", OllamaEndpointKind.Lan)]
    [InlineData("http://ollama-server:11434", OllamaEndpointKind.Lan)]
    [InlineData("http://gpu-box.local:11434", OllamaEndpointKind.Lan)]
    [InlineData("http://172.32.0.1:11434", OllamaEndpointKind.Other)]
    [InlineData("https://ollama.example.com", OllamaEndpointKind.Other)]
    [InlineData("http://8.8.8.8:11434", OllamaEndpointKind.Other)]
    public void Classify(string url, OllamaEndpointKind expected)
    {
        Assert.True(OllamaUrlClassifier.TryParse(url, out var uri));
        Assert.Equal(expected, OllamaUrlClassifier.Classify(uri!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.96:12121")]
    [InlineData("ftp://192.168.1.96")]
    [InlineData("not a url")]
    public void TryParse_RejectsInvalid(string? input) => Assert.False(OllamaUrlClassifier.TryParse(input, out _));

    [Fact]
    public void TryParse_TrimsTrailingSlash()
    {
        Assert.True(OllamaUrlClassifier.TryParse(" http://127.0.0.1:11434/ ", out var uri));
        Assert.Equal("http://127.0.0.1:11434/", uri!.ToString());
        Assert.Equal("/api/tags", new Uri(uri, "/api/tags").AbsolutePath);
    }
}
