using System.IO;
using System.Text.Json;
using AlertingSystem.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace AlertingSystem.Backend.Services;

public sealed class MariaDbDataStore
{
    private const string ImportMarker = "LegacyJsonImported_EFCore_V1";
    private const int MaximumHistoryEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly AlertingDbContextFactory _contextFactory;
    private readonly bool _hasMetadataTable;

    public MariaDbDataStore(AlertingDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
        _directory = contextFactory.DataDirectory;
        _hasMetadataTable = contextFactory.HasMetadataTable;

        using var db = CreateContext();
        ImportLegacyJsonOnce(db);
    }

    public List<Incident> LoadIncidents()
    {
        using var db = CreateContext();
        return db.Incidents.AsNoTracking().ToList();
    }

    public List<CheckHistoryEntry> LoadCheckHistory()
    {
        using var db = CreateContext();

        return db.CheckHistory
            .AsNoTracking()
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Id)
            .Take(MaximumHistoryEntries)
            .ToList();
    }

    public void SaveIncidents(IEnumerable<Incident> incidents)
    {
        using var db = CreateContext();

        foreach (var item in incidents)
        {
            var stored = db.Incidents.FirstOrDefault(
                existing => existing.IncidentNumber == item.IncidentNumber);

            if (stored is not null)
            {
                db.Entry(stored).CurrentValues.SetValues(item);
            }
        }

        db.SaveChanges();
    }

    public bool SaveIncident(Incident incident, bool allowInsert = false)
    {
        using var db = CreateContext();
        var stored = db.Incidents.FirstOrDefault(
            existing => existing.IncidentNumber == incident.IncidentNumber);

        if (stored is null)
        {
            if (!allowInsert)
            {
                return false;
            }

            db.Incidents.Add(incident);
        }
        else
        {
            db.Entry(stored).CurrentValues.SetValues(incident);
        }

        db.SaveChanges();
        return true;
    }

    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history)
    {
        using var db = CreateContext();

        foreach (var item in history
                     .OrderByDescending(entry => entry.Timestamp)
                     .Take(MaximumHistoryEntries))
        {
            if (item.Id > 0)
            {
                var stored = db.CheckHistory.Find(item.Id);
                if (stored is not null)
                {
                    db.Entry(stored).CurrentValues.SetValues(item);
                }
            }
            else if (item.Id == 0)
            {
                db.CheckHistory.Add(item);
            }
        }

        db.SaveChanges();
        TrimHistory(db);
    }

    public bool SaveCheckHistoryEntry(CheckHistoryEntry entry)
    {
        using var db = CreateContext();

        if (entry.Id > 0)
        {
            var stored = db.CheckHistory.Find(entry.Id);
            if (stored is null)
            {
                return false;
            }

            db.Entry(stored).CurrentValues.SetValues(entry);
        }
        else
        {
            db.CheckHistory.Add(entry);
        }

        db.SaveChanges();
        TrimHistory(db);
        return true;
    }

    private static void TrimHistory(AlertingDbContext db)
    {
        var oldEntries = db.CheckHistory
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Id)
            .Skip(MaximumHistoryEntries)
            .ToList();

        if (oldEntries.Count == 0)
            return;

        db.CheckHistory.RemoveRange(oldEntries);
        db.SaveChanges();
    }

    private AlertingDbContext CreateContext() => _contextFactory.CreateContext();

    private void ImportLegacyJsonOnce(AlertingDbContext db)
    {
        if (!_hasMetadataTable ||
            db.AppMetadata.Any(item => item.MetadataKey == ImportMarker))
        {
            return;
        }

        if (!db.Incidents.Any() && !db.CheckHistory.Any())
        {
            var incidents = LoadLegacyJson<Incident>("incidents.json");
            var history = LoadLegacyJson<CheckHistoryEntry>("check-history.json")
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaximumHistoryEntries)
                .ToList();

            db.Incidents.AddRange(incidents);
            db.CheckHistory.AddRange(history);
        }

        db.AppMetadata.Add(new AppMetadata
        {
            MetadataKey = ImportMarker,
            MetadataValue = DateTime.UtcNow.ToString("O")
        });

        db.SaveChanges();
    }

    private List<T> LoadLegacyJson<T>(string fileName)
    {
        var path = Path.Combine(_directory, fileName);

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(
                File.ReadAllText(path),
                JsonOptions) ?? [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Could not import the existing data file '{path}'. The original file has been left unchanged.",
                exception);
        }
    }
}
