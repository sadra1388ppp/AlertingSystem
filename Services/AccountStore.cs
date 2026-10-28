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

        RenameLegacyAccountsTableIfNeeded(connection);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Accounts (
                    Id INT NOT NULL AUTO_INCREMENT,
                    DisplayName VARCHAR(100) NOT NULL,
                    Username VARCHAR(32) NOT NULL,
                    PasswordHash VARCHAR(255) NOT NULL,
                    CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    IsDeleted TINYINT(1) NOT NULL DEFAULT 0,
                    DeletedAt DATETIME(6) NULL,
                    PRIMARY KEY (Id),
                    UNIQUE KEY UX_Accounts_Username (Username)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                """;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "ALTER TABLE Accounts ADD COLUMN IF NOT EXISTS IsDeleted TINYINT(1) NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "ALTER TABLE Accounts ADD COLUMN IF NOT EXISTS DeletedAt DATETIME(6) NULL";
            command.ExecuteNonQuery();
        }
    }

    public bool HasAccounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Accounts WHERE IsDeleted = 0 LIMIT 1)";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public List<AccountUser> GetAllAccounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DisplayName, Username, CreatedAt, IsDeleted, DeletedAt
            FROM Accounts
            ORDER BY IsDeleted ASC, CreatedAt DESC, Id DESC
            """;

        using var reader = command.ExecuteReader();
        var accounts = new List<AccountUser>();
        while (reader.Read())
        {
            accounts.Add(new AccountUser
            {
                Id = reader.GetInt32(0),
                DisplayName = reader.GetString(1),
                Username = reader.GetString(2),
                CreatedAt = reader.GetDateTime(3),
                IsDeleted = reader.GetBoolean(4),
                DeletedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            });
        }

        return accounts;
    }

    public bool IsActiveAccount(int userId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM Accounts WHERE Id = @id AND IsDeleted = 0 LIMIT 1)";
        command.Parameters.AddWithValue("@id", userId);
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
                    INSERT INTO Accounts (DisplayName, Username, PasswordHash)
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
            FROM Accounts
            WHERE Username = @username AND IsDeleted = 0
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

    public bool TrySoftDeleteAccount(int userId, string password, out string error)
    {
        error = string.Empty;
        string passwordHash;
        bool isDeleted;

        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT PasswordHash, IsDeleted
                FROM Accounts
                WHERE Id = @id
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@id", userId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                error = "This account could not be found in MariaDB.";
                return false;
            }

            passwordHash = reader.GetString(0);
            isDeleted = reader.GetBoolean(1);
        }

        if (isDeleted)
        {
            error = "This account has already been deleted.";
            return false;
        }

        if (!VerifyPassword(password, passwordHash))
        {
            error = "The password is incorrect. The account was not changed.";
            return false;
        }

        using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE Accounts
            SET IsDeleted = 1, DeletedAt = UTC_TIMESTAMP(6)
            WHERE Id = @id AND IsDeleted = 0
            """;
        updateCommand.Parameters.AddWithValue("@id", userId);

        if (updateCommand.ExecuteNonQuery() != 1)
        {
            error = "The account could not be marked as deleted. Refresh the account list and try again.";
            return false;
        }

        return true;
    }

    private static void RenameLegacyAccountsTableIfNeeded(MySqlConnection connection)
    {
        bool legacyTableExists;
        bool accountsTableExists;

        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = DATABASE() AND table_name = 'AppUsers'
                    ),
                    EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = DATABASE() AND table_name = 'Accounts'
                    )
                """;

            using var reader = checkCommand.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            legacyTableExists = reader.GetBoolean(0);
            accountsTableExists = reader.GetBoolean(1);
        }

        if (legacyTableExists && !accountsTableExists)
        {
            using var renameCommand = connection.CreateCommand();
            renameCommand.CommandText = "RENAME TABLE AppUsers TO Accounts";
            renameCommand.ExecuteNonQuery();
        }
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
