using System;
using System.Text.Json;

namespace SecureChat.Server
{
    /// <summary>
    /// Gói tin truyền qua TCP. Mỗi gói = 1 dòng JSON kết thúc bằng '\n'.
    /// Tên thuộc tính khớp với Packet của Client (Nhân).
    /// </summary>
    [Serializable]
    public class Packet
    {
        public string Type { get; set; } = "";
        public string Sender { get; set; } = "";
        public string Receiver { get; set; } = "";
        public string Payload { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public static Packet? FromJson(string json) => JsonSerializer.Deserialize<Packet>(json, Options);

        public static T? ParsePayload<T>(string payload) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(payload, Options); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Payload của gói REGISTER: Client gửi bản băm mật khẩu và public key RSA.</summary>
    public class RegisterRequest
    {
        public string PasswordHash { get; set; } = "";
        public string PublicKey { get; set; } = "";
    }
}
