using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PrintGate.Core;

public enum SessionView { All, Unfinished, Abnormal, Recorded }
public sealed record AuditFilter(string Keyword = "", DateTimeOffset? From = null, DateTimeOffset? Until = null, SessionView View = SessionView.All, string? StudentId = null, bool Overlap = false);
public sealed record AuditPerson(string StudentId, string Name, long Uses, string FirstUsed, string LastUsed);
public sealed record PeoplePage(IReadOnlyList<AuditPerson> Rows, long Total);
public sealed record AuditSession(string SessionId, string StudentId, string Name, string? Organization, string ComputerId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? EndReason, bool HasRecordingEvent)
{
    public string OrganizationLabel => string.IsNullOrWhiteSpace(Organization) ? "未采集" : Organization;
    public string StartedLabel => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string EndedLabel => EndedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string DurationLabel => EndedAt is null ? "—" : $"{Math.Max(0, (EndedAt.Value - StartedAt).TotalMinutes):0.0} 分钟";
    public string ReasonLabel => EndedAt is null ? "未结束（待确认）" : AuditLabels.Describe(EndReason ?? "unknown");
    public string RecordingLabel => HasRecordingEvent ? "查看详情" : "无录屏事件";
}
public sealed record AuditEvent(DateTimeOffset OccurredAt, string Type, string? SessionId)
{
    public string TimeLabel => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Description => AuditLabels.Describe(Type);
}
public sealed record AuditSummary(long Sessions, long Users, long Unfinished, long Abnormal);
public sealed record AuditPage(IReadOnlyList<AuditSession> Rows, AuditSummary Summary, bool DatabaseExists);

// Does not instantiate AuditStore, migrate schemas, recover sessions, or change journal settings.
public sealed class AuditReader(string databasePath)
{
    private const string AbnormalSql = "s.ended_at IS NOT NULL AND COALESCE(s.end_reason,'') NOT IN ('studio_closed','mouse_idle_timeout','maintenance_requested','windows_session_locked')";
    private const string RecordedSql = "EXISTS(SELECT 1 FROM events e WHERE e.session_id=s.session_id AND e.event_type='recording_started')";
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        return connection;
    }
    private static bool HasOrganization(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(sessions)";
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1) == "organization") return true;
        return false;
    }
    private static string Where(SqliteCommand command, AuditFilter filter, bool hasOrganization)
    {
        var clauses = new List<string> { "1=1" };
        var query = filter.Keyword.Trim();
        if (query.Length > 100) throw new ArgumentException("搜索内容最多 100 个字符。");
        if (query.Length > 0)
        {
            var org = hasOrganization ? " OR instr(COALESCE(s.organization,''),$query)>0" : "";
            clauses.Add("(instr(s.name,$query)>0 OR instr(s.student_id,$query)>0 OR instr(s.computer_id,$query)>0" + org + ")");
            command.Parameters.AddWithValue("$query", query);
        }
        if (filter.StudentId is not null) { clauses.Add("s.student_id=$studentId"); command.Parameters.AddWithValue("$studentId", filter.StudentId); }
        if (filter.From.HasValue) { clauses.Add(filter.Overlap ? "(s.ended_at IS NULL OR s.ended_at >= $from)" : "s.started_at >= $from"); command.Parameters.AddWithValue("$from", filter.From.Value.ToUniversalTime().ToString("O")); }
        if (filter.Until.HasValue) { clauses.Add("s.started_at < $until"); command.Parameters.AddWithValue("$until", filter.Until.Value.ToUniversalTime().ToString("O")); }
        if (filter.From.HasValue && filter.Until.HasValue && filter.From >= filter.Until) throw new ArgumentException("结束日期必须不早于开始日期。");
        if (filter.View == SessionView.Unfinished) clauses.Add("s.ended_at IS NULL");
        if (filter.View == SessionView.Abnormal) clauses.Add("(" + AbnormalSql + ")");
        if (filter.View == SessionView.Recorded) clauses.Add(RecordedSql);
        return string.Join(" AND ", clauses);
    }
    private static string Select(bool org) => "SELECT s.session_id,s.student_id,s.name," + (org ? "s.organization" : "NULL") + ",s.computer_id,s.started_at,s.ended_at,s.end_reason," + RecordedSql + " FROM sessions s WHERE ";
    private static AuditSession Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8));

    public AuditPage Query(AuditFilter filter, int page = 1, int pageSize = 20)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        if (!File.Exists(databasePath)) return new([], new(0, 0, 0, 0), false);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var org = HasOrganization(connection, transaction);
        using var summaryCommand = connection.CreateCommand(); summaryCommand.Transaction = transaction;
        var where = Where(summaryCommand, filter, org);
        summaryCommand.CommandText = $"SELECT COUNT(*),COUNT(DISTINCT s.student_id),COALESCE(SUM(s.ended_at IS NULL),0),COALESCE(SUM({AbnormalSql}),0) FROM sessions s WHERE {where}";
        AuditSummary summary;
        using (var r = summaryCommand.ExecuteReader()) { r.Read(); summary = new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)); }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Select(org) + Where(command, filter, org) + " ORDER BY s.started_at DESC,s.session_id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize); command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        var rows = new List<AuditSession>();
        using (var r = command.ExecuteReader()) while (r.Read()) rows.Add(Read(r));
        transaction.Commit();
        return new(rows, summary, true);
    }

    public IReadOnlyList<AuditEvent> Events(string? sessionId, AuditFilter filter, int limit = 500)
    {
        if (!File.Exists(databasePath)) return [];
        using var connection = Open(); using var command = connection.CreateCommand();
        var where = new List<string>();
        if (sessionId is not null) { where.Add("session_id=$id"); command.Parameters.AddWithValue("$id", sessionId); }
        else
        {
            if (filter.From.HasValue) { where.Add("occurred_at >= $from"); command.Parameters.AddWithValue("$from", filter.From.Value.ToUniversalTime().ToString("O")); }
            if (filter.Until.HasValue) { where.Add("occurred_at < $until"); command.Parameters.AddWithValue("$until", filter.Until.Value.ToUniversalTime().ToString("O")); }
        }
        command.CommandText = "SELECT occurred_at,event_type,session_id FROM events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY occurred_at DESC,id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<AuditEvent>(); using var r = command.ExecuteReader();
        while (r.Read()) result.Add(new(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return result;
    }

    public long Export(AuditFilter filter, TextWriter writer)
    {
        writer.WriteLine("会话编号,姓名,学号,组织,电脑,开始时间（本地）,结束时间（本地）,时长,状态,录屏事件");
        if (!File.Exists(databasePath)) return 0;
        using var connection = Open(); using var command = connection.CreateCommand();
        var org = HasOrganization(connection);
        command.CommandText = Select(org) + Where(command, filter, org) + " ORDER BY s.started_at DESC,s.session_id DESC";
        long count = 0; using var r = command.ExecuteReader();
        while (r.Read())
        {
            var s = Read(r);
            writer.WriteLine(string.Join(",", new[] { s.SessionId, s.Name, s.StudentId, s.OrganizationLabel, s.ComputerId,
                s.StartedLabel, s.EndedLabel, s.DurationLabel, s.ReasonLabel, s.HasRecordingEvent ? "有" : "无" }.Select(CsvCell)));
            count++;
        }
        return count;
    }
    public PeoplePage People(AuditFilter filter, int page = 1, int pageSize = 20)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        if (!File.Exists(databasePath)) return new([], 0);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var org = HasOrganization(connection, transaction);
        using var count = connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(DISTINCT s.student_id) FROM sessions s WHERE " + Where(count, filter, org);
        var total = (long)count.ExecuteScalar()!;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT s.student_id,(SELECT n.name FROM sessions n WHERE n.student_id=s.student_id ORDER BY n.started_at DESC,n.session_id DESC LIMIT 1),COUNT(*),MIN(s.started_at),MAX(s.started_at) FROM sessions s WHERE " + Where(command, filter, org) + " GROUP BY s.student_id ORDER BY COUNT(*) DESC,MAX(s.started_at) DESC,s.student_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit",pageSize); command.Parameters.AddWithValue("$offset",(long)(page-1)*pageSize);
        var rows = new List<AuditPerson>();
        using (var r = command.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        transaction.Commit(); return new(rows,total);
    }
    public static string CsvCell(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

public static class AuditLabels
{
    public static string Describe(string type) => type switch
    {
        "admin_view_opened" => "管理员进入记录页面", "admin_view_closed" => "管理员退出记录页面", "admin_idle_timeout" => "管理员超时退出",
        "authenticated" => "身份认证通过", "authentication_failed" => "认证失败", "application_started" => "工作站程序启动",
        "studio_started" => "打开打印软件", "studio_resumed" => "恢复保留工程", "studio_closed" => "正常关闭",
        "mouse_idle_timeout" => "鼠标超时锁定", "windows_session_locked" => "Windows 会话锁定",
        "maintenance_requested" => "进入管理员维护", "recording_started" => "开始录屏", "recording_stopped" => "结束录屏",
        "old_recordings_cleaned" => "已清理旧录像", "application_fault" => "程序或录屏异常",
        "launch_or_audit_failure" => "启动或日志异常", "input_desktop_unavailable" => "输入桌面不可用",
        "recovered_after_interruption" => "异常中断后恢复", "retained_owner_mismatch" => "保留工程用户不匹配",
        "studio_closed_while_locked" => "锁定期间软件退出", _ => type
    };
}
