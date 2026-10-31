using System.Collections.ObjectModel;
using Suni.App.Overlay;
using Suni.App.ViewModels;

namespace Suni.App.Tests;

public class OverlayFeedTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static SubtitleLine Line(int n, string? translation = null) =>
        new($"seg_{n}", n, "00:00:00", "en", $"original {n}", translation);

    [Fact]
    public void ShowsOnlyMostRecentLines()
    {
        var source = new ObservableCollection<SubtitleLine>();
        var feed = new OverlayFeed(source) { MaxLines = 2 };

        for (var i = 1; i <= 5; i++)
            source.Add(Line(i));

        Assert.Equal(["seg_4", "seg_5"], feed.Lines.Select(l => l.SegmentId));
    }

    [Fact]
    public void NewestFirstSource_StillShowsNewestLines_InChronologicalOrder()
    {
        // Màn chính chèn câu mới ở đầu (mới → cũ); overlay vẫn phải hiện 2 câu mới nhất, câu mới ở dưới.
        var source = new ObservableCollection<SubtitleLine>();
        var feed = new OverlayFeed(source) { MaxLines = 2 };

        for (var i = 1; i <= 5; i++)
            source.Insert(0, Line(i));

        Assert.Equal(["seg_4", "seg_5"], feed.Lines.Select(l => l.SegmentId));
    }

    [Fact]
    public void ChangingMaxLines_TakesEffectOnRebuild()
    {
        var source = new ObservableCollection<SubtitleLine> { Line(1), Line(2), Line(3) };
        var feed = new OverlayFeed(source) { MaxLines = 3 };
        feed.Rebuild();
        Assert.Equal(3, feed.Lines.Count);

        feed.MaxLines = 1;
        feed.Rebuild();
        Assert.Equal(["seg_3"], feed.Lines.Select(l => l.SegmentId));
    }

    [Fact]
    public void ClearsAfterSilence_LikeMovieSubtitles()
    {
        var time = new ManualTime();
        var source = new ObservableCollection<SubtitleLine>();
        var feed = new OverlayFeed(source, time) { ClearAfter = TimeSpan.FromSeconds(8) };

        source.Add(Line(1));
        time.Advance(7.9);
        feed.Tick();
        Assert.Single(feed.Lines);

        time.Advance(0.2);
        feed.Tick();
        Assert.Empty(feed.Lines);
    }

    [Fact]
    public void LateTranslation_ResetsTimer_AndBringsLineBack()
    {
        var time = new ManualTime();
        var line = Line(1);
        var source = new ObservableCollection<SubtitleLine> { line };
        var feed = new OverlayFeed(source, time) { ClearAfter = TimeSpan.FromSeconds(8) };
        feed.Rebuild();

        time.Advance(9);
        feed.Tick();
        Assert.Empty(feed.Lines);

        line.Translation = "bản dịch đến muộn";
        Assert.Single(feed.Lines);
        time.Advance(5);
        feed.Tick();
        Assert.Single(feed.Lines);
    }

    [Fact]
    public void ClearAfterZero_NeverClears()
    {
        var time = new ManualTime();
        var source = new ObservableCollection<SubtitleLine> { Line(1) };
        var feed = new OverlayFeed(source, time) { ClearAfter = TimeSpan.Zero };
        feed.Rebuild();

        time.Advance(3600);
        feed.Tick();
        Assert.Single(feed.Lines);
    }

    [Fact]
    public void MergedLineReplacement_IsReflected()
    {
        // MainViewModel thay các dòng mảnh bằng một dòng gộp (Lines[i] = merged; RemoveAt...).
        var source = new ObservableCollection<SubtitleLine> { Line(1), Line(2), Line(3) };
        var feed = new OverlayFeed(source) { MaxLines = 2 };
        feed.Rebuild();

        source.RemoveAt(2);
        source[1] = new SubtitleLine("seg_23", 2, "00:00:00", "en", "original 2 original 3", "câu gộp");

        Assert.Equal(["seg_1", "seg_23"], feed.Lines.Select(l => l.SegmentId));
        Assert.Equal("câu gộp", feed.Lines[1].PrimaryText);
    }

    [Fact]
    public void PrimaryText_FallsBackToOriginal_UntilTranslated()
    {
        var line = Line(1);
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("original 1", line.PrimaryText);
        line.Translation = "bản dịch";
        Assert.Equal("bản dịch", line.PrimaryText);
        Assert.Contains(nameof(SubtitleLine.PrimaryText), changed);
    }

    [Fact]
    public void Detach_StopsFollowingSource()
    {
        var source = new ObservableCollection<SubtitleLine>();
        var feed = new OverlayFeed(source);
        feed.Detach();

        source.Add(Line(1));

        Assert.Empty(feed.Lines);
    }
}
