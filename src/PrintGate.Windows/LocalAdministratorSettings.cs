using System.Text.Json;
using PrintGate.Core;

namespace PrintGate.Windows;

internal static class LocalAdministratorSettings
{
    // Installation ACL permits only Windows administrators/SYSTEM to change this file.
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "local-admin.json");
    internal static LocalAdministratorCredential? Load()
    {
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 8192) throw new InvalidDataException("本地管理员配置过大。");
        var credential = JsonSerializer.Deserialize<LocalAdministratorCredential>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("本地管理员配置为空。");
        credential.Validate(); return credential;
    }
    internal static void Save(LocalAdministratorCredential credential)
    {
        credential.Validate();
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(credential, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
