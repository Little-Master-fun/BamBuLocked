using System.Text.Json;
using PrintGate.Core;

namespace PrintGate.Windows;

// Stored under the kiosk user's profile. Only a salted password verifier is written;
// neither the campus password nor any CAS ticket is persisted.
internal sealed class CampusCredentialStore : ICampusCredentialCache
{
    private const int MaximumEntries = 5000;
    private static readonly object gate = new();
    private readonly string path;
    private List<CachedCampusCredential> entries;

    private CampusCredentialStore(string path, List<CachedCampusCredential> entries)
    { this.path = path; this.entries = entries; }

    internal static CampusCredentialStore Load()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintGate");
        var path = Path.Combine(directory, "campus-accounts.json");
        if (!File.Exists(path)) return new(path, []);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("本地认证缓存过大。");
        var entries = JsonSerializer.Deserialize<List<CachedCampusCredential>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("本地认证缓存为空。");
        if (entries.Count > MaximumEntries) throw new InvalidDataException("本地认证缓存条目过多。");
        foreach (var entry in entries) entry.Validate();
        if (entries.Select(x => x.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("本地认证缓存包含重复账号。");
        return new(path, entries);
    }

    public CachedCampusCredential? Find(string account)
    {
        lock (gate) return entries.FirstOrDefault(x => x.MatchesAccount(account));
    }

    public void Save(CachedCampusCredential credential)
    {
        credential.Validate();
        lock (gate)
        {
            var next = entries.Where(x => !x.MatchesAccount(credential.Account)).Append(credential).ToList();
            if (next.Count > MaximumEntries) throw new InvalidDataException("本地认证缓存已达上限。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                entries = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
