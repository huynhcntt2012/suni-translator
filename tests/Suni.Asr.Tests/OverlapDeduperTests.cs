namespace Suni.Asr.Tests;

public class OverlapDeduperTests
{
    [Theory]
    [InlineData("on top of the video, sentence", "sentence by sentence, while", "by sentence, while")]
    [InlineData("on top of the video.", "The video. Sentence by sentence", "Sentence by sentence")]
    [InlineData("keeps playing", "keeps playing the music", "the music")]
    [InlineData("hello world", "something else entirely", "something else entirely")]
    [InlineData("", "first chunk", "first chunk")]
    [InlineData("previous", "", "")]
    public void WordBased(string previous, string next, string expected) =>
        Assert.Equal(expected, OverlapDeduper.TrimOverlap(previous, next));

    [Fact]
    public void CaseAndPunctuation_AreIgnoredWhenMatching() =>
        Assert.Equal("is great", OverlapDeduper.TrimOverlap("This Feature,", "feature is great"));

    [Fact]
    public void OverlapLongerThanMaxWords_IsLeftUntouched()
    {
        // 300 ms chồng lấn không thể chứa 5 từ; trùng dài như vậy nhiều khả năng là người nói lặp lại thật.
        Assert.Equal("a b c d e f", OverlapDeduper.TrimOverlap("x a b c d e", "a b c d e f"));
    }

    [Theory]
    [InlineData("使い方を説明", "説明します", "します")]
    [InlineData("你好世界", "世界和平", "和平")]
    [InlineData("안녕하세요", "반갑습니다", "반갑습니다")]
    public void CharacterBased_ForCjk(string previous, string next, string expected) =>
        Assert.Equal(expected, OverlapDeduper.TrimOverlap(previous, next, characterBased: true));
}
