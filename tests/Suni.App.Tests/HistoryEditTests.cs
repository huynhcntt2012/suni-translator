using System.IO;
using Suni.App.Services;
using Suni.App.ViewModels;
using Suni.Core.Domain;
using Suni.Pipeline;
using Suni.Storage;

namespace Suni.App.Tests;

public class HistoryEditTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suni_edit_{Guid.NewGuid():N}");
    private HistoryStore _store = null!;
    private HistoryRecorder _recorder = null!;
    private HistoryViewModel _vm = null!;
    private Session _session = null!;

    public async Task InitializeAsync()
    {
        _store = new HistoryStore(SuniDatabase.ForFile(Path.Combine(_dir, "suni.db")));
        await _store.InitializeAsync();
        _recorder = new HistoryRecorder(_store, new InMemoryEventBus());
        _vm = new HistoryViewModel(_store, _recorder, new ExportService([]));

        _session = new Session
        {
            Id = Ids.NewSessionId(), StartedAt = DateTimeOffset.Now, CaptureMode = CaptureMode.ProcessLoopback,
            SourceApp = "chrome.exe", TargetLang = "vi",
        };
        await _store.UpsertAsync(_session);
        await _store.UpsertAsync(Seg(1, "Press start.", "Bấm Bắt đầu."));
        await _store.UpsertAsync(Seg(2, "Open settings.", null, SegmentStatus.TranslateFailed));

        await _vm.InitializeAsync(); // chọn phiên mới nhất và nạp câu
        for (var i = 0; i < 50 && _vm.Lines.Count < 2; i++)
            await Task.Delay(20); // nạp câu chạy trong handler async của SelectedSession
    }

    public async Task DisposeAsync()
    {
        await _recorder.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private Segment Seg(long seq, string original, string? translated, SegmentStatus status = SegmentStatus.Translated) => new()
    {
        Id = $"seg_{seq}_{_session.Id}", SessionId = _session.Id, Seq = seq, StartMs = seq * 1000, EndMs = seq * 1000 + 900,
        TextOriginal = original, TextTranslated = translated, Status = status,
    };

    private HistoryLine Line(int seq) => _vm.Lines.Single(l => l.SegmentId == $"seg_{seq}_{_session.Id}");

    [Fact]
    public void Lines_ShowMachineTranslation_AndFailedMarker()
    {
        Assert.Equal("Bấm Bắt đầu.", Line(1).Translation);
        Assert.False(Line(1).IsEdited);
        Assert.Equal("⚠ bản dịch thất bại", Line(2).Translation);
    }

    [Fact]
    public async Task EditAndSave_PersistsEditedTranslation_KeepingMachineVersion()
    {
        var line = Line(1);
        _vm.BeginEditCommand.Execute(line);
        Assert.True(line.IsEditing);
        Assert.Equal("Bấm Bắt đầu.", line.EditText);

        line.EditText = "Nhấn nút Start.";
        await _vm.SaveEditCommand.ExecuteAsync(line);

        Assert.False(line.IsEditing);
        Assert.True(line.IsEdited);
        Assert.Equal("Nhấn nút Start.", line.Translation);
        Assert.Contains("Bấm Bắt đầu.", line.MachineTooltip);

        var saved = (await _store.ListBySessionAsync(_session.Id)).Single(s => s.Seq == 1);
        Assert.Equal("Nhấn nút Start.", saved.TextTranslatedEdited);
        Assert.Equal("Bấm Bắt đầu.", saved.TextTranslated);
    }

    [Fact]
    public async Task Revert_GoesBackToMachineTranslation()
    {
        var line = Line(1);
        _vm.BeginEditCommand.Execute(line);
        line.EditText = "Nhấn nút Start.";
        await _vm.SaveEditCommand.ExecuteAsync(line);

        await _vm.RevertEditCommand.ExecuteAsync(line);

        Assert.False(line.IsEdited);
        Assert.Equal("Bấm Bắt đầu.", line.Translation);
        Assert.Null((await _store.ListBySessionAsync(_session.Id)).Single(s => s.Seq == 1).TextTranslatedEdited);
    }

    [Fact]
    public async Task Cancel_DiscardsChanges()
    {
        var line = Line(1);
        _vm.BeginEditCommand.Execute(line);
        line.EditText = "không lưu";
        _vm.CancelEditCommand.Execute(line);

        Assert.False(line.IsEditing);
        Assert.Equal("Bấm Bắt đầu.", line.Translation);
        Assert.Null((await _store.ListBySessionAsync(_session.Id)).Single(s => s.Seq == 1).TextTranslatedEdited);
    }

    [Fact]
    public async Task FailedTranslation_CanBeFilledByHand()
    {
        var line = Line(2);
        _vm.BeginEditCommand.Execute(line);
        Assert.Equal("", line.EditText); // không chép dấu "⚠ bản dịch thất bại" vào ô sửa

        line.EditText = "Mở cài đặt.";
        await _vm.SaveEditCommand.ExecuteAsync(line);

        Assert.Equal("Mở cài đặt.", line.Translation);
    }

    [Fact]
    public void EditingAnotherLine_ClosesThePreviousEditor()
    {
        _vm.BeginEditCommand.Execute(Line(1));
        _vm.BeginEditCommand.Execute(Line(2));

        Assert.False(Line(1).IsEditing);
        Assert.True(Line(2).IsEditing);
    }
}
