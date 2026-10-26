using System.Data;
using System.IO;
using System.Text.Json;
using AlertingSystem.Models;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

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
    private readonly bool _useSimplifiedIncidentTable;
    private readonly bool _useSimplifiedHistoryTable;
    private readonly bool _useSimplifiedMetadataTable;
    private bool _hasMetadataTable;

    public MariaDbDataStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlertingSystem");

        Directory.CreateDirectory(_directory);
        _connectionString = DatabaseSettings.LoadConnectionString(_directory);

        // The app may have been rolled back while MariaDB retained newer table names.
        // If the check-history table is still named "checks", rename it through an EF Core
        // migration so existing rows and indexes remain intact.
        var existingTables = ReadTableNames(_connectionString);
        if (existingTables.Contains("checks") && !existingTables.Contains("AlertHistory"))
        {
            using var migrationContext = new AlertingDbContext(
                _connectionString,
                useSimplifiedIncidentTable: ChooseSimplifiedTable(
                    existingTables, "incident_records", "Incidents"),
                useSimplifiedHistoryTable: true,
                useSimplifiedMetadataTable: ChooseSimplifiedTable(
                    existingTables, "settings", "AppMetadata"));
            migrationContext.Database.Migrate();
            existingTables = ReadTableNames(_connectionString);
        }

        _useSimplifiedIncidentTable = ChooseSimplifiedTable(
            existingTables, "incident_records", "Incidents");
        _useSimplifiedHistoryTable = ChooseSimplifiedTable(
            existingTables, "checks", "AlertHistory");
        _useSimplifiedMetadataTable = ChooseSimplifiedTable(
            existingTables, "settings", "AppMetadata");
        _hasMetadataTable = existingTables.Contains("AppMetadata") || existingTables.Contains("settings");

        using var db = CreateContext();
        var databaseCreated = db.Database.EnsureCreated();

        if (databaseCreated)
        {
            // A fresh database is created using the original schema, which includes metadata.
            _hasMetadataTable = true;
        }

        EnsureIncidentReasonColumn(db);
        ImportLegacyJsonOnce(db);
    }

    private void EnsureIncidentReasonColumn(AlertingDbContext db)
    {
        // EnsureCreated does not add columns to an existing database. Upgrade the existing
        // incident table in place so all prior incident records are preserved.
        var tableName = _useSimplifiedIncidentTable ? "incident_records" : "Incidents";
        db.Database.ExecuteSqlRaw($"ALTER TABLE `{tableName}` ADD COLUMN IF NOT EXISTS `Reason` TEXT NULL");
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

    // Updates rows that already exist. Missing rows are never recreated here,
    // and rows omitted from the in-memory list are never deleted.
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

    public HashSet<long> LoadCheckHistoryIds()
    {
        using var db = CreateContext();
        return db.CheckHistory
            .AsNoTracking()
            .Select(entry => entry.Id)
            .ToHashSet();
    }

    // Updates existing history, inserts only genuinely new entries, and never
    // removes older rows just because they are no longer displayed in the app.
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
            else if (item.Id == 0)
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

            // Do not resurrect a row deleted directly in MariaDB.
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

    private AlertingDbContext CreateContext() => new(
        _connectionString,
        _useSimplifiedIncidentTable,
        _useSimplifiedHistoryTable,
        _useSimplifiedMetadataTable);

    private static bool ChooseSimplifiedTable(
        HashSet<string> existingTables,
        string simplifiedName,
        string originalName)
    {
        return existingTables.Contains(simplifiedName) && !existingTables.Contains(originalName);
    }

    private static HashSet<string> ReadTableNames(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        var schema = connection.GetSchema("Tables");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DataRow row in schema.Rows)
        {
            var schemaName = row["TABLE_SCHEMA"]?.ToString();
            if (!string.Equals(schemaName, builder.Database, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tableName = row["TABLE_NAME"]?.ToString();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                names.Add(tableName);
            }
        }

        return names;
    }

    private void ImportLegacyJsonOnce(AlertingDbContext db)
    {
        // AppMetadata only stores the legacy-import marker. Some older or partially
        // migrated databases never had this table; don't let that optional marker
        // stop the main incident/history screens from opening or risk duplicate imports.
        if (!_hasMetadataTable)
        {
            return;
        }

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
