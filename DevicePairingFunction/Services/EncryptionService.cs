using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Services;

public class EncryptionService : IEncryptionService
{
    private readonly byte[] _key;
    private readonly ILogger<EncryptionService> _logger;

    public EncryptionService(IConfiguration configuration, ILogger<EncryptionService> logger)
    {
        _logger = logger;
        var keyString = configuration["EncryptionKey"]
            ?? throw new InvalidOperationException("EncryptionKey not configured");

        // For development, use a deterministic key derived from the string
        // In production, this should be a proper 32-byte key from Key Vault
        if (keyString.StartsWith("REPLACE_WITH"))
        {
            _logger.LogWarning("Using development encryption key - not secure for production!");
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("development-key-not-for-production"));
        }
        else
        {
            _key = Convert.FromBase64String(keyString);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Encryption key must be 32 bytes (256 bits)");
        }
    }

    public string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Combine IV + ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText)
    {
        var fullCipher = Convert.FromBase64String(cipherText);

        using var aes = Aes.Create();
        aes.Key = _key;

        // Extract IV from the beginning
        var iv = new byte[aes.BlockSize / 8];
        var cipher = new byte[fullCipher.Length - iv.Length];

        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    public string HashDeviceSecret(string deviceSecret)
    {
        // Use PBKDF2 with a random salt
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(deviceSecret),
            salt,
            iterations: 100000,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        // Combine salt + hash
        var result = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, result, salt.Length, hash.Length);

        return Convert.ToBase64String(result);
    }

    public bool VerifyDeviceSecret(string deviceSecret, string storedHash)
    {
        var fullHash = Convert.FromBase64String(storedHash);

        // Extract salt (first 16 bytes)
        var salt = new byte[16];
        Buffer.BlockCopy(fullHash, 0, salt, 0, 16);

        // Extract stored hash (remaining bytes)
        var storedHashBytes = new byte[fullHash.Length - 16];
        Buffer.BlockCopy(fullHash, 16, storedHashBytes, 0, storedHashBytes.Length);

        // Compute hash with same salt
        var computedHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(deviceSecret),
            salt,
            iterations: 100000,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        return CryptographicOperations.FixedTimeEquals(computedHash, storedHashBytes);
    }
}
