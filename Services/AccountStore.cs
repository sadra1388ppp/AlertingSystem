using System.IO;
using System.Security.Cryptography;
using AlertingSystem.Models;
using MySqlConnector;

namespace AlertingSystem.Services;

public sealed class AccountStore
{
    private const int PasswordIterations = 310000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly string _connectionString;

    public AccountStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlertingSystem");
        Directory.CreateDirectory(directory);
        _connectionString = DatabaseSettings.LoadConnectionString(directory);

        using var connection = new MySqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppUsers (
                Id INT NOT NULL AUTO_INCREMENT,
                DisplayName VARCHAR(100) NOT NULL,
                Username VARCHAR(32) NOT NULL,
                PasswordHash VARCHAR(255) NOT NULL,
                CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                PRIMARY KEY (Id),
                UNIQUE KEY UX_AppUsers_Username (Username)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;
        command.ExecuteNonQuery();
    }

    public bool HasAccounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM AppUsers LIMIT 1)";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool TryCreateAccount(
        string displayName,
        string username,
        string password,
        out AccountUser? createdUser,
        out string error)
    {
        createdUser = null;
        error = string.Empty;
        displayName = displayName.Trim();
        username = NormalizeUsername(username);

        if (displayName.Length is < 2 or > 100)
        {
            error = "Display name must contain between 2 and 100 characters.";
            return false;
        }

        if (username.Length is < 3 or > 32 ||
            username.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '.' && character != '_' && character != '-'))
        {
            error = "Username must be 3–32 characters and use only letters, numbers, dots, underscores, or hyphens.";
            return false;
        }

        if (password.Length is < 8 or > 1024)
        {
            error = "Password must contain at least 8 characters.";
            return false;
        }

        var passwordHash = CreatePasswordHash(password);

        try
        {
            using var connection = OpenConnection();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO AppUsers (DisplayName, Username, PasswordHash)
                    VALUES (@displayName, @username, @passwordHash)
                    """;
                command.Parameters.AddWithValue("@displayName", displayName);
                command.Parameters.AddWithValue("@username", username);
                command.Parameters.AddWithValue("@passwordHash", passwordHash);
                command.ExecuteNonQuery();
            }

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT LAST_INSERT_ID()";
            var id = Convert.ToInt32(idCommand.ExecuteScalar());

            createdUser = new AccountUser
            {
                Id = id,
                DisplayName = displayName,
                Username = username,
                CreatedAt = DateTime.UtcNow
            };
            return true;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            error = "That username is already registered. Choose another username.";
            return false;
        }
    }

    public bool TryAuthenticate(string username, string password, out AccountUser? user)
    {
        user = null;
        username = NormalizeUsername(username);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DisplayName, Username, PasswordHash, CreatedAt
            FROM AppUsers
            WHERE Username = @username
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@username", username);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var storedHash = reader.GetString(3);
        if (!VerifyPassword(password, storedHash))
        {
            return false;
        }

        user = new AccountUser
        {
            Id = reader.GetInt32(0),
            DisplayName = reader.GetString(1),
            Username = reader.GetString(2),
            CreatedAt = reader.GetDateTime(4)
        };
        return true;
    }

    private MySqlConnection OpenConnection()
    {
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string NormalizeUsername(string username) =>
        (username ?? string.Empty).Trim().ToLowerInvariant();

    private static string CreatePasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return $"pbkdf2-sha256:{PasswordIterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedValue)
    {
        var parts = storedValue.Split(':');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations is < 100000 or > 2000000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expectedHash = Convert.FromBase64String(parts[3]);
            if (salt.Length is < 8 or > 64 || expectedHash.Length is < 16 or > 64)
            {
                return false;
            }

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
