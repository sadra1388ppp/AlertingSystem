using System.IO;
using System.Text.Json;
using MySqlConnector;

namespace AlertingSystem.Services;

public sealed class DatabaseSettings
{
    public string Server { get; set; } = "127.0.0.1";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "alertingsystem";
    public string UserId { get; set; } = "alertingsystem_app";
    public string Password { get; set; } = "123";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string LoadConnectionString(string settingsDirectory)
    {
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

        Directory.CreateDirectory(settingsDirectory);
        var settingsPath = Path.Combine(settingsDirectory, "database-settings.json");

        if (!File.Exists(settingsPath))
        {
            // Local development defaults only. Credentials stay under LocalAppData, not in Git.
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new DatabaseSettings(), JsonOptions));
        }

        DatabaseSettings settings;

        try
        {
            settings = JsonSerializer.Deserialize<DatabaseSettings>(
                File.ReadAllText(settingsPath), JsonOptions) ?? new DatabaseSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Could not read the MariaDB settings file at '{settingsPath}'. Fix or recreate that file.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(settings.Server) ||
            string.IsNullOrWhiteSpace(settings.Database) ||
            string.IsNullOrWhiteSpace(settings.UserId) ||
            settings.Port == 0)
        {
            throw new InvalidDataException(
                $"Server, Port, Database, and UserId must be filled in the MariaDB settings file:{Environment.NewLine}{settingsPath}");
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Server,
            Port = settings.Port,
            Database = settings.Database,
            UserID = settings.UserId,
            Password = settings.Password ?? string.Empty,
            CharacterSet = "utf8mb4",
            // This branch is for local development against 127.0.0.1.
            // Avoid TLS negotiation with a local MariaDB installation that closes the
            // connection during the optional SSL handshake. Environment-provided
            // connection strings keep their own SSL settings above.
            SslMode = MySqlSslMode.None,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 30
        };

        return builder.ConnectionString;
    }
}
