using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Suni.App.ViewModels;

namespace Suni.App.Overlay;

/// <summary>
/// Các dòng đang hiện trên overlay: tối đa <see cref="MaxLines"/> câu mới nhất của phụ đề chính.
/// Như phụ đề phim, màn hình được xoá khi không có câu mới / bản dịch mới trong <see cref="ClearAfter"/>.
/// Không phụ thuộc WPF để test được; <see cref="Tick"/> do timer của cửa sổ gọi.
/// </summary>
public sealed class OverlayFeed
{
    private readonly ObservableCollection<SubtitleLine> _source;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastActivity;

    public OverlayFeed(ObservableCollection<SubtitleLine> source, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
        _lastActivity = _time.GetUtcNow();
        _source.CollectionChanged += OnSourceChanged;
        foreach (var line in _source)
            line.PropertyChanged += OnLineChanged;
    }

    public ObservableCollection<SubtitleLine> Lines { get; } = [];

    public int MaxLines { get; set; } = 2;

    /// <summary>0 hoặc âm = không tự xoá.</summary>
    public TimeSpan ClearAfter { get; set; } = TimeSpan.FromSeconds(8);

    public void Tick()
    {
        if (Lines.Count > 0 && ClearAfter > TimeSpan.Zero && _time.GetUtcNow() - _lastActivity >= ClearAfter)
            Lines.Clear();
    }

    /// <summary>
    /// N câu mới nhất theo seq, xếp cũ → mới (câu mới ở dưới như phụ đề phim) — không phụ thuộc thứ tự
    /// của danh sách chính (màn chính hiện mới → cũ).
    /// </summary>
    public void Rebuild()
    {
        var recent = _source.OrderByDescending(l => l.Seq).Take(Math.Max(1, MaxLines)).OrderBy(l => l.Seq).ToList();
        if (recent.SequenceEqual(Lines))
            return;
        Lines.Clear();
        foreach (var l in recent)
            Lines.Add(l);
    }

    public void Detach()
    {
        _source.CollectionChanged -= OnSourceChanged;
        foreach (var line in _source)
            line.PropertyChanged -= OnLineChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SubtitleLine l in e.OldItems)
                l.PropertyChanged -= OnLineChanged;
        if (e.NewItems is not null)
            foreach (SubtitleLine l in e.NewItems)
                l.PropertyChanged += OnLineChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var l in _source)
                l.PropertyChanged += OnLineChanged;

        _lastActivity = _time.GetUtcNow();
        Rebuild();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubtitleLine.Translation))
            return;
        _lastActivity = _time.GetUtcNow();
        Rebuild(); // bản dịch về muộn cho câu đã bị xoá khỏi màn hình → hiện lại
    }
}
