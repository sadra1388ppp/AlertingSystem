using System.IO;
using System.Text.Json;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class HiddenItemsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem",
        "hidden-items.json");

    private readonly HashSet<string> _incidentNumbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _historyIds = [];

    public HiddenItemsStore()
    {
        Load();
    }

    public bool IsIncidentHidden(string incidentNumber) =>
        _incidentNumbers.Contains(incidentNumber);

    public bool IsHistoryEntryHidden(long id) => id > 0 && _historyIds.Contains(id);

    // Only hidden history IDs need reconciliation during periodic sync.
    public long[] GetHiddenHistoryIds() => _historyIds.ToArray();

    public void HideIncident(string incidentNumber)
    {
        if (_incidentNumbers.Add(incidentNumber))
        {
            Save();
        }
    }

    public void HideHistoryEntry(long id)
    {
        if (id > 0 && _historyIds.Add(id))
        {
            Save();
        }
    }

    public void Reconcile(
        IEnumerable<Incident> databaseIncidents,
        IEnumerable<long> databaseHistoryIds)
    {
        var incidentNumbers = databaseIncidents
            .Select(item => item.IncidentNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var historyIds = databaseHistoryIds.ToHashSet();

        var removedIncidents = _incidentNumbers.RemoveWhere(
            incidentNumber => !incidentNumbers.Contains(incidentNumber));
        var removedHistory = _historyIds.RemoveWhere(
            id => !historyIds.Contains(id));

        if (removedIncidents > 0 || removedHistory > 0)
        {
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HiddenItemsDocument>(
                File.ReadAllText(_path));

            if (document is null)
            {
                return;
            }

            _incidentNumbers.UnionWith(document.IncidentNumbers);
            _historyIds.UnionWith(document.HistoryIds);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var document = new HiddenItemsDocument
        {
            IncidentNumbers = _incidentNumbers.ToList(),
            HistoryIds = _historyIds.ToList()
        };

        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class HiddenItemsDocument
    {
        public List<string> IncidentNumbers { get; set; } = [];
        public List<long> HistoryIds { get; set; } = [];
    }
}
