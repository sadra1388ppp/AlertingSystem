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
            _hasMetadataTable = true;
        }

        EnsureIncidentReasonColumn(db);
        ImportLegacyJsonOnce(db);
    }

    private void EnsureIncidentReasonColumn(AlertingDbContext db)
    {
        var tableName = _useSimplifiedIncidentTable ? "incident_records" : "Incidents";
        db.Database.ExecuteSqlRaw(
            $"ALTER TABLE `{tableName}` ADD COLUMN IF NOT EXISTS Reason TEXT NULL");

        // Avoid rewriting every incident row on every app startup. The backfill
        // and NOT NULL alteration are needed only when the column is nullable.
        var connection = db.Database.GetDbConnection();
        var closeConnectionWhenDone = connection.State != ConnectionState.Open;
        if (closeConnectionWhenDone)
        {
            db.Database.OpenConnection();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT IS_NULLABLE
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND COLUMN_NAME = 'Reason'
                LIMIT 1
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var isNullable = Convert.ToString(command.ExecuteScalar());
            if (!string.Equals(isNullable, "YES", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            db.Database.ExecuteSqlRaw(
                $"UPDATE `{tableName}` SET Reason = '' WHERE Reason IS NULL");
            db.Database.ExecuteSqlRaw(
                $"ALTER TABLE `{tableName}` MODIFY COLUMN Reason TEXT NOT NULL");
        }
        finally
        {
            if (closeConnectionWhenDone)
            {
                db.Database.CloseConnection();
            }
        }
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

    public HashSet<long> LoadCheckHistoryIds(IEnumerable<long> idsToCheck)
    {
        ArgumentNullException.ThrowIfNull(idsToCheck);
        var ids = idsToCheck.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        using var db = CreateContext();
        return db.CheckHistory
            .AsNoTracking()
            .Where(entry => ids.Contains(entry.Id))
            .Select(entry => entry.Id)
            .ToHashSet();
    }

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
        if (!_hasMetadataTable)
        {
            return;
        }

        if (db.AppMetadata.Any(item => item.MetadataKey == ImportMarker))
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
