using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HPK.Core.Utils.Services;

/// <summary>
/// Abstraction for symmetric encryption and decryption operations (DIP).
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plainText, string? key = null);
    string Decrypt(string cipherText, string? key = null);
}

/// <summary>
/// Default AES-256 implementation of <see cref="IEncryptionService"/>.
/// </summary>
public class AesEncryptionService : IEncryptionService
{
    private static readonly string DefaultKey = "Icot_Platform_Secure_Key_2026!#$"; // 32 chars for AES-256

    public static AesEncryptionService Instance { get; } = new();

    public string Encrypt(string plainText, string? key = null)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        var encryptionKey = GetValidKey(key);

        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        
        // Prepend IV to ciphertext stream
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs, Encoding.UTF8))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public string Decrypt(string cipherText, string? key = null)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);
            var encryptionKey = GetValidKey(key);

            using var aes = Aes.Create();
            aes.Key = encryptionKey;

            var iv = new byte[aes.BlockSize / 8];
            if (fullCipher.Length < iv.Length)
                return cipherText;

            Array.Copy(fullCipher, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs, Encoding.UTF8);

            return sr.ReadToEnd();
        }
        catch
        {
            // If decryption fails (e.g. not encrypted yet or invalid format), return original text
            return cipherText;
        }
    }

    private static byte[] GetValidKey(string? key)
    {
        var rawKey = key ?? Environment.GetEnvironmentVariable("APP_KEY") ?? Environment.GetEnvironmentVariable("ENCRYPTION_KEY") ?? DefaultKey;
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
    }
}
