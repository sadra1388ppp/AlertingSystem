using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AlertingSystem.Services;

internal static class AccountPasswordHasher
{
    private const int MemoryKiB = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    internal static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = DeriveHash(password, salt);

        return $"argon2id:{MemoryKiB}:{Iterations}:{Parallelism}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static byte[] DeriveHash(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = Parallelism,
                MemorySize = MemoryKiB,
                Iterations = Iterations
            };

            return argon2.GetBytes(HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static bool Verify(string password, string storedValue)
    {
        var parts = storedValue.Split(':');

        if (parts.Length != 6 ||
            parts[0] != "argon2id" ||
            parts[1] != MemoryKiB.ToString() ||
            parts[2] != Iterations.ToString() ||
            parts[3] != Parallelism.ToString())
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
}