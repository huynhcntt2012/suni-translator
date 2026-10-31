using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Suni.Storage;

/// <summary>Mở kết nối SQLite (WAL) và chạy migration nhúng trong assembly (Schema/NNN_*.sql).</summary>
public sealed class SuniDatabase(string connectionString)
{
    /// <summary>
    /// Thư mục dữ liệu của app: %LOCALAPPDATA%\Suni, hoặc biến môi trường SUNI_DATA_DIR nếu có —
    /// dùng cho test/chạy thử để không bao giờ chạm vào lịch sử và cài đặt thật của người dùng.
    /// </summary>
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("SUNI_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Suni");

    public static string DefaultPath => Path.Combine(DataRoot, "data", "suni.db");

    public static SuniDatabase ForFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new SuniDatabase(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
    }

    /// <summary>Đường dẫn file DB.</summary>
    public string FilePath { get; } = new SqliteConnectionStringBuilder(connectionString).DataSource;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;").ConfigureAwait(false);
        return conn;
    }

    public static async Task MigrateAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_version(version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL);")
            .ConfigureAwait(false);
        var current = await conn.ExecuteScalarAsync<long?>("SELECT MAX(version) FROM schema_version;").ConfigureAwait(false) ?? 0;

        foreach (var (version, sql) in LoadMigrations().Where(m => m.Version > current))
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("INSERT INTO schema_version(version, applied_at) VALUES (@version, @now);",
                new { version, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(int Version, string Sql)> LoadMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Schema.") && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var file = n[(n.IndexOf(".Schema.", StringComparison.Ordinal) + 8)..];
                using var reader = new StreamReader(asm.GetManifestResourceStream(n)!);
                return (int.Parse(file[..file.IndexOf('_')]), reader.ReadToEnd());
            })
            .OrderBy(m => m.Item1);
    }
}
