using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterviewAssistant;

public sealed class Storage
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string DirectoryPath => _directory;

    public Storage(string? directory = null)
    {
        if (directory is not null)
        {
            _directory = directory;
            return;
        }

        // LocalAppData can be redirected into a package-private folder when launched
        // from a packaged host. A folder in the user's profile is shared by both launch paths.
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".interview-assistant");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewAssistant");
        if (!File.Exists(Path.Combine(shared, "data.json")) && File.Exists(Path.Combine(legacy, "data.json")))
        {
            try { CopyLegacyData(legacy, shared); }
            catch
            {
                // Keep the old data accessible if migration cannot finish.
                _directory = legacy;
                return;
            }
        }
        _directory = shared;
    }

    private static void CopyLegacyData(string legacy, string shared)
    {
        Directory.CreateDirectory(shared);
        foreach (var name in new[] { "deepseek.secret", "tencent.secret", "data.json" })
        {
            var source = Path.Combine(legacy, name);
            var destination = Path.Combine(shared, name);
            if (!File.Exists(source) || File.Exists(destination)) continue;
            // Read/write bytes instead of File.Copy: the old package store may use EFS,
            // which cannot preserve its encryption attribute at the shared destination.
            var temporary = destination + ".migration.tmp";
            File.WriteAllBytes(temporary, File.ReadAllBytes(source));
            File.Move(temporary, destination);
        }
    }

    public AppData Load()
    {
        var path = Path.Combine(_directory, "data.json");
        if (!File.Exists(path)) return new AppData();
        return JsonSerializer.Deserialize<AppData>(File.ReadAllText(path), _json) ?? new AppData();
    }

    public void Save(AppData data)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "data.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, _json));
        File.Move(temporary, path, true);
    }

    public void SaveSecret(string name, string value)
    {
        Directory.CreateDirectory(_directory);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_directory, name + ".secret"), bytes);
    }

    public string ReadSecret(string name)
    {
        var path = Path.Combine(_directory, name + ".secret");
        if (!File.Exists(path)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return ""; }
    }
}
