using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Client.Security;

/// <summary>
/// Mã hóa lai (hybrid): tin nhắn mã hóa bằng 1 khóa AES ngẫu nhiên, khóa AES đó được
/// bọc bằng RSA public key của từng người nhận.
/// Envelope (chuỗi đưa vào Packet.Payload): "wrappedKey1,wrappedKey2.aesBlob" (toàn Base64).
/// Truyền nhiều public key (vd: người nhận + chính mình) để cả hai đều giải mã lại được lịch sử.
/// </summary>
public static class MessageCipher
{
    public static string Encrypt(string plaintext, params string[] recipientPublicKeys)
    {
        if (recipientPublicKeys.Length == 0)
            throw new ArgumentException("Cần ít nhất một public key người nhận.");

        var aesKey = AesCrypto.GenerateKey();
        var wrapped = recipientPublicKeys
            .Select(pk => Convert.ToBase64String(RsaCrypto.Encrypt(aesKey, pk)));
        var blob = AesCrypto.EncryptString(plaintext, aesKey);

        return string.Join(",", wrapped) + "." + blob;
    }

    public static string Decrypt(string envelope, string privateKeyBase64)
    {
        var parts = envelope.Split('.');
        if (parts.Length != 2)
            throw new FormatException("Envelope không đúng định dạng.");

        var blob = Convert.FromBase64String(parts[1]);
        foreach (var wrapped in parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var aesKey = RsaCrypto.Decrypt(Convert.FromBase64String(wrapped), privateKeyBase64);
                return Encoding.UTF8.GetString(AesCrypto.Decrypt(blob, aesKey));
            }
            catch (CryptographicException)
            {
                // khóa bọc này không dành cho mình, thử cái tiếp theo
            }
        }
        throw new CryptographicException("Không giải mã được: không có khóa phù hợp.");
    }
}
