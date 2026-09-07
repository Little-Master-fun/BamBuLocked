using Microsoft.Data.Sqlite;

namespace PrintGate.Core;

public sealed class AuditStore
{
    private readonly string connectionString;
    public AuditStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY, student_id TEXT NOT NULL, name TEXT NOT NULL,
                computer_id TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, end_reason TEXT
            );
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT,
                occurred_at TEXT NOT NULL, event_type TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        // Preserve old records: NULL means the organization was not collected.
        using var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(sessions)";
        bool hasOrganization = false;
        using (var reader = schema.ExecuteReader())
            while (reader.Read())
                hasOrganization |= reader.GetString(1) == "organization";
        if (!hasOrganization)
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE sessions ADD COLUMN organization TEXT";
            migration.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    public string Begin(Identity identity, string computerId, string organization)
    {
        organization = Organization.Normalize(organization);
        var id = Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sessions (session_id,student_id,name,computer_id,started_at,organization) VALUES ($id,$student,$name,$computer,$time,$organization)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$student", identity.StudentId);
        command.Parameters.AddWithValue("$name", identity.Name);
        command.Parameters.AddWithValue("$organization", organization);
        command.Parameters.AddWithValue("$computer", computerId);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        InsertEvent(connection, transaction, id, "authenticated");
        transaction.Commit();
        return id;
    }

    public void End(string id, string reason)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE sessions SET ended_at=$time,end_reason=$reason WHERE session_id=$id AND ended_at IS NULL";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 1) InsertEvent(connection, transaction, id, reason);
        transaction.Commit();
    }

    public void Record(string? id, string type)
    {
        using var connection = Open();
        InsertEvent(connection, null, id, type);
    }

    public void RecoverInterrupted()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // End time is recovery time, NOT an invented exact crash timestamp.
        command.CommandText = "UPDATE sessions SET ended_at=$time,end_reason='recovered_after_interruption' WHERE ended_at IS NULL";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        Record(null, "application_started");
    }

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction? transaction, string? id, string type)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO events(session_id,occurred_at,event_type) VALUES ($id,$time,$type)";
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$type", type);
        command.ExecuteNonQuery();
    }
}
