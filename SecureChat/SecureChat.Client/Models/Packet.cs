using System.Text.Json;

namespace SecureChat.Client.Models;

/// <summary>Các loại gói tin - PHẢI thống nhất với Server (Hải).</summary>
public static class PacketType
{
    public const string Register = "REGISTER";
    public const string Login = "LOGIN";
    public const string Chat = "CHAT";
    public const string GetHistory = "GET_HISTORY";
    public const string StatusUpdate = "STATUS_UPDATE";
    public const string Logout = "LOGOUT";
    public const string GetPublicKey = "GET_PUBLIC_KEY";
    public const string Error = "ERROR";

    /// <summary>Server trả phản hồi cho yêu cầu X bằng gói Type = "X_RESPONSE".</summary>
    public static string ResponseOf(string requestType) => requestType + "_RESPONSE";
}

/// <summary>
/// Gói tin truyền qua TCP. Mỗi gói = 1 dòng JSON kết thúc bằng '\n'
/// (JSON đã escape xuống dòng nên không bao giờ chứa '\n' thật).
/// </summary>
public class Packet
{
    public string Type { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Receiver { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static Packet? FromJson(string json) => JsonSerializer.Deserialize<Packet>(json, Options);
}
