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
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewAssistant");
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
