using System.IO;
using System.Text.Json;
using AlertingSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace AlertingSystem.Services;

public sealed class MariaDbDataStore
{
    private const string ImportMarker = "LegacyJsonImported_EFCore_V1";
    private const int MaximumHistoryEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _connectionString;

    public MariaDbDataStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlertingSystem");

        Directory.CreateDirectory(_directory);
        _connectionString = DatabaseSettings.LoadConnectionString(_directory);

        using var db = CreateContext();
        db.Database.EnsureCreated();
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
        var snapshot = incidents.ToList();

        using var db = CreateContext();
        var existing = db.Incidents.ToList();
        var existingByNumber = existing.ToDictionary(
            item => item.IncidentNumber,
            StringComparer.OrdinalIgnoreCase);
        var retainedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in snapshot)
        {
            retainedNumbers.Add(item.IncidentNumber);

            if (existingByNumber.TryGetValue(item.IncidentNumber, out var stored))
            {
                db.Entry(stored).CurrentValues.SetValues(item);
            }
            else
            {
                db.Incidents.Add(item);
            }
        }

        db.Incidents.RemoveRange(
            existing.Where(item => !retainedNumbers.Contains(item.IncidentNumber)));

        db.SaveChanges();
    }

    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history)
    {
        var snapshot = history
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaximumHistoryEntries)
            .ToList();

        using var db = CreateContext();
        var existing = db.CheckHistory.ToList();
        var existingById = existing.ToDictionary(item => item.Id);
        var retainedIds = new HashSet<long>();

        foreach (var item in snapshot)
        {
            if (item.Id > 0 && existingById.TryGetValue(item.Id, out var stored))
            {
                retainedIds.Add(item.Id);
                db.Entry(stored).CurrentValues.SetValues(item);
            }
            else
            {
                db.CheckHistory.Add(item);
            }
        }

        db.CheckHistory.RemoveRange(existing.Where(item => !retainedIds.Contains(item.Id)));
        db.SaveChanges();
    }

    private AlertingDbContext CreateContext() => new(_connectionString);

    private void ImportLegacyJsonOnce(AlertingDbContext db)
    {
        if (db.AppMetadata.Any(item => item.MetadataKey == ImportMarker))
        {
            return;
        }

        // Do not merge stale JSON into a database that already contains records.
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
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? [];
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
