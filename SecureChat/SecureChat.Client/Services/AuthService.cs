using System.Text.RegularExpressions;
using System.Text.Json;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Security;

namespace SecureChat.Client.Services;

/// <summary>
/// Đăng ký / đăng nhập theo docs/PROTOCOL.md của Server:
///  - REGISTER: Sender = username, Payload = {"PasswordHash","PublicKey"}  -> REGISTER_RESPONSE
///  - LOGIN:    Sender = username, Payload = PasswordHash                  -> LOGIN_RESPONSE
///  - Phản hồi: Payload = "SUCCESS" hoặc "FAIL: lý do"
/// </summary>
public class AuthService
{
    private const string ServerName = "SERVER";
    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,20}$");

    private readonly ClientNetworkHandler _net;
    private readonly ClientState _state;

    public AuthService(ClientNetworkHandler net, ClientState state)
    {
        _net = net;
        _state = state;
    }

    public async Task<(bool Ok, string Message)> RegisterAsync(string username, string password)
    {
        if (!UsernameRegex.IsMatch(username))
            return (false, "Tên đăng nhập phải 3-20 ký tự gồm chữ, số hoặc dấu gạch dưới.");

        var keys = RsaCrypto.GenerateKeyPair();
        var payload = JsonSerializer.Serialize(new
        {
            PasswordHash = PasswordHasher.Hash(username, password),
            keys.PublicKey
        });

        var resp = await _net.RequestAsync(new Packet
        {
            Type = PacketType.Register, Sender = username, Receiver = ServerName, Payload = payload
        });

        if (!IsSuccess(resp)) return (false, CleanError(resp.Payload));

        LocalKeyStore.Save(username, keys); // giữ khóa riêng trên máy này
        return (true, "Đăng ký thành công. Hãy đăng nhập.");
    }

    public async Task<(bool Ok, string Message)> LoginAsync(string username, string password)
    {
        var keys = LocalKeyStore.Load(username);
        if (keys == null)
            return (false, "Không tìm thấy khóa riêng của tài khoản này trên máy (hãy đăng ký trên máy này).");

        var resp = await _net.RequestAsync(new Packet
        {
            Type = PacketType.Login, Sender = username, Receiver = ServerName,
            Payload = PasswordHasher.Hash(username, password)
        });

        if (!IsSuccess(resp)) return (false, CleanError(resp.Payload));

        _state.Username = username;
        _state.Keys = keys;
        return (true, "Đăng nhập thành công.");
    }

    private static bool IsSuccess(Packet resp) =>
        resp.Payload.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);

    private static string CleanError(string payload) =>
        payload.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase) ? payload[5..].Trim() : payload;
}
