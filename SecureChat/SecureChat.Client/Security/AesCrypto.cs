using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Client.Security;

/// <summary>
/// AES-256-GCM (mã hóa có xác thực). Blob = nonce(12) | tag(16) | ciphertext.
/// Mỗi lần mã hóa dùng nonce ngẫu nhiên nên cùng 1 chuỗi cho ra ciphertext khác nhau.
/// </summary>
public static class AesCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] Encrypt(byte[] plain, byte[] key)
    {
        CheckKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return blob;
    }

    public static byte[] Decrypt(byte[] blob, byte[] key)
    {
        CheckKey(key);
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Dữ liệu mã hóa không hợp lệ (quá ngắn).");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain); // ném CryptographicException nếu sai khóa / bị sửa
        return plain;
    }

    public static string EncryptString(string text, byte[] key) =>
        Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(text), key));

    public static string DecryptString(string base64, byte[] key) =>
        Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64), key));

    private static void CheckKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Khóa AES phải dài {KeySize} byte.", nameof(key));
    }
}
