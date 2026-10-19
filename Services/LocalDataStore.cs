using System.Globalization;
using System.IO;
using System.Text.Json;
using AlertingSystem.Models;
using Microsoft.Data.Sqlite;

namespace AlertingSystem.Services;

public sealed class LocalDataStore
{
    private const string LegacyImportKey = "LegacyJsonImportV1";
    private const int MaximumHistoryEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlertingSystem");

    private readonly string _databasePath;
    private readonly string _connectionString;

    public LocalDataStore()
    {
        Directory.CreateDirectory(_directory);

        _databasePath = Path.Combine(_directory, "alertingsystem.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

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
            SELECT Timestamp, Endpoint, Result, Details, ResponseTimeMs
            FROM AlertHistory
            ORDER BY Timestamp DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", MaximumHistoryEntries);

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

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Incidents
                (
                    IncidentNumber TEXT NOT NULL PRIMARY KEY,
                    EndpointUrl TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Details TEXT NOT NULL,
                    ResolutionNote TEXT NOT NULL,
                    Severity INTEGER NOT NULL,
                    Status INTEGER NOT NULL,
                    FirstOccurredAt TEXT NOT NULL,
                    LastOccurredAt TEXT NOT NULL,
                    ResolvedAt TEXT NULL,
                    OccurrenceCount INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS AlertHistory
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    Endpoint TEXT NOT NULL,
                    Result TEXT NOT NULL,
                    Details TEXT NOT NULL,
                    ResponseTimeMs INTEGER NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Incidents_Status_LastOccurredAt
                    ON Incidents(Status, LastOccurredAt);

                CREATE INDEX IF NOT EXISTS IX_AlertHistory_Timestamp
                    ON AlertHistory(Timestamp);

                CREATE TABLE IF NOT EXISTS AppMetadata
                (
                    MetadataKey TEXT NOT NULL PRIMARY KEY,
                    MetadataValue TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        ImportLegacyJsonOnce(connection);
    }

    private void ImportLegacyJsonOnce(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.Transaction = transaction;
            checkCommand.CommandText =
                "SELECT MetadataValue FROM AppMetadata WHERE MetadataKey = $key LIMIT 1;";
            checkCommand.Parameters.AddWithValue("$key", LegacyImportKey);

            if (checkCommand.ExecuteScalar() is not null)
            {
                transaction.Commit();
                return;
            }
        }

        // The original JSON files are left untouched as a backup after import.
        var legacyIncidents = LoadLegacyJson<Incident>("incidents.json");
        var legacyHistory = LoadLegacyJson<CheckHistoryEntry>("check-history.json");

        InsertIncidents(connection, transaction, legacyIncidents, ignoreDuplicates: true);
        InsertCheckHistory(connection, transaction, legacyHistory
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaximumHistoryEntries)
            .ToList());

        using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.Transaction = transaction;
            markerCommand.CommandText = """
                INSERT INTO AppMetadata (MetadataKey, MetadataValue)
                VALUES ($key, $value);
                """;
            markerCommand.Parameters.AddWithValue("$key", LegacyImportKey);
            markerCommand.Parameters.AddWithValue("$value", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            markerCommand.ExecuteNonQuery();
        }

        transaction.Commit();
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
            // Fail startup instead of silently marking a broken legacy file as imported.
            throw new InvalidDataException(
                $"Could not import the existing data file '{path}'. The file has been left unchanged.",
                exception);
        }
    }

    private static void InsertIncidents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<Incident> incidents,
        bool ignoreDuplicates)
    {
        var insertMode = ignoreDuplicates ? "INSERT OR IGNORE" : "INSERT";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {insertMode} INTO Incidents
            (
                IncidentNumber, EndpointUrl, Title, Details, ResolutionNote,
                Severity, Status, FirstOccurredAt, LastOccurredAt, ResolvedAt,
                OccurrenceCount
            )
            VALUES
            (
                $number, $endpoint, $title, $details, $resolutionNote,
                $severity, $status, $firstOccurredAt, $lastOccurredAt,
                $resolvedAt, $occurrenceCount
            );
            """;

        var numberParameter = command.Parameters.Add("$number", SqliteType.Text);
        var endpointParameter = command.Parameters.Add("$endpoint", SqliteType.Text);
        var titleParameter = command.Parameters.Add("$title", SqliteType.Text);
        var detailsParameter = command.Parameters.Add("$details", SqliteType.Text);
        var noteParameter = command.Parameters.Add("$resolutionNote", SqliteType.Text);
        var severityParameter = command.Parameters.Add("$severity", SqliteType.Integer);
        var statusParameter = command.Parameters.Add("$status", SqliteType.Integer);
        var firstParameter = command.Parameters.Add("$firstOccurredAt", SqliteType.Text);
        var lastParameter = command.Parameters.Add("$lastOccurredAt", SqliteType.Text);
        var resolvedParameter = command.Parameters.Add("$resolvedAt", SqliteType.Text);
        var countParameter = command.Parameters.Add("$occurrenceCount", SqliteType.Integer);

        foreach (var incident in incidents)
        {
            numberParameter.Value = incident.IncidentNumber;
            endpointParameter.Value = incident.EndpointUrl;
            titleParameter.Value = incident.Title;
            detailsParameter.Value = incident.Details;
            noteParameter.Value = incident.ResolutionNote;
            severityParameter.Value = (int)incident.Severity;
            statusParameter.Value = (int)incident.Status;
            firstParameter.Value = FormatDateTime(incident.FirstOccurredAt);
            lastParameter.Value = FormatDateTime(incident.LastOccurredAt);
            resolvedParameter.Value = incident.ResolvedAt is DateTime resolvedAt
                ? FormatDateTime(resolvedAt)
                : DBNull.Value;
            countParameter.Value = incident.OccurrenceCount;
            command.ExecuteNonQuery();
        }
    }

    private static void InsertCheckHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<CheckHistoryEntry> history)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AlertHistory
                (Timestamp, Endpoint, Result, Details, ResponseTimeMs)
            VALUES
                ($timestamp, $endpoint, $result, $details, $responseTimeMs);
            """;

        var timestampParameter = command.Parameters.Add("$timestamp", SqliteType.Text);
        var endpointParameter = command.Parameters.Add("$endpoint", SqliteType.Text);
        var resultParameter = command.Parameters.Add("$result", SqliteType.Text);
        var detailsParameter = command.Parameters.Add("$details", SqliteType.Text);
        var responseTimeParameter = command.Parameters.Add("$responseTimeMs", SqliteType.Integer);

        foreach (var entry in history)
        {
            timestampParameter.Value = FormatDateTime(entry.Timestamp);
            endpointParameter.Value = entry.Endpoint;
            resultParameter.Value = entry.Result;
            detailsParameter.Value = entry.Details;
            responseTimeParameter.Value = entry.ResponseTimeMs.HasValue
                ? entry.ResponseTimeMs.Value
                : DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static string FormatDateTime(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDateTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
