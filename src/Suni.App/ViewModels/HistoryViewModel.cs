using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Suni.App.Services;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Storage;

namespace Suni.App.ViewModels;

/// <summary>Màn History (SRS 3.2): phiên theo ngày/ứng dụng; tìm, xem câu, copy, xuất, xoá.</summary>
public sealed partial class HistoryViewModel(HistoryStore store, HistoryRecorder recorder, ExportService export) : ObservableObject
{
    private CancellationTokenSource? _searchDebounce;

    public ObservableCollection<SessionItem> Sessions { get; } = [];

    public ObservableCollection<HistoryLine> Lines { get; } = [];

    public IReadOnlyList<ContentOption> ContentOptions { get; } =
    [
        new(ExportContent.Bilingual, "Song ngữ"),
        new(ExportContent.OriginalOnly, "Chỉ câu gốc"),
        new(ExportContent.TranslationOnly, "Chỉ bản dịch"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(DeleteSessionCommand))]
    private SessionItem? _selectedSession;

    [ObservableProperty] private ContentOption _selectedContent = null!;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _detailTitle = "";

    /// <summary>Window chủ cho hộp thoại lưu/xác nhận.</summary>
    public Window? Owner { get; set; }

    public async Task InitializeAsync()
    {
        SelectedContent = ContentOptions[0];
        await recorder.FlushAsync(); // phiên vừa dừng phải có mặt
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var keep = SelectedSession?.Session.Id;
        Sessions.Clear();
        foreach (var s in await store.ListAsync())
            Sessions.Add(new SessionItem(s));
        SelectedSession = Sessions.FirstOrDefault(s => s.Session.Id == keep) ?? Sessions.FirstOrDefault();
        Message = Sessions.Count == 0 ? "Chưa có phiên nào được lưu." : null;
    }

    async partial void OnSelectedSessionChanged(SessionItem? value)
    {
        if (value is null || SearchText.Length > 0)
            return;
        DetailTitle = value.Title;
        Lines.Clear();
        foreach (var s in await store.ListBySessionAsync(value.Session.Id))
            Lines.Add(new HistoryLine(s, null));
    }

    async partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (value.Trim().Length == 0)
        {
            OnSelectedSessionChanged(SelectedSession);
            return;
        }

        var hits = await store.SearchAsync(value);
        if (cts.IsCancellationRequested)
            return;
        var titles = Sessions.ToDictionary(s => s.Session.Id, s => s.Title);
        DetailTitle = $"Kết quả tìm \"{value.Trim()}\": {hits.Count} câu";
        Lines.Clear();
        foreach (var h in hits)
            Lines.Add(new HistoryLine(h, titles.GetValueOrDefault(h.SessionId)));
    }

    [RelayCommand]
    private void Copy()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
        {
            sb.Append('[').Append(l.Time).Append("] ").AppendLine(l.Original);
            if (!string.IsNullOrEmpty(l.Translation))
                sb.Append("           ").AppendLine(l.Translation);
        }
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync(ExportFormat format)
    {
        var session = SelectedSession!.Session;
        try
        {
            var segments = await store.ListBySessionAsync(session.Id);
            var path = await export.ExportAsync(session, segments, format, SelectedContent.Content, Owner);
            if (path is not null)
                Message = $"Đã xuất {segments.Count} câu ra {path}";
        }
        catch (Exception ex)
        {
            Message = $"Xuất thất bại: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSessionAsync()
    {
        var item = SelectedSession!;
        if (MessageBox.Show(Owner!, $"Xoá phiên {item.Title}?\nKhông thể hoàn tác.", "Xoá phiên",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await store.DeleteAsync(item.Session.Id);
        Lines.Clear();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (Sessions.Count == 0 || MessageBox.Show(Owner!, $"Xoá toàn bộ {Sessions.Count} phiên trong lịch sử?\nKhông thể hoàn tác.",
                "Xoá lịch sử", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await store.DeleteAllAsync();
        Lines.Clear();
        await RefreshAsync();
    }

    private bool HasSelection() => SelectedSession is not null;

    // ------------------------------------------------------------ sửa bản dịch (FR-13)

    [RelayCommand]
    private void BeginEdit(HistoryLine? line)
    {
        if (line is null)
            return;
        foreach (var other in Lines.Where(l => l.IsEditing && l != line))
            other.IsEditing = false; // mỗi lúc sửa một câu
        line.EditText = line.EditedTranslation ?? line.MachineTranslation ?? "";
        line.IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveEditAsync(HistoryLine? line)
    {
        if (line is not { IsEditing: true })
            return;
        await SetEditedAsync(line, line.EditText);
        line.IsEditing = false;
    }

    [RelayCommand]
    private void CancelEdit(HistoryLine? line)
    {
        if (line is not null)
            line.IsEditing = false;
    }

    /// <summary>Bỏ bản sửa, quay về bản máy dịch.</summary>
    [RelayCommand]
    private async Task RevertEditAsync(HistoryLine? line)
    {
        if (line is null)
            return;
        await SetEditedAsync(line, null);
        line.IsEditing = false;
    }

    private async Task SetEditedAsync(HistoryLine line, string? text)
    {
        try
        {
            var updated = await store.SetEditedTranslationAsync(line.SegmentId, text);
            if (updated is null)
            {
                Message = "Không tìm thấy câu này (phiên có thể đã bị xoá).";
                return;
            }
            line.Apply(updated);
            Message = line.IsEdited ? "Đã lưu bản sửa — xuất TXT/SRT/JSON sẽ dùng bản này." : "Đã quay về bản máy dịch.";
        }
        catch (Exception ex)
        {
            Message = $"Không lưu được bản sửa: {ex.Message}";
        }
    }
}

public sealed record ContentOption(ExportContent Content, string Label);

public sealed class SessionItem(SessionSummary summary)
{
    public Session Session { get; } = summary.Session;
    public int Count { get; } = summary.SegmentCount;

    public string Title => $"{Session.StartedAt:dd/MM HH:mm} · {Session.SourceApp ?? "Hệ thống"}";

    public string Detail
    {
        get
        {
            var duration = Session.EndedAt is { } end ? $" · {(end - Session.StartedAt):h\\:mm\\:ss}" : "";
            return $"{Count} câu · {Session.SourceLang ?? "auto"} → {Session.TargetLang}{duration}";
        }
    }
}

/// <summary>Một câu trong History, sửa được bản dịch (FR-13). Bản máy dịch luôn được giữ để hoàn tác.</summary>
public sealed partial class HistoryLine : ObservableObject
{
    public HistoryLine(Segment segment, string? sessionTitle)
    {
        SegmentId = segment.Id;
        Time = TimeSpan.FromMilliseconds(segment.StartMs).ToString(@"hh\:mm\:ss");
        Original = segment.TextOriginal ?? "";
        SessionTitle = sessionTitle;
        Apply(segment);
    }

    public string SegmentId { get; }
    public string Time { get; }
    public string Original { get; }
    public string? SessionTitle { get; }

    /// <summary>Bản máy dịch (null nếu dịch thất bại / phiên không dịch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Translation), nameof(MachineTooltip))]
    private string? _machineTranslation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Translation), nameof(IsEdited), nameof(MachineTooltip))]
    private string? _editedTranslation;

    [ObservableProperty] private bool _translateFailed;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    public bool IsEdited => EditedTranslation is not null;

    /// <summary>Bản hiển thị/xuất: bản sửa nếu có, không thì bản máy.</summary>
    public string? Translation => EditedTranslation ?? MachineTranslation ?? (TranslateFailed ? "⚠ bản dịch thất bại" : null);

    public string? MachineTooltip => IsEdited ? $"Bản máy dịch: {MachineTranslation ?? "(không có)"}" : null;

    public void Apply(Segment segment)
    {
        MachineTranslation = segment.TextTranslated;
        EditedTranslation = segment.TextTranslatedEdited;
        TranslateFailed = segment.Status == SegmentStatus.TranslateFailed;
    }
}
