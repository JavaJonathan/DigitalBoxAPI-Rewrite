using System.Security.Cryptography;

namespace DigitalBoxApi.Services;

// PBKDF2 (SHA-256) password hashing for user login credentials.
// Hash string format: {iterations}.{saltBase64}.{subkeyBase64}.
public static class PasswordHasher
{
    private const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int SubkeySize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, SubkeySize);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(subkey)}";
    }

    public static bool Verify(string password, string? encodedHash)
    {
        if (string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedSubkey;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedSubkey = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualSubkey = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expectedSubkey.Length);

        return CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
    }
}
