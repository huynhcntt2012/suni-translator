namespace Suni.Asr.Tests;

public class AsrResultFilterTests
{
    [Theory]
    [InlineData("(upbeat music)")]          // whisper tiny với nhạc (benchmark P0)
    [InlineData("[BLANK_AUDIO]")]           // whisper với im lặng
    [InlineData("♪ ♪")]
    [InlineData("♪ la la la ♪")]
    [InlineData("*laughs*")]
    [InlineData("（拍手）")]
    [InlineData("【音楽】")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("Thanks for watching!")]
    [InlineData("ご視聴ありがとうございました")]
    [InlineData("Cảm ơn các bạn đã theo dõi.")]
    [InlineData("go go go go go go")]
    [InlineData("I'm sorry. I'm sorry. I'm sorry. I'm sorry.")]
    public void NonSpeechOrHallucination_IsDropped(string text) =>
        Assert.Null(AsrResultFilter.Clean(text, noSpeechProbability: 0.1f));

    [Theory]
    [InlineData("Today I will explain how to use this feature.")]
    [InlineData("この機能の使い方を説明します。")]
    [InlineData("Thank you for watching the first part, now let's continue.")] // chứa cụm quen nhưng không phải toàn bộ
    [InlineData("No, no, no, that's not what I meant.")]
    public void RealSpeech_IsKept(string text) =>
        Assert.Equal(text, AsrResultFilter.Clean(text, noSpeechProbability: 0.1f));

    [Fact]
    public void Annotations_AreStrippedFromRealSpeech() =>
        Assert.Equal("Press start. Then wait.", AsrResultFilter.Clean("Press start. (music) Then wait. [BLANK_AUDIO]", 0.1f));

    [Theory]
    [InlineData("Audio shot.", 0.31f)]                                // small + tiếng ồn (benchmark P0)
    [InlineData("AN mentioned this thing with this thing", 0.08f)]    // small Vulkan + tiếng ồn
    [InlineData("KENNETHede", 0.21f)]
    public void LowConfidence_IsDropped(string text, float avgProbability) =>
        Assert.Null(AsrResultFilter.Clean(text, 0f, avgProbability));

    [Fact]
    public void LowestRealSpeechConfidence_IsKept() =>
        Assert.Equal("Press Start.", AsrResultFilter.Clean("Press Start.", 0.02f, 0.53f));

    [Fact]
    public void Bye_AloneIsDropped() => Assert.Null(AsrResultFilter.Clean("Bye.", 0f, 0.59f)); // turbo + nhạc

    [Fact]
    public void HighNoSpeechProbability_DropsShortText_KeepsLongText()
    {
        Assert.Null(AsrResultFilter.Clean("Okay.", 0.9f));
        Assert.NotNull(AsrResultFilter.Clean("The translated subtitles will appear on top of the video.", 0.9f));
    }
}
