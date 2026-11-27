using System;
using System.Security.Cryptography;
using System.Text;

namespace WinNotch;

/// <summary>
/// Per-device password lock for HolyNotch Desktop Settings (PBKDF2-SHA256 with random 16-byte salt, constant-time verify).
/// </summary>
public static class SettingsLock
{
    const string Prefix = "v1:";
    const int Iterations = 100_000;
    const int SaltBytes = 16;
    const int HashBytes = 32;

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password cannot be empty.", nameof(password));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? storedHash, string? password)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(password) || !storedHash.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        string[] parts = storedHash[Prefix.Length..].Split(':');
        if (parts.Length != 2) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expected = Convert.FromBase64String(parts[1]);
            if (salt.Length != SaltBytes || expected.Length != HashBytes) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
