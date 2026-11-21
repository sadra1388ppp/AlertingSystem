using System.Data;
using System.Data.Common;
using System.IO;
using Microsoft.EntityFrameworkCore;

namespace AlertingSystem.Backend.Services;

public sealed class AlertingDbContextFactory
{
    private readonly string _connectionString;
    private readonly bool _useSimplifiedIncidentTable;
    private readonly bool _useSimplifiedHistoryTable;
    private readonly bool _useSimplifiedMetadataTable;

    public AlertingDbContextFactory()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlertingSystem");
        Directory.CreateDirectory(DataDirectory);

        _connectionString = DatabaseSettings.LoadConnectionString(DataDirectory);

        using var probe = new AlertingDbContext(_connectionString);
        var tables = ReadTableNames(probe.Database.GetDbConnection(), _connectionString);

        if (tables.Contains("checks") && !tables.Contains("AlertHistory"))
        {
            using var migrationContext = new AlertingDbContext(
                _connectionString,
                ChooseSimplifiedTable(tables, "incident_records", "Incidents"),
                useSimplifiedHistoryTable: true,
                useSimplifiedMetadataTable: ChooseSimplifiedTable(tables, "settings", "AppMetadata"));

            migrationContext.Database.Migrate();
            tables = ReadTableNames(
                migrationContext.Database.GetDbConnection(),
                _connectionString);
        }

        _useSimplifiedIncidentTable =
            ChooseSimplifiedTable(tables, "incident_records", "Incidents");
        _useSimplifiedHistoryTable =
            ChooseSimplifiedTable(tables, "checks", "AlertHistory");
        _useSimplifiedMetadataTable =
            ChooseSimplifiedTable(tables, "settings", "AppMetadata");

        using var context = CreateContext();
        var databaseCreated = context.Database.EnsureCreated();
        var finalTables = ReadTableNames(context.Database.GetDbConnection(), _connectionString);

        HasMetadataTable = databaseCreated ||
            finalTables.Contains("AppMetadata") ||
            finalTables.Contains("settings");
    }

    public string DataDirectory { get; }
    public bool HasMetadataTable { get; }

    public AlertingDbContext CreateContext() => new(
        _connectionString,
        _useSimplifiedIncidentTable,
        _useSimplifiedHistoryTable,
        _useSimplifiedMetadataTable);

    private static bool ChooseSimplifiedTable(
        HashSet<string> tables,
        string simplifiedName,
        string originalName) =>
        tables.Contains(simplifiedName) && !tables.Contains(originalName);

    private static HashSet<string> ReadTableNames(
        DbConnection connection,
        string connectionString)
    {
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };
            var databaseName = builder.TryGetValue("Database", out var value)
                ? Convert.ToString(value)
                : null;

            var schema = connection.GetSchema("Tables");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in schema.Rows)
            {
                if (schema.Columns.Contains("TABLE_SCHEMA") &&
                    !string.IsNullOrWhiteSpace(databaseName) &&
                    !string.Equals(
                        Convert.ToString(row["TABLE_SCHEMA"]),
                        databaseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tableName = Convert.ToString(row["TABLE_NAME"]);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    names.Add(tableName);
                }
            }

            return names;
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }
}