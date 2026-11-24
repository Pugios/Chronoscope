using Chronoscope.Tracker.Sampling;
using Microsoft.Data.Sqlite;

namespace Chronoscope.Tracker.Storage;

public enum SpanKind
{
    Active,
    Idle,
}

// One stretch of time during which nothing changed: same window, or same idle state.
public sealed record ActivitySpan(
    long Id, DateTimeOffset Start, DateTimeOffset End, SpanKind Kind,
    string? AppName, string? Process, string? ExePath, string? Title)
{
    public double Seconds => (End - Start).TotalSeconds;
}

// The tracker's own database. Timestamps are Unix milliseconds in UTC: they sort, they survive
// a change of time zone or DST, and they need no parsing. Anything meant for humans goes through
// the activity_local view, which shows them in local time.
//
// Gaps are simply missing rows. The tracker was not running, or the machine slept; a gap is not
// idle time and is never written as such.
public sealed class ActivityStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _extend;

    public ActivityStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        // WAL: the Chronoscope app can read while the tracker writes, without blocking either.
        // NORMAL sync: a power cut may lose the last few heartbeats, never corrupt the file.
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA synchronous = NORMAL");
        CreateSchema();

        _insert = _connection.CreateCommand();
        _insert.CommandText = """
            INSERT INTO activity (start_ms, end_ms, kind, app_name, process, exe_path, title)
            VALUES ($ms, $ms, $kind, $app, $process, $exe, $title);
            SELECT last_insert_rowid();
            """;
        foreach (string name in new[] { "$ms", "$kind", "$app", "$process", "$exe", "$title" })
        {
            _insert.Parameters.Add(new SqliteParameter(name, null));
        }

        _extend = _connection.CreateCommand();
        _extend.CommandText = "UPDATE activity SET end_ms = $ms WHERE id = $id";
        _extend.Parameters.Add(new SqliteParameter("$ms", null));
        _extend.Parameters.Add(new SqliteParameter("$id", null));
    }

    private void CreateSchema()
    {
        Execute($"""
            CREATE TABLE IF NOT EXISTS activity (
                id       INTEGER PRIMARY KEY,
                start_ms INTEGER NOT NULL,
                end_ms   INTEGER NOT NULL,
                kind     TEXT    NOT NULL CHECK (kind IN ('active', 'idle')),
                app_name TEXT,
                process  TEXT,
                exe_path TEXT,
                title    TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_activity_start ON activity (start_ms);

            CREATE VIEW IF NOT EXISTS activity_local AS
            SELECT id,
                   datetime(start_ms / 1000, 'unixepoch', 'localtime') AS start,
                   datetime(end_ms   / 1000, 'unixepoch', 'localtime') AS end,
                   (end_ms - start_ms) / 1000.0                        AS seconds,
                   kind, app_name, process, title
            FROM activity;

            PRAGMA user_version = {SchemaVersion};
            """);
    }

    // Starts a span. It begins zero-length; Extend stretches it on every poll.
    public long Open(DateTimeOffset at, SpanKind kind, WindowInfo? window)
    {
        _insert.Parameters["$ms"].Value = at.ToUnixTimeMilliseconds();
        _insert.Parameters["$kind"].Value = kind == SpanKind.Active ? "active" : "idle";
        _insert.Parameters["$app"].Value = (object?)window?.AppName ?? DBNull.Value;
        _insert.Parameters["$process"].Value = (object?)window?.Process ?? DBNull.Value;
        _insert.Parameters["$exe"].Value = (object?)window?.ExePath ?? DBNull.Value;
        _insert.Parameters["$title"].Value = (object?)window?.Title ?? DBNull.Value;
        return (long)_insert.ExecuteScalar()!;
    }

    // The heartbeat. Writing the end on every poll means a crash or a power cut costs at most
    // one poll interval, and no "close the span on shutdown" step has to succeed.
    public void Extend(long id, DateTimeOffset end)
    {
        _extend.Parameters["$ms"].Value = end.ToUnixTimeMilliseconds();
        _extend.Parameters["$id"].Value = id;
        _extend.ExecuteNonQuery();
    }

    // Every span that overlaps [from, to), in time order. Not clipped to the range.
    public List<ActivitySpan> Read(DateTimeOffset from, DateTimeOffset to)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, start_ms, end_ms, kind, app_name, process, exe_path, title
            FROM activity
            WHERE start_ms < $to AND end_ms > $from
            ORDER BY start_ms
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var spans = new List<ActivitySpan>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            spans.Add(new ActivitySpan(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetString(3) == "active" ? SpanKind.Active : SpanKind.Idle,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return spans;
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _insert.Dispose();
        _extend.Dispose();
        _connection.Dispose();
    }
}
