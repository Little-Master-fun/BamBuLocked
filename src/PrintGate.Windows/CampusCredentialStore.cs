using System.Text.Json;
using PrintGate.Core;

namespace PrintGate.Windows;

// Stored under the kiosk user's profile. It contains identity data only; campus
// passwords, password verifiers and CAS tickets are never persisted.
internal sealed class CampusCredentialStore : ICampusCredentialCache
{
    private const int MaximumEntries = 5000;
    private static readonly object gate = new();
    private readonly string path;
    private List<CachedCampusIdentity> entries;

    private CampusCredentialStore(string path, List<CachedCampusIdentity> entries)
    { this.path = path; this.entries = entries; }

    internal static CampusCredentialStore Load()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintGate");
        var path = Path.Combine(directory, "campus-accounts.json");
        if (!File.Exists(path)) return new(path, []);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("本地认证缓存过大。");
        var entries = JsonSerializer.Deserialize<List<CachedCampusIdentity>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("本地认证缓存为空。");
        if (entries.Count > MaximumEntries) throw new InvalidDataException("本地认证缓存条目过多。");
        foreach (var entry in entries) entry.Validate();
        if (entries.Select(x => x.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("本地认证缓存包含重复账号。");
        var store = new CampusCredentialStore(path, entries);
        // Rewrite caches created by earlier releases to remove their legacy password hash fields.
        store.Write(entries);
        return store;
    }

    public CachedCampusIdentity? Find(string account)
    {
        lock (gate) return entries.FirstOrDefault(x => x.MatchesAccount(account));
    }

    public void Save(CachedCampusIdentity credential)
    {
        credential.Validate();
        lock (gate)
        {
            var next = entries.Where(x => !x.MatchesAccount(credential.Account)).Append(credential).ToList();
            if (next.Count > MaximumEntries) throw new InvalidDataException("本地认证缓存已达上限。");
            Write(next);
            entries = next;
        }
    }

    private void Write(List<CachedCampusIdentity> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
