using System.Text.Json;
using PrintGate.Core;
using Xunit;

namespace PrintGate.Tests;

public sealed class RecordingStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PrintGate-recordings-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private RecordingStorage Store(Func<long>? free = null) => new(directory, 7, 100, free ?? (() => 1000));

    private string Video(int ageDays, int size = 100)
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var metadata = new RecordingMetadata(id, "session-test", now.AddDays(-ageDays).AddMinutes(-10), now.AddDays(-ageDays), "completed");
        File.WriteAllText(Path.Combine(directory, $"pg-{id}.json"), JsonSerializer.Serialize(metadata));
        var path = Path.Combine(directory, $"pg-{id}.mkv");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact] public void DeletesOldestFirstAndStopsAsSoonAsSpaceIsEnough()
    {
        var oldest = Video(3); var newer = Video(1);
        var store = Store(() => File.Exists(oldest) ? 0 : 100);
        var result = store.Cleanup(now);
        Assert.Equal(1, result.Deleted); Assert.True(result.HasSpace);
        Assert.False(File.Exists(oldest)); Assert.True(File.Exists(newer));
    }

    [Fact] public void DeletesMultipleOldVideosIfOneIsNotEnough()
    {
        var first = Video(5); var second = Video(3); var third = Video(1);
        var store = Store(() => (File.Exists(first) ? 0 : 50) + (File.Exists(second) ? 0 : 50));
        Assert.True(store.Cleanup(now).HasSpace);
        Assert.False(File.Exists(first)); Assert.False(File.Exists(second)); Assert.True(File.Exists(third));
    }

    [Fact] public void ExpiryIsEnforcedEvenWhenDiskHasPlentyOfSpace()
    {
        var expired = Video(7); var recent = Video(6);
        Assert.Equal(1, Store().Cleanup(now).Deleted);
        Assert.False(File.Exists(expired)); Assert.True(File.Exists(recent));
    }

    [Fact] public void NeverDeletesActiveRecordingOrUnrelatedFiles()
    {
        var store = Store(() => 0);
        var (record, lease) = store.Begin("active-session");
        using (lease)
        {
            var path = store.VideoPath(record.Id); File.WriteAllBytes(path, new byte[10]);
            var unrelated = Path.Combine(directory, "personal.mkv"); File.WriteAllText(unrelated, "keep");
            var result = store.Cleanup(now.AddDays(10));
            Assert.False(result.HasSpace); Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(path)); Assert.True(File.Exists(unrelated));
        }
    }

    [Fact] public void ReportsInsufficientSpaceAfterAllEligibleVideosAreDeleted()
    {
        Video(1);
        var result = Store(() => 0).Cleanup(now);
        Assert.Equal(1, result.Deleted); Assert.False(result.HasSpace);
    }

    [Fact] public void CannotDeleteFilesOutsideRecordingDirectoryThroughMetadata()
    {
        var store = Store(() => 0);
        File.WriteAllText(Path.Combine(directory, "pg-bad.json"), JsonSerializer.Serialize(new RecordingMetadata("../outside", "x", now, now, "completed")));
        Assert.Equal(0, store.Cleanup(now).Deleted);
    }

    [Fact] public void RecoversAbandonedRecordingAfterLeaseIsReleased()
    {
        var store = Store();
        var (record, lease) = store.Begin("crashed-session");
        File.WriteAllBytes(store.VideoPath(record.Id), new byte[10]); lease.Dispose();
        store.Cleanup(now);
        var saved = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(Path.Combine(directory, $"pg-{record.Id}.json")))!;
        Assert.Equal("interrupted", saved.Status); Assert.Equal(now, saved.EndedAt);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
