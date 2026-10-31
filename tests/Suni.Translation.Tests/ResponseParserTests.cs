namespace Suni.Translation.Tests;

public class ResponseParserTests
{
    private const string Source = "この機能の使い方を説明します。";

    [Theory]
    [InlineData("{\"translation\":\"Tôi sẽ giải thích cách sử dụng tính năng này.\"}")]
    [InlineData("```json\n{\"translation\":\"Tôi sẽ giải thích cách sử dụng tính năng này.\"}\n```")]
    [InlineData("<think>người dùng muốn dịch...</think>{\"translation\":\"Tôi sẽ giải thích cách sử dụng tính năng này.\"}")]
    [InlineData("Đây là bản dịch: {\"translation\":\"Tôi sẽ giải thích cách sử dụng tính năng này.\"}")]
    [InlineData("  {\"translation\":\"  Tôi sẽ giải thích cách sử dụng tính năng này.  \"}  ")]
    public void ValidOrRecoverableOutput_IsParsed(string content)
    {
        Assert.True(ResponseParser.TryParse(content, Source, out var t));
        Assert.Equal("Tôi sẽ giải thích cách sử dụng tính năng này.", t);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tôi sẽ giải thích cách sử dụng tính năng này.")] // không phải JSON
    [InlineData("{\"text\":\"Xin chào\"}")]                          // sai key
    [InlineData("{\"translation\":\"\"}")]                           // rỗng
    [InlineData("{\"translation\":123}")]                            // sai kiểu
    [InlineData("[\"Xin chào\"]")]
    public void InvalidOutput_IsRejected(string? content)
    {
        Assert.False(ResponseParser.TryParse(content, Source, out var t));
        Assert.Null(t);
    }

    [Fact]
    public void OverlyLongExplanation_IsRejected()
    {
        var essay = new string('x', Source.Length * 4 + 41);
        Assert.False(ResponseParser.TryParse($"{{\"translation\":\"{essay}\"}}", Source, out _));
    }
}
