using Microsoft.Data.Sqlite;
using PrintGate.Core;
using Xunit;

namespace PrintGate.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PrintGate-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AuditStore audit;
    private string DbPath => Path.Combine(directory, "audit.db");
    public SessionTests() => audit = new AuditStore(DbPath);
    private static readonly Identity Alice = new("001", "测试甲");
    private static readonly Identity Bob = new("002", "测试乙");

    [Fact] public void RetainedProjectCanOnlyBeResumedByItsOwner()
    {
        var session = new SessionCoordinator(audit, "TEST");
        session.Enter(Alice, "测试组织");
        session.Lock("mouse_idle_timeout", true);
        Assert.False(session.IsAuthorized);
        Assert.False(session.CanEnter(Bob));
        Assert.Throws<InvalidOperationException>(() => session.Enter(Bob, "另一组织"));
        session.Enter(Alice, "测试组织");
        Assert.True(session.IsAuthorized);
        session.Lock("studio_closed", false);
        session.Enter(Bob, "另一组织");
        Assert.True(session.IsAuthorized);
    }

    [Fact] public void StoresNameIdAndReasonWithoutCredentialColumns()
    {
        var id = audit.Begin(Alice, "TEST", "测试组织");
        audit.End(id, "studio_closed");
        using var connection = new SqliteConnection($"Data Source={DbPath}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT student_id,name,end_reason,ended_at FROM sessions";
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        Assert.Equal("001", reader.GetString(0)); Assert.Equal("测试甲", reader.GetString(1));
        Assert.Equal("studio_closed", reader.GetString(2)); Assert.False(reader.IsDBNull(3));
    }

    [Fact] public void AuditFailurePreventsAuthorization()
    {
        var session = new SessionCoordinator(audit, "TEST");
        Execute("DROP TABLE sessions");
        Assert.Throws<SqliteException>(() => session.Enter(Alice, "测试组织"));
        Assert.False(session.IsAuthorized);
    }

    [Fact] public void AuditFailureDuringLockStillRevokesAuthorization()
    {
        var session = new SessionCoordinator(audit, "TEST");
        session.Enter(Alice, "测试组织"); Execute("DROP TABLE sessions");
        Assert.Throws<SqliteException>(() => session.Lock("mouse_idle_timeout", true));
        Assert.False(session.IsAuthorized);
    }

    [Fact] public void ClosingIsIdempotent()
    {
        var id = audit.Begin(Alice, "TEST", "测试组织"); audit.End(id, "studio_closed"); audit.End(id, "other");
        Assert.Equal("studio_closed", Scalar("SELECT end_reason FROM sessions"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM events"));
    }

    [Fact] public void RecoversInterruptedSessionsWithExplicitRecoveryReason()
    {
        audit.Begin(Alice, "TEST", "测试组织"); audit.RecoverInterrupted();
        Assert.Equal("recovered_after_interruption", Scalar("SELECT end_reason FROM sessions"));
    }

    [Fact] public void OrganizationIsStoredPerSessionAndTrimmed()
    {
        var session = new SessionCoordinator(audit, "TEST");
        session.Enter(Alice, "  创客实验室  ");
        var first = session.SessionId;
        session.Lock("studio_closed", false);
        session.Enter(Alice, "学生社团 O'Brien");
        Assert.Equal("创客实验室", Scalar($"SELECT organization FROM sessions WHERE session_id='{first}'"));
        Assert.Equal("学生社团 O'Brien", Scalar($"SELECT organization FROM sessions WHERE session_id='{session.SessionId}'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("组织\n名称")]
    public void InvalidOrganizationCannotOpenSession(string value)
    {
        var session = new SessionCoordinator(audit, "TEST");
        Assert.Throws<ArgumentException>(() => session.Enter(Alice, value));
        Assert.False(session.IsAuthorized);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sessions"));
    }

    [Fact] public void RejectsOrganizationOverLimit()
        => Assert.Throws<ArgumentException>(() => audit.Begin(Alice, "TEST", new string('a', 101)));

    [Fact] public void UpgradesLegacyDatabaseWithoutChangingOldIdentity()
    {
        Execute("DROP TABLE sessions");
        Execute("CREATE TABLE sessions (session_id TEXT PRIMARY KEY, student_id TEXT NOT NULL, name TEXT NOT NULL, computer_id TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, end_reason TEXT)");
        Execute("INSERT INTO sessions VALUES ('legacy','001','测试甲','TEST','2026-09-01',NULL,NULL)");
        var upgraded = new AuditStore(DbPath);
        Assert.Equal("测试甲", Scalar("SELECT name FROM sessions WHERE session_id='legacy'"));
        Assert.Equal(DBNull.Value, Scalar("SELECT organization FROM sessions WHERE session_id='legacy'"));
        upgraded.Begin(Bob, "TEST", "新组织");
        _ = new AuditStore(DbPath); // Migration can run again without duplicate columns or data loss.
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM sessions"));
        Assert.Equal("新组织", Scalar("SELECT organization FROM sessions WHERE student_id='002'"));
    }

    private void Execute(string sql) { using var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar(); }
    public void Dispose() => Directory.Delete(directory, true);
}
