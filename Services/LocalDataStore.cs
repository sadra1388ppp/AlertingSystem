using System.Text.Json;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class LocalDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem");

    public List<Incident> LoadIncidents() => LoadList<Incident>("incidents.json");

    public List<CheckHistoryEntry> LoadCheckHistory() => LoadList<CheckHistoryEntry>("check-history.json");

    public void SaveIncidents(IEnumerable<Incident> incidents) =>
        SaveList("incidents.json", incidents);

    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history) =>
        SaveList("check-history.json", history);

    private List<T> LoadList<T>(string fileName)
    {
        var path = Path.Combine(_directory, fileName);

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Start with an empty in-memory collection if a local file is unreadable.
            // A later save will write valid JSON again.
            return [];
        }
    }

    private void SaveList<T>(string fileName, IEnumerable<T> items)
    {
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, fileName);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(items, JsonOptions);

        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
