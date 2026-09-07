using Microsoft.Data.Sqlite;
using PrintGate.Core;
using System.Text.Json;
using Xunit;

public sealed class AdminTests : IDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, "admin-tests-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "audit.db");
    public AdminTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        Assert.False(new AuditReader(Db).Query(new()).DatabaseExists);
        Assert.False(File.Exists(Db));
    }
    [Fact]
    public void PeopleCountsGroupByExactIdAndDrillDownKeepsFilters()
    {
        var store = new AuditStore(Db);
        store.Begin(new("001","Alice"),"pc","Club");
        store.Begin(new("001","Alice"),"pc","Lab");
        store.Begin(new("0012","Bob"),"pc","Lab");
        var reader = new AuditReader(Db);
        var people = reader.People(new(),1,1);
        Assert.Equal(2,people.Total); Assert.Equal(2,Assert.Single(people.Rows).Uses);
        Assert.Equal("001",people.Rows[0].StudentId);
        Assert.Equal("0012",Assert.Single(reader.People(new(),2,1).Rows).StudentId);
        Assert.Equal(2,reader.Query(new(StudentId:"001")).Summary.Sessions);
        Assert.Single(reader.Query(new("Lab",StudentId:"001")).Rows);
        Assert.All(reader.People(new("Lab")).Rows,p=>Assert.Equal(1,p.Uses));
    }
    [Fact]
    public void MomentSearchFindsOverlappingSessionsWithoutIncludingLaterOnes()
    {
        var store = new AuditStore(Db);
        var a = store.Begin(new("001","Alice"),"pc","Lab");
        var b = store.Begin(new("002","Bob"),"pc","Lab");
        var t = new DateTimeOffset(2026,9,1,14,0,0,TimeSpan.Zero);
        using var connection = new SqliteConnection("Data Source="+Db); connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET started_at=CASE WHEN session_id=$id THEN $early ELSE $late END,ended_at=$end";
        cmd.Parameters.AddWithValue("$id",a); cmd.Parameters.AddWithValue("$early",t.ToString("O"));
        cmd.Parameters.AddWithValue("$late",t.AddHours(1).ToString("O")); cmd.Parameters.AddWithValue("$end",t.AddHours(2).ToString("O")); cmd.ExecuteNonQuery();
        var query = new AuditFilter(From:t.AddMinutes(30),Until:t.AddMinutes(30).AddSeconds(1),Overlap:true);
        var reader = new AuditReader(Db);
        Assert.Equal(a,Assert.Single(reader.Query(query).Rows).SessionId);
        Assert.Empty(reader.Query(query with { Overlap=false }).Rows);
        Assert.Equal("001",Assert.Single(reader.People(query).Rows).StudentId);
        using var writer = new StringWriter(); Assert.Equal(1,reader.Export(query,writer)); Assert.DoesNotContain(b,writer.ToString());
    }
    [Fact]
    public void FiltersPagingAndReadOnlyUnfinishedSessions()
    {
        var store = new AuditStore(Db);
        var a = store.Begin(new("001", "Alice"), "printer", "Robotics");
        var b = store.Begin(new("001", "Alice"), "printer", "Robotics");
        var c = store.Begin(new("002", "Bob"), "printer", "Design%");
        store.End(b,"studio_closed"); store.End(c,"application_fault"); store.Record(b,"recording_started");
        var reader = new AuditReader(Db);
        var page = reader.Query(new(), 2, 1);
        Assert.Single(page.Rows); Assert.Equal(new AuditSummary(3,2,1,1), page.Summary);
        Assert.Equal(2, reader.Query(new("Robotics")).Summary.Sessions);
        Assert.Single(reader.Query(new("%")).Rows);
        Assert.Empty(reader.Query(new("' OR 1=1 --")).Rows);
        Assert.Equal(c, Assert.Single(reader.Query(new(View: SessionView.Abnormal)).Rows).SessionId);
        Assert.Equal(b, Assert.Single(reader.Query(new(View: SessionView.Recorded)).Rows).SessionId);
        Assert.Null(Assert.Single(reader.Query(new(View: SessionView.Unfinished)).Rows).EndedAt);
        Assert.Equal(a, Assert.Single(reader.Query(new(View: SessionView.Unfinished)).Rows).SessionId);
        Assert.Equal(2, reader.Events(b,new()).Count(e => e.Type == "studio_closed" || e.Type == "recording_started"));
    }
    [Fact]
    public void DateRangeIsStartInclusiveEndExclusiveAndExportIncludesEveryRow()
    {
        var store = new AuditStore(Db);
        var first = store.Begin(new("001", "Alice"), "printer", "=FORMULA");
        var second = store.Begin(new("002", "Bob"), "printer", "Design");
        var t = new DateTimeOffset(2026,9,1,0,0,0,TimeSpan.Zero);
        using var connection = new SqliteConnection("Data Source=" + Db); connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET started_at=CASE WHEN session_id=$id THEN $first ELSE $second END";
        cmd.Parameters.AddWithValue("$id",first); cmd.Parameters.AddWithValue("$first",t.ToString("O")); cmd.Parameters.AddWithValue("$second",t.AddDays(1).ToString("O")); cmd.ExecuteNonQuery();
        var reader = new AuditReader(Db);
        Assert.Equal(first, Assert.Single(reader.Query(new(From:t, Until:t.AddDays(1))).Rows).SessionId);
        using var writer = new StringWriter(); Assert.Equal(2, reader.Export(new(),writer));
        Assert.Contains("\"'=FORMULA\"",writer.ToString()); Assert.Contains(second,writer.ToString());
    }
    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("  @evil")]
    [InlineData("\t123")]
    public void CsvNeutralizesSpreadsheetFormulas(string value) => Assert.StartsWith("\"'",AuditReader.CsvCell(value));

    [Fact]
    public void LegacyDatabaseIsReadWithoutMigration()
    {
        using var connection = new SqliteConnection("Data Source=" + Db); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sessions(session_id TEXT,student_id TEXT,name TEXT,computer_id TEXT,started_at TEXT,ended_at TEXT,end_reason TEXT); CREATE TABLE events(id INTEGER,session_id TEXT,occurred_at TEXT,event_type TEXT); INSERT INTO sessions VALUES('a','001','Alice','pc','2026-09-01T00:00:00.0000000+00:00',NULL,NULL)";
        command.ExecuteNonQuery();
        Assert.Null(Assert.Single(new AuditReader(Db).Query(new()).Rows).Organization);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name='organization'";
        Assert.Equal(0L,command.ExecuteScalar());
    }
    [Fact]
    public void RecordingsAreAssociatedAndRecheckedBeforePlayback()
    {
        var id = Guid.NewGuid().ToString("N");
        var metadata = new RecordingMetadata(id,"session",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"completed");
        var json = Path.Combine(root,$"pg-{id}.json"); var video = Path.Combine(root,$"pg-{id}.mkv");
        File.WriteAllText(json,JsonSerializer.Serialize(metadata)); File.WriteAllText(video,"sample");
        var index = new RecordingIndex(root);
        Assert.Empty(index.ForSession("another").Recordings);
        Assert.Equal(video,index.ResolveForPlayback("session",id));
        File.Delete(video);
        Assert.Throws<IOException>(()=>index.ResolveForPlayback("session",id));
        File.WriteAllText(video,"sample"); File.WriteAllText(json,JsonSerializer.Serialize(metadata with { EndedAt = null }));
        Assert.Throws<IOException>(()=>index.ResolveForPlayback("session",id));
        Assert.Null(JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(json))!.EndedAt);
        File.WriteAllText(json,JsonSerializer.Serialize(metadata with { Id = "../../outside" }));
        Assert.Equal(1,index.ForSession("session").Skipped);
    }
}
