using System.Text.Json;

namespace PrintGate.Core;

public sealed record RecordingMetadata(string Id, string SessionId, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Status);
public sealed record RecordingCleanupResult(int Deleted, long AvailableBytes, bool HasSpace);

public sealed class RecordingStorage
{
    public string DirectoryPath { get; }
    private readonly Func<long> availableBytes;
    private readonly TimeSpan retention;
    private readonly long minimumFreeBytes;

    public RecordingStorage(string path, int retentionDays, long minimumFreeBytes, Func<long>? availableBytes = null)
    {
        DirectoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(DirectoryPath);
        if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("录像目录不能是符号链接或目录联接。");
        if (retentionDays < 1 || minimumFreeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        retention = TimeSpan.FromDays(retentionDays);
        this.minimumFreeBytes = minimumFreeBytes;
        this.availableBytes = availableBytes ?? (() => new DriveInfo(Path.GetPathRoot(DirectoryPath)!).AvailableFreeSpace);
    }

    public string VideoPath(string id) => Path.Combine(DirectoryPath, "pg-" + ValidId(id) + ".mkv");
    private string MetadataPath(string id) => Path.Combine(DirectoryPath, "pg-" + ValidId(id) + ".json");
    private string LeasePath(string id) => Path.Combine(DirectoryPath, "pg-" + ValidId(id) + ".lease");
    private static string ValidId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new ArgumentException("Invalid recording id.");

    public (RecordingMetadata Metadata, FileStream Lease) Begin(string sessionId)
    {
        var record = new RecordingMetadata(Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow, null, "recording");
        var lease = new FileStream(LeasePath(record.Id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { Write(record); return (record, lease); }
        catch { lease.Dispose(); throw; }
    }

    public void Finish(RecordingMetadata record, string status) => Write(record with { EndedAt = DateTimeOffset.UtcNow, Status = status });

    private void Write(RecordingMetadata metadata)
    {
        var path = MetadataPath(metadata.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
        File.Move(temporary, path, true);
    }

    public RecordingCleanupResult Cleanup(DateTimeOffset now)
    {
        var candidates = new List<RecordingMetadata>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "pg-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (IsLink(path) || new FileInfo(path).Length > 8192) continue;
                var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path));
                if (record is null || !Guid.TryParseExact(record.Id, "N", out _) || Path.GetFileName(path) != $"pg-{record.Id}.json") continue;
                if (IsLink(LeasePath(record.Id))) continue;
                // An exclusive lease prevents deleting the active recording even before FFmpeg opens it.
                using var lease = new FileStream(LeasePath(record.Id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var video = VideoPath(record.Id);
                if (IsLink(video)) continue;
                if (record.EndedAt is null)
                {
                    // After a crash, recover only files no encoder still holds open.
                    if (File.Exists(video))
                    {
                        using var probe = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    record = record with { EndedAt = now, Status = "interrupted" };
                    Write(record);
                }
                candidates.Add(record);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        var deleted = 0;
        // Earliest completed video first; future timestamps do not make an active file eligible.
        foreach (var record in candidates.OrderBy(r => r.EndedAt).ThenBy(r => r.StartedAt))
        {
            if (record.EndedAt > now - retention && availableBytes() >= minimumFreeBytes) continue;
            try
            {
                if (IsLink(LeasePath(record.Id))) continue;
                using (var lease = new FileStream(LeasePath(record.Id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var video = VideoPath(record.Id);
                    if (IsLink(video)) continue;
                    if (File.Exists(video))
                    {
                        using (var probe = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        File.Delete(video); // A still-open encoder on Windows will make this fail safely.
                    }
                    File.Delete(MetadataPath(record.Id));
                    deleted++;
                }
                File.Delete(LeasePath(record.Id));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var remaining = availableBytes();
        return new RecordingCleanupResult(deleted, remaining, remaining >= minimumFreeBytes);
    }

    private static bool IsLink(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
