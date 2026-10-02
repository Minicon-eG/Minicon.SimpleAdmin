using System.Security.Cryptography;
using System.Text;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// AES-256-CBC encryption for SQL connection strings stored in config.json.
/// Salt and IV are generated per-value and stored inline with the ciphertext.
/// </summary>
public static class ConnectionStringEncryption
{
    private const string Prefix = "enc:v1:";
    private const int Iterations = 100_000;

    /// <summary>Returns true if the value was encrypted by this utility.</summary>
    public static bool IsEncrypted(string? value) =>
        value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a plain-text connection string.
    /// Format: enc:v1:{base64(salt)}.{base64(iv)}.{base64(ciphertext)}
    /// </summary>
    public static string Encrypt(string plaintext, string masterKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv   = RandomNumberGenerator.GetBytes(16);
        var key  = DeriveKey(masterKey, salt);

        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor   = aes.CreateEncryptor();
        var       plainBytes  = Encoding.UTF8.GetBytes(plaintext);
        var       cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return Prefix
               + Convert.ToBase64String(salt)        + "."
               + Convert.ToBase64String(iv)          + "."
               + Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// Decrypts an encrypted connection string. Returns plain-text values unchanged.
    /// </summary>
    public static string Decrypt(string encoded, string masterKey)
    {
        if (!IsEncrypted(encoded))
            return encoded;

        var parts = encoded[Prefix.Length..].Split('.');
        if (parts.Length != 3)
            throw new FormatException($"Invalid encrypted connection string format (expected 3 segments, got {parts.Length}).");

        var salt        = Convert.FromBase64String(parts[0]);
        var iv          = Convert.FromBase64String(parts[1]);
        var cipherBytes = Convert.FromBase64String(parts[2]);
        var key         = DeriveKey(masterKey, salt);

        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor  = aes.CreateDecryptor();
        var       plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string masterKey, byte[] salt)
    {
        var keyBytes = Convert.FromBase64String(masterKey);
        return Rfc2898DeriveBytes.Pbkdf2(keyBytes, salt, Iterations, HashAlgorithmName.SHA256, 32);
    }
}
