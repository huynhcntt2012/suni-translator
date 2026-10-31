using Suni.Core.Domain;

namespace Suni.Core.Tests;

public class SegmentTests
{
    private static Segment NewSegment(SegmentStatus status = SegmentStatus.Captured) => new()
    {
        Id = Ids.NewSegmentId(),
        SessionId = "sess_test",
        Seq = 1,
        StartMs = 12_500,
        EndMs = 15_600,
        Status = status,
    };

    [Fact]
    public void MoveTo_ReturnsCopyWithNewStatus()
    {
        var s = NewSegment();
        var next = s.MoveTo(SegmentStatus.VoiceDetected);

        Assert.Equal(SegmentStatus.Captured, s.Status);
        Assert.Equal(SegmentStatus.VoiceDetected, next.Status);
        Assert.Equal(s.Id, next.Id);
    }

    [Fact]
    public void MoveTo_Invalid_Throws() =>
        Assert.Throws<InvalidSegmentTransitionException>(() => NewSegment().MoveTo(SegmentStatus.Translated));

    [Fact]
    public void Fail_SetsStatusAndError()
    {
        var failed = NewSegment(SegmentStatus.TranslatePending).Fail(SegmentStatus.TranslateFailed, ErrorCode.LlmTimeout);

        Assert.Equal(SegmentStatus.TranslateFailed, failed.Status);
        Assert.Equal(ErrorCode.LlmTimeout, failed.Error);
    }

    [Fact]
    public void DisplayTranslation_PrefersUserEdit()
    {
        var s = NewSegment() with { TextTranslated = "máy dịch", TextTranslatedEdited = "người sửa" };
        Assert.Equal("người sửa", s.DisplayTranslation);
        Assert.Equal("máy dịch", (s with { TextTranslatedEdited = null }).DisplayTranslation);
    }

    [Fact]
    public void DurationMs_IsEndMinusStart() => Assert.Equal(3_100, NewSegment().DurationMs);

    [Fact]
    public void Ids_HavePrefixAndAreUnique()
    {
        var a = Ids.NewSegmentId();
        var b = Ids.NewSegmentId();
        Assert.StartsWith("seg_", a);
        Assert.StartsWith("sess_", Ids.NewSessionId());
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("zh", true)]
    [InlineData("vi", false)]
    [InlineData(null, false)]
    public void Languages_IsCjk(string? code, bool expected) => Assert.Equal(expected, Languages.IsCjk(code));

    [Fact]
    public void Languages_UnknownCode_FallsBackToCode() => Assert.Equal("th", Languages.EnglishNameOf("th"));
}
