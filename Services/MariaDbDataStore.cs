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

        // Bootstrap a new database using the old model first. The EF Core migration
        // below renames those tables. Existing databases are left untouched by EnsureCreated.
        using (var bootstrap = new AlertingDbContext(_connectionString, useLegacyTableNames: true))
        {
            bootstrap.Database.EnsureCreated();
        }

        using var db = CreateContext();
        db.Database.Migrate();
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

    public HashSet<long> LoadCheckHistoryIds()
    {
        using var db = CreateContext();
        return db.CheckHistory
            .AsNoTracking()
            .Select(entry => entry.Id)
            .ToHashSet();
    }

    // Updates existing incidents only. Missing rows are not recreated because they
    // may have been deleted directly from MariaDB while the app was open.
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

    public void SaveIncident(Incident incident, bool allowInsert = false)
    {
        using var db = CreateContext();
        var stored = db.Incidents.FirstOrDefault(
            existing => existing.IncidentNumber == incident.IncidentNumber);

        if (stored is null)
        {
            if (!allowInsert)
            {
                return;
            }

            db.Incidents.Add(incident);
        }
        else
        {
            db.Entry(stored).CurrentValues.SetValues(incident);
        }

        db.SaveChanges();
    }

    // Non-destructive bulk upsert. It never deletes rows omitted from the list.
    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history)
    {
        using var db = CreateContext();

        foreach (var item in history.OrderByDescending(entry => entry.Timestamp)
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
            else
            {
                db.CheckHistory.Add(item);
            }
        }

        db.SaveChanges();
    }

    public void SaveCheckHistoryEntry(CheckHistoryEntry entry)
    {
        using var db = CreateContext();

        if (entry.Id > 0)
        {
            var stored = db.CheckHistory.Find(entry.Id);

            // Do not resurrect an entry that was deleted directly in MariaDB.
            if (stored is null)
            {
                return;
            }

            db.Entry(stored).CurrentValues.SetValues(entry);
        }
        else
        {
            db.CheckHistory.Add(entry);
        }

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
