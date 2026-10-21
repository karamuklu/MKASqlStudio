using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace WindowsMssqlStudio;

public sealed class ConnectionProfile
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool WindowsAuthentication { get; set; }
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string DisplayName => $"{Server} · {Database}";
}

public static class ConnectionProfileStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MkaSqlStudio", "connections.json");

    public static IReadOnlyList<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var protectedProfiles = JsonSerializer.Deserialize<List<StoredConnectionProfile>>(File.ReadAllText(FilePath)) ?? [];
            return protectedProfiles.Select(profile => new ConnectionProfile
            {
                Server = profile.Server,
                Database = profile.Database,
                WindowsAuthentication = profile.WindowsAuthentication,
                UserName = profile.UserName,
                Password = Unprotect(profile.Password)
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(ConnectionProfile profile)
    {
        var profiles = Load().ToList();
        profiles.RemoveAll(item => string.Equals(item.Server, profile.Server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Database, profile.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.UserName, profile.UserName, StringComparison.OrdinalIgnoreCase));
        profiles.Insert(0, profile);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stored = profiles.Select(item => new StoredConnectionProfile
        {
            Server = item.Server,
            Database = item.Database,
            WindowsAuthentication = item.WindowsAuthentication,
            UserName = item.UserName,
            Password = Protect(item.Password)
        });
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private sealed class StoredConnectionProfile
    {
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public bool WindowsAuthentication { get; set; }
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
    }
}

