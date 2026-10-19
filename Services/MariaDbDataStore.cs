using System.Globalization;
using System.IO;
using System.Text.Json;
using AlertingSystem.Models;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace AlertingSystem.Services;

public sealed class MariaDbDataStore
{
    private const string MigrationMarker = "LocalDatabaseMigrationV1";
    private const int MaximumHistoryEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem");

    private readonly string _settingsPath;
    private readonly string _sqlitePath;
    private readonly string _connectionString;

    public MariaDbDataStore()
    {
        Directory.CreateDirectory(_directory);

        _settingsPath = Path.Combine(_directory, "database-settings.json");
        _sqlitePath = Path.Combine(_directory, "alertingsystem.db");
        _connectionString = LoadConnectionString();

        InitializeDatabase();
    }

    public List<Incident> LoadIncidents()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IncidentNumber, EndpointUrl, Title, Details, ResolutionNote,
                   Severity, Status, FirstOccurredAt, LastOccurredAt, ResolvedAt,
                   OccurrenceCount
            FROM Incidents;
            """;

        using var reader = command.ExecuteReader();
        var incidents = new List<Incident>();

        while (reader.Read())
        {
            incidents.Add(new Incident
            {
                IncidentNumber = reader.GetString(0),
                EndpointUrl = reader.GetString(1),
                Title = reader.GetString(2),
                Details = reader.GetString(3),
                ResolutionNote = reader.GetString(4),
                Severity = (IncidentSeverity)reader.GetInt32(5),
                Status = (IncidentStatus)reader.GetInt32(6),
                FirstOccurredAt = ParseDateTime(reader.GetString(7)),
                LastOccurredAt = ParseDateTime(reader.GetString(8)),
                ResolvedAt = reader.IsDBNull(9) ? null : ParseDateTime(reader.GetString(9)),
                OccurrenceCount = reader.GetInt32(10)
            });
        }

        return incidents;
    }

    public List<CheckHistoryEntry> LoadCheckHistory()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CheckedAt, Endpoint, Result, Details, ResponseTimeMs
            FROM AlertHistory
            ORDER BY CheckedAt DESC, Id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", MaximumHistoryEntries);

        using var reader = command.ExecuteReader();
        var history = new List<CheckHistoryEntry>();

        while (reader.Read())
        {
            history.Add(new CheckHistoryEntry
            {
                Timestamp = ParseDateTime(reader.GetString(0)),
                Endpoint = reader.GetString(1),
                Result = reader.GetString(2),
                Details = reader.GetString(3),
                ResponseTimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4)
            });
        }

        return history;
    }

    public void SaveIncidents(IEnumerable<Incident> incidents)
    {
        var snapshot = incidents.ToList();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM Incidents;";
            deleteCommand.ExecuteNonQuery();
        }

        InsertIncidents(connection, transaction, snapshot, ignoreDuplicates: false);
        transaction.Commit();
    }

    public void SaveCheckHistory(IEnumerable<CheckHistoryEntry> history)
    {
        var snapshot = history
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaximumHistoryEntries)
            .ToList();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM AlertHistory;";
            deleteCommand.ExecuteNonQuery();
        }

        InsertCheckHistory(connection, transaction, snapshot);
        transaction.Commit();
    }

    private MySqlConnection OpenConnection()
    {
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private string LoadConnectionString()
    {
        // An environment variable can override the local settings file when desired.
        var environmentConnectionString =
            Environment.GetEnvironmentVariable("ALERTINGSYSTEM_DB_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(environmentConnectionString))
        {
            var environmentBuilder = new MySqlConnectionStringBuilder(environmentConnectionString)
            {
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 30
            };

            return environmentBuilder.ConnectionString;
        }

        if (!File.Exists(_settingsPath))
        {
            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(new DatabaseSettings(), JsonOptions));

            throw new InvalidOperationException(
                "A MariaDB settings file has been created at:\n" +
                _settingsPath +
                "\n\nSet its Password value, save the file, and launch AlertingSystem again.");
        }

        DatabaseSettings settings;

        try
        {
            settings = JsonSerializer.Deserialize<DatabaseSettings>(
                File.ReadAllText(_settingsPath)) ?? new DatabaseSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Could not read the MariaDB settings file at '{_settingsPath}'.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(settings.Server) ||
            string.IsNullOrWhiteSpace(settings.Database) ||
            string.IsNullOrWhiteSpace(settings.UserId) ||
            string.IsNullOrWhiteSpace(settings.Password))
        {
            throw new InvalidOperationException(
                "Open the MariaDB settings file and fill in Server, Database, UserId, and Password:\n" +
                _settingsPath);
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Server,
            Port = settings.Port,
            Database = settings.Database,
            UserID = settings.UserId,
            Password = settings.Password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 30
        };

        return builder.ConnectionString;
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();

        ExecuteSchemaCommand(connection, """
            CREATE TABLE IF NOT EXISTS Incidents
            (
                IncidentNumber VARCHAR(100) NOT NULL PRIMARY KEY,
                EndpointUrl TEXT NOT NULL,
                Title VARCHAR(512) NOT NULL,
                Details TEXT NOT NULL,
                ResolutionNote TEXT NOT NULL,
                Severity INT NOT NULL,
                Status INT NOT NULL,
                FirstOccurredAt VARCHAR(40) NOT NULL,
                LastOccurredAt VARCHAR(40) NOT NULL,
                ResolvedAt VARCHAR(40) NULL,
                OccurrenceCount INT NOT NULL,
                KEY IX_Incidents_Status_LastOccurredAt (Status, LastOccurredAt)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);

        ExecuteSchemaCommand(connection, """
            CREATE TABLE IF NOT EXISTS AlertHistory
            (
                Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                CheckedAt VARCHAR(40) NOT NULL,
                Endpoint TEXT NOT NULL,
                Result VARCHAR(255) NOT NULL,
                Details TEXT NOT NULL,
                ResponseTimeMs BIGINT NULL,
                KEY IX_AlertHistory_CheckedAt (CheckedAt)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);

        ExecuteSchemaCommand(connection, """
            CREATE TABLE IF NOT EXISTS AppMetadata
            (
                MetadataKey VARCHAR(100) NOT NULL PRIMARY KEY,
                MetadataValue VARCHAR(100) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);

        MigrateLocalDataOnce(connection);
    }

    private static void ExecuteSchemaCommand(MySqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void MigrateLocalDataOnce(MySqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.Transaction = transaction;
            markerCommand.CommandText =
                "SELECT MetadataValue FROM AppMetadata WHERE MetadataKey = @key LIMIT 1;";
            markerCommand.Parameters.AddWithValue("@key", MigrationMarker);

            if (markerCommand.ExecuteScalar() is not null)
            {
                transaction.Commit();
                return;
            }
        }

        var incidentCount = GetTableCount(connection, transaction, "Incidents");
        var historyCount = GetTableCount(connection, transaction, "AlertHistory");

        // Never overwrite or merge into a MariaDB database that already has data.
        if (incidentCount == 0 && historyCount == 0)
        {
            var localData = ReadExistingSqliteSnapshot();

            var incidents = localData?.Incidents ?? LoadLegacyJson<Incident>("incidents.json");
            var history = localData?.History ?? LoadLegacyJson<CheckHistoryEntry>("check-history.json");

            InsertIncidents(connection, transaction, incidents, ignoreDuplicates: true);
            InsertCheckHistory(
                connection,
                transaction,
                history.OrderByDescending(entry => entry.Timestamp)
                    .Take(MaximumHistoryEntries)
                    .ToList());
        }

        using (var saveMarkerCommand = connection.CreateCommand())
        {
            saveMarkerCommand.Transaction = transaction;
            saveMarkerCommand.CommandText = """
                INSERT INTO AppMetadata (MetadataKey, MetadataValue)
                VALUES (@key, @value);
                """;
            saveMarkerCommand.Parameters.AddWithValue("@key", MigrationMarker);
            saveMarkerCommand.Parameters.AddWithValue(
                "@value",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            saveMarkerCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static long GetTableCount(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string tableName)
    {
        // tableName is only called with the two fixed table names above.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private (List<Incident> Incidents, List<CheckHistoryEntry> History)? ReadExistingSqliteSnapshot()
    {
        if (!File.Exists(_sqlitePath))
        {
            return null;
        }

        var sqliteConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _sqlitePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(sqliteConnectionString);
        connection.Open();

        if (!SqliteTableExists(connection, "Incidents") ||
            !SqliteTableExists(connection, "AlertHistory"))
        {
            return null;
        }

        var incidents = new List<Incident>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT IncidentNumber, EndpointUrl, Title, Details, ResolutionNote,
                       Severity, Status, FirstOccurredAt, LastOccurredAt, ResolvedAt,
                       OccurrenceCount
                FROM Incidents;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                incidents.Add(new Incident
                {
                    IncidentNumber = reader.GetString(0),
                    EndpointUrl = reader.GetString(1),
                    Title = reader.GetString(2),
                    Details = reader.GetString(3),
                    ResolutionNote = reader.GetString(4),
                    Severity = (IncidentSeverity)reader.GetInt32(5),
                    Status = (IncidentStatus)reader.GetInt32(6),
                    FirstOccurredAt = ParseDateTime(reader.GetString(7)),
                    LastOccurredAt = ParseDateTime(reader.GetString(8)),
                    ResolvedAt = reader.IsDBNull(9) ? null : ParseDateTime(reader.GetString(9)),
                    OccurrenceCount = reader.GetInt32(10)
                });
            }
        }

        var history = new List<CheckHistoryEntry>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Timestamp, Endpoint, Result, Details, ResponseTimeMs
                FROM AlertHistory
                ORDER BY Timestamp DESC, Id DESC
                LIMIT 200;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                history.Add(new CheckHistoryEntry
                {
                    Timestamp = ParseDateTime(reader.GetString(0)),
                    Endpoint = reader.GetString(1),
                    Result = reader.GetString(2),
                    Details = reader.GetString(3),
                    ResponseTimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4)
                });
            }
        }

        return (incidents, history);
    }

    private static bool SqliteTableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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

    private static void InsertIncidents(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IEnumerable<Incident> incidents,
        bool ignoreDuplicates)
    {
        var insertKeyword = ignoreDuplicates ? "INSERT IGNORE" : "INSERT";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {insertKeyword} INTO Incidents
            (
                IncidentNumber, EndpointUrl, Title, Details, ResolutionNote,
                Severity, Status, FirstOccurredAt, LastOccurredAt, ResolvedAt,
                OccurrenceCount
            )
            VALUES
            (
                @number, @endpoint, @title, @details, @resolutionNote,
                @severity, @status, @firstOccurredAt, @lastOccurredAt,
                @resolvedAt, @occurrenceCount
            );
            """;

        command.Parameters.AddWithValue("@number", string.Empty);
        command.Parameters.AddWithValue("@endpoint", string.Empty);
        command.Parameters.AddWithValue("@title", string.Empty);
        command.Parameters.AddWithValue("@details", string.Empty);
        command.Parameters.AddWithValue("@resolutionNote", string.Empty);
        command.Parameters.AddWithValue("@severity", 0);
        command.Parameters.AddWithValue("@status", 0);
        command.Parameters.AddWithValue("@firstOccurredAt", string.Empty);
        command.Parameters.AddWithValue("@lastOccurredAt", string.Empty);
        command.Parameters.AddWithValue("@resolvedAt", DBNull.Value);
        command.Parameters.AddWithValue("@occurrenceCount", 1);

        foreach (var incident in incidents)
        {
            command.Parameters["@number"].Value = incident.IncidentNumber;
            command.Parameters["@endpoint"].Value = incident.EndpointUrl;
            command.Parameters["@title"].Value = incident.Title;
            command.Parameters["@details"].Value = incident.Details;
            command.Parameters["@resolutionNote"].Value = incident.ResolutionNote;
            command.Parameters["@severity"].Value = (int)incident.Severity;
            command.Parameters["@status"].Value = (int)incident.Status;
            command.Parameters["@firstOccurredAt"].Value = FormatDateTime(incident.FirstOccurredAt);
            command.Parameters["@lastOccurredAt"].Value = FormatDateTime(incident.LastOccurredAt);
            command.Parameters["@resolvedAt"].Value = incident.ResolvedAt is DateTime resolvedAt
                ? FormatDateTime(resolvedAt)
                : DBNull.Value;
            command.Parameters["@occurrenceCount"].Value = incident.OccurrenceCount;
            command.ExecuteNonQuery();
        }
    }

    private static void InsertCheckHistory(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IEnumerable<CheckHistoryEntry> history)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AlertHistory
                (CheckedAt, Endpoint, Result, Details, ResponseTimeMs)
            VALUES
                (@checkedAt, @endpoint, @result, @details, @responseTimeMs);
            """;

        command.Parameters.AddWithValue("@checkedAt", string.Empty);
        command.Parameters.AddWithValue("@endpoint", string.Empty);
        command.Parameters.AddWithValue("@result", string.Empty);
        command.Parameters.AddWithValue("@details", string.Empty);
        command.Parameters.AddWithValue("@responseTimeMs", DBNull.Value);

        foreach (var entry in history)
        {
            command.Parameters["@checkedAt"].Value = FormatDateTime(entry.Timestamp);
            command.Parameters["@endpoint"].Value = entry.Endpoint;
            command.Parameters["@result"].Value = entry.Result;
            command.Parameters["@details"].Value = entry.Details;
            command.Parameters["@responseTimeMs"].Value = entry.ResponseTimeMs.HasValue
                ? entry.ResponseTimeMs.Value
                : DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static string FormatDateTime(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDateTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class DatabaseSettings
    {
        public string Server { get; set; } = "127.0.0.1";
        public uint Port { get; set; } = 3306;
        public string Database { get; set; } = "alertingsystem";
        public string UserId { get; set; } = "alertingsystem_app";
        public string Password { get; set; } = string.Empty;
    }
}
