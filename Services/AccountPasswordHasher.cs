using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AlertingSystem.Services;

internal static class AccountPasswordHasher
{
    // Argon2id parameters: 64 MiB memory, 3 iterations, and 2 lanes.
    private const int ArgonMemoryKiB = 65536;
    private const int ArgonIterations = 3;
    private const int ArgonParallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Existing accounts use PBKDF2. They are upgraded after a successful sign-in.
    private const int LegacyPasswordIterationsMinimum = 100000;
    private const int LegacyPasswordIterationsMaximum = 2000000;
    internal static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = DeriveHash(password, salt);

        // The format stores the algorithm and parameters with the salt and hash,
        // allowing future parameter upgrades without changing the database schema.
        return $"argon2id:{ArgonMemoryKiB}:{ArgonIterations}:{ArgonParallelism}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static byte[] DeriveHash(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = ArgonParallelism,
                MemorySize = ArgonMemoryKiB,
                Iterations = ArgonIterations
            };

            return argon2.GetBytes(HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static bool Verify(string password, string storedValue) =>
        Verify(password, storedValue, out _);

    internal static bool Verify(
        string password,
        string storedValue,
        out bool needsUpgrade)
    {
        needsUpgrade = false;
        var parts = storedValue.Split(':');

        if (parts.Length == 6 &&
            string.Equals(parts[0], "argon2id", StringComparison.Ordinal))
        {
            // Accept only the application's configured parameters. This prevents
            // untrusted database values from requesting excessive CPU or memory.
            if (parts[1] != ArgonMemoryKiB.ToString() ||
                parts[2] != ArgonIterations.ToString() ||
                parts[3] != ArgonParallelism.ToString())
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[4]);
                var expectedHash = Convert.FromBase64String(parts[5]);
                if (salt.Length != SaltSize || expectedHash.Length != HashSize)
                {
                    return false;
                }

                var actualHash = DeriveHash(password, salt);
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Backward compatibility for accounts created before the Argon2id upgrade.
        if (parts.Length != 4 ||
            !string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations is < LegacyPasswordIterationsMinimum or > LegacyPasswordIterationsMaximum)
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

            var verified = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            needsUpgrade = verified;
            return verified;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
