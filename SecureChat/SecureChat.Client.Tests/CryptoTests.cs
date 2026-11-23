using System.Security.Cryptography;
using SecureChat.Client.Models;
using SecureChat.Client.Security;
using Xunit;

namespace SecureChat.Client.Tests;

public class CryptoTests
{
    // RSA keygen khá chậm nên tạo 1 lần dùng chung
    private static readonly RsaKeyPair Alice = RsaCrypto.GenerateKeyPair();
    private static readonly RsaKeyPair Bob = RsaCrypto.GenerateKeyPair();

    // ---------- AES ----------

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("Xin chào, đây là tin nhắn tiếng Việt có dấu 🔒")]
    public void Aes_RoundTrip_ReturnsOriginal(string text)
    {
        var key = AesCrypto.GenerateKey();
        var encrypted = AesCrypto.EncryptString(text, key);
        Assert.Equal(text, AesCrypto.DecryptString(encrypted, key));
    }

    [Fact]
    public void Aes_Ciphertext_DiffersFromPlaintext_AndEachCall()
    {
        var key = AesCrypto.GenerateKey();
        var a = AesCrypto.EncryptString("same message", key);
        var b = AesCrypto.EncryptString("same message", key);

        Assert.NotEqual("same message", a);
        Assert.NotEqual(a, b); // nonce ngẫu nhiên
    }

    [Fact]
    public void Aes_WrongKey_Throws()
    {
        var encrypted = AesCrypto.EncryptString("secret", AesCrypto.GenerateKey());
        Assert.ThrowsAny<CryptographicException>(
            () => AesCrypto.DecryptString(encrypted, AesCrypto.GenerateKey()));
    }

    [Fact]
    public void Aes_TamperedData_Throws()
    {
        var key = AesCrypto.GenerateKey();
        var blob = AesCrypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("secret"), key);
        blob[^1] ^= 0xFF; // sửa 1 byte

        Assert.ThrowsAny<CryptographicException>(() => AesCrypto.Decrypt(blob, key));
    }

    // ---------- RSA ----------

    [Fact]
    public void Rsa_RoundTrip_ReturnsOriginal()
    {
        var data = AesCrypto.GenerateKey();
        var enc = RsaCrypto.Encrypt(data, Alice.PublicKey);
        Assert.Equal(data, RsaCrypto.Decrypt(enc, Alice.PrivateKey));
    }

    [Fact]
    public void Rsa_WrongPrivateKey_Throws()
    {
        var enc = RsaCrypto.Encrypt(new byte[] { 1, 2, 3 }, Alice.PublicKey);
        Assert.ThrowsAny<CryptographicException>(() => RsaCrypto.Decrypt(enc, Bob.PrivateKey));
    }

    // ---------- Hybrid (dùng cho tin nhắn thật) ----------

    [Fact]
    public void Message_RoundTrip_LongText()
    {
        var text = string.Concat(Enumerable.Repeat("Tin nhắn dài hơn nhiều so với giới hạn của RSA. ", 100));
        var envelope = MessageCipher.Encrypt(text, Bob.PublicKey);

        Assert.DoesNotContain("Tin nhắn", envelope);
        Assert.Equal(text, MessageCipher.Decrypt(envelope, Bob.PrivateKey));
    }

    [Fact]
    public void Message_OnlyIntendedRecipientCanDecrypt()
    {
        var envelope = MessageCipher.Encrypt("for bob", Bob.PublicKey);
        Assert.ThrowsAny<CryptographicException>(() => MessageCipher.Decrypt(envelope, Alice.PrivateKey));
    }

    [Fact]
    public void Message_MultipleRecipients_BothCanDecrypt()
    {
        var envelope = MessageCipher.Encrypt("hi both", Alice.PublicKey, Bob.PublicKey);
        Assert.Equal("hi both", MessageCipher.Decrypt(envelope, Alice.PrivateKey));
        Assert.Equal("hi both", MessageCipher.Decrypt(envelope, Bob.PrivateKey));
    }

    // ---------- Packet ----------

    [Fact]
    public void Packet_Json_RoundTrip_KeepsVietnameseAndNewlines()
    {
        var p = new Packet
        {
            Type = PacketType.Chat, Sender = "nhan", Receiver = "hai",
            Payload = "dòng 1\ndòng 2 có dấu"
        };
        var json = p.ToJson();
        var back = Packet.FromJson(json)!;

        Assert.DoesNotContain('\n', json); // 1 gói = 1 dòng
        Assert.Equal(p.Payload, back.Payload);
        Assert.Equal(PacketType.Chat, back.Type);
    }
}
