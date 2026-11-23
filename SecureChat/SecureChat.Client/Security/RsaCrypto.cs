using System.Security.Cryptography;

namespace SecureChat.Client.Security;

/// <summary>Cặp khóa RSA, cả hai dạng Base64 (public = SubjectPublicKeyInfo, private = PKCS#8).</summary>
public sealed record RsaKeyPair(string PublicKey, string PrivateKey);

/// <summary>RSA-2048 + OAEP-SHA256. Chỉ dùng để bọc khóa AES (RSA không mã hóa được tin dài).</summary>
public static class RsaCrypto
{
    public static RsaKeyPair GenerateKeyPair(int bits = 2048)
    {
        using var rsa = RSA.Create(bits);
        return new RsaKeyPair(
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
    }

    public static byte[] Encrypt(byte[] data, string publicKeyBase64)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    public static byte[] Decrypt(byte[] data, string privateKeyBase64)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA256);
    }
}
