using System.Text.Json;

namespace PrintGate.Core;

public sealed record IndexedRecording(RecordingMetadata Metadata, string Path, bool Exists, long Bytes)
{
    public string Id => Metadata.Id;
    public string StartedLabel => Metadata.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string StatusLabel => !Exists ? "文件缺失" : Metadata.EndedAt is null ? "未结束（待确认）" : Metadata.Status switch
    { "completed" => "可查看", "interrupted" => "中断录像", "start_failed" => "启动失败", _ => Metadata.Status };
    public string SizeLabel => $"{Bytes / 1048576d:0.0} MB";
    public bool CanOpen => Exists && Metadata.EndedAt is not null;
}
public sealed record RecordingIndexResult(IReadOnlyList<IndexedRecording> Recordings, int Skipped);

// Read-only catalog: never recover, prune, or rewrite the recorder's metadata/leases.
public sealed class RecordingIndex(string directory)
{
    public RecordingIndexResult ForSession(string sessionId)
    {
        if (!Directory.Exists(directory)) return new([], 0);
        RejectLinks(directory);
        var rows = new List<IndexedRecording>(); var skipped = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "pg-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                RejectLinks(path);
                if (new FileInfo(path).Length > 8192) { skipped++; continue; }
                var m = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path));
                if (m is null || !Guid.TryParseExact(m.Id, "N", out _) || Path.GetFileName(path) != $"pg-{m.Id}.json") { skipped++; continue; }
                if (m.SessionId != sessionId) continue;
                var video = Path.GetFullPath(Path.Combine(directory, $"pg-{m.Id}.mkv"));
                RejectLinks(video);
                var file = new FileInfo(video);
                rows.Add(new(m, video, file.Exists, file.Exists ? file.Length : 0));
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (JsonException) { skipped++; }
        }
        return new(rows.OrderByDescending(r => r.Metadata.StartedAt).ToList(), skipped);
    }
    public string ResolveForPlayback(string sessionId, string recordingId)
    {
        if (!Guid.TryParseExact(recordingId, "N", out _)) throw new IOException("无效录像编号。");
        var record = ForSession(sessionId).Recordings.SingleOrDefault(r => r.Id == recordingId);
        if (record is null || !record.CanOpen) throw new IOException("录像已清理、缺失或尚未结束。");
        return record.Path;
    }
    private static void RejectLinks(string path)
    {
        // Also check parents: a configured root or child can be replaced by a junction.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不读取符号链接或目录联接中的录像。");
    }
}
