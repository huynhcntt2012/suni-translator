using Dapper;
using Microsoft.Data.Sqlite;
using Suni.Core.Domain;

namespace Suni.Storage.Tests;

public class RecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suni_recover_{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_dir, "suni.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>3 phiên × 700 câu dài → DB nhiều trang; checkpoint để dữ liệu nằm trong file chính.</summary>
    private async Task<(int Sessions, int Segments)> CreatePopulatedDbAsync()
    {
        var store = new HistoryStore(SuniDatabase.ForFile(DbPath));
        await store.InitializeAsync();
        var writes = new List<HistoryWrite>();
        for (var s = 0; s < 3; s++)
        {
            var session = new Session
            {
                Id = $"sess_{s}", StartedAt = DateTimeOffset.Now.AddHours(-s), CaptureMode = CaptureMode.ProcessLoopback,
                SourceApp = "chrome.exe", TargetLang = "en", Status = SessionStatus.Stopped,
            };
            writes.Add(HistoryWrite.Session(session));
            for (var i = 1; i <= 700; i++)
                writes.Add(HistoryWrite.Segment(new Segment
                {
                    Id = $"seg_{s}_{i}", SessionId = session.Id, Seq = i, StartMs = i * 1000, EndMs = i * 1000 + 900,
                    TextOriginal = $"Câu gốc số {i} của phiên {s}: " + new string('x', 120),
                    TextTranslated = $"Translation {i} of session {s}: " + new string('y', 120),
                    Status = SegmentStatus.Translated,
                }));
        }
        await store.WriteBatchAsync(writes);
        await using (var conn = await new SuniDatabase(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString()).OpenAsync())
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        SqliteConnection.ClearAllPools();
        return (3, 2100);
    }

    private static void CorruptPage(string path, int page, int pageSize = 4096)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Position = (long)(page - 1) * pageSize;
        var garbage = new byte[pageSize];
        new Random(42).NextBytes(garbage);
        fs.Write(garbage);
    }

    private static async Task<string> QuickCheckAsync(string path)
    {
        await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        await conn.OpenAsync();
        return (await conn.QueryAsync<string>("PRAGMA quick_check;")).First();
    }

    [Fact]
    public async Task HealthyDb_IsLeftAlone()
    {
        await CreatePopulatedDbAsync();
        Assert.Null(await HistoryStore.RecoverIfCorruptAsync(DbPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public async Task MissingDb_IsNotAnError() => Assert.Null(await HistoryStore.RecoverIfCorruptAsync(DbPath));

    [Fact]
    public async Task CorruptPage_SalvagesReadableRows_IntoHealthyNewDb_AndKeepsCorruptCopy()
    {
        var (sessions, segments) = await CreatePopulatedDbAsync();
        var pages = (int)(new FileInfo(DbPath).Length / 4096);
        CorruptPage(DbPath, pages / 2);
        Assert.NotEqual("ok", await QuickCheckAsync(DbPath).ContinueWith(t => t.IsFaulted ? "lỗi" : t.Result));

        var report = await HistoryStore.RecoverIfCorruptAsync(DbPath);

        Assert.NotNull(report);
        Assert.Equal(sessions, report.SessionsRecovered + report.OrphanSessionsCreated);
        Assert.InRange(report.SegmentsRecovered, segments * 9 / 10, segments); // chỉ mất các dòng trên trang hỏng
        Assert.True(File.Exists(report.CorruptCopyPath));                      // bản hỏng được giữ lại
        Assert.Equal("ok", await QuickCheckAsync(DbPath));                     // DB mới lành

        var store = new HistoryStore(SuniDatabase.ForFile(DbPath));
        var listed = await store.ListAsync();
        Assert.Equal(report.SegmentsRecovered, listed.Sum(s => s.SegmentCount));
        Assert.NotEmpty(await store.SearchAsync("Translation 5"));            // chỉ mục tìm kiếm được dựng lại
    }

    [Fact]
    public async Task GarbageValuesInRows_AreSkipped_NotFatal()
    {
        // Gặp trên DB hỏng thật: cột text chứa blob rác, status không phải enum hợp lệ.
        await CreatePopulatedDbAsync();
        await using (var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("PRAGMA foreign_keys = OFF;"); // mô phỏng dữ liệu rác: bỏ qua ràng buộc khi ghi
            await conn.ExecuteAsync("UPDATE segments SET session_id = randomblob(16) WHERE seq = 5;");
            await conn.ExecuteAsync("UPDATE segments SET status = 'NotAStatus' WHERE seq = 6;");
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        CorruptPage(DbPath, (int)(new FileInfo(DbPath).Length / 4096) / 2);

        var report = await HistoryStore.RecoverIfCorruptAsync(DbPath);

        Assert.NotNull(report);
        var store = new HistoryStore(SuniDatabase.ForFile(DbPath));
        foreach (var s in await store.ListAsync())
            Assert.All(await store.ListBySessionAsync(s.Session.Id), seg => Assert.NotEqual(5, seg.Seq)); // dòng rác bị bỏ, đọc lại không lỗi
    }

    [Fact]
    public async Task FileThatIsNotADatabase_IsReplacedByEmptyDb()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(DbPath, "đây không phải là SQLite");

        var report = await HistoryStore.RecoverIfCorruptAsync(DbPath);

        Assert.NotNull(report);
        Assert.Equal(0, report.SegmentsRecovered);
        Assert.Equal("ok", await QuickCheckAsync(DbPath));
        Assert.Equal("đây không phải là SQLite", await File.ReadAllTextAsync(report.CorruptCopyPath));
    }
}
