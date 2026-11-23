using System.Security.Cryptography;
using System.Text.Json;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Security;

namespace SecureChat.Client.Services;

/// <summary>
/// Gửi/nhận tin nhắn mã hóa, lịch sử chat và trạng thái online/offline (theo docs/PROTOCOL.md của Server).
///  - Gửi: GET_PUBLIC_KEY (người nhận) -> MessageCipher.Encrypt(text, keyNgườiNhận, keyCủaMình) -> CHAT.
///    Mã hóa cho cả hai để người gửi cũng đọc lại được lịch sử.
///  - GET_HISTORY: Payload = mảng JSON các gói CHAT (bản mã), hoặc "FAIL: ...".
///  - STATUS_UPDATE: Sender = username, Payload = ONLINE | OFFLINE.
///  - ERROR: Server báo lỗi cho các gói không có phản hồi (vd CHAT).
/// </summary>
public class ChatService
{
    private readonly ClientNetworkHandler _net;
    private readonly ClientState _state;
    private readonly Dictionary<string, string> _publicKeys = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, string, DateTime>? MessageReceived;   // sender, text, time
    public event Action<string, bool>? StatusChanged;                 // user, isOnline
    public event Action<string>? ErrorReceived;                       // nội dung lỗi từ Server

    public ChatService(ClientNetworkHandler net, ClientState state)
    {
        _net = net;
        _state = state;
        _net.PacketReceived += OnPacket;
    }

    private void OnPacket(Packet p)
    {
        switch (p.Type)
        {
            case PacketType.Chat:
                var (text, _) = TryDecrypt(p.Payload);
                MessageReceived?.Invoke(p.Sender, text, p.Timestamp);
                break;

            case PacketType.StatusUpdate:
                bool online = p.Payload.Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
                _state.Online[p.Sender] = online;
                StatusChanged?.Invoke(p.Sender, online);
                break;

            case PacketType.Error:
                ErrorReceived?.Invoke(p.Payload);
                break;
        }
    }

    /// <summary>Mã hóa tin cho người nhận (và cho chính mình) rồi gửi gói CHAT.</summary>
    public async Task SendChatAsync(string to, string text)
    {
        var theirKey = await GetPublicKeyAsync(to);
        var envelope = MessageCipher.Encrypt(text, theirKey, _state.Keys!.PublicKey);

        await _net.SendAsync(new Packet
        {
            Type = PacketType.Chat, Sender = _state.Username!, Receiver = to, Payload = envelope
        });
    }

    private async Task<string> GetPublicKeyAsync(string user)
    {
        if (_publicKeys.TryGetValue(user, out var cached)) return cached;

        var resp = await _net.RequestAsync(new Packet
        {
            Type = PacketType.GetPublicKey, Sender = _state.Username!, Receiver = user
        });
        ThrowIfFail(resp.Payload);

        _publicKeys[user] = resp.Payload;
        return resp.Payload;
    }

    public async Task<List<HistoryItem>> GetHistoryAsync(string partner)
    {
        var resp = await _net.RequestAsync(new Packet
        {
            Type = PacketType.GetHistory, Sender = _state.Username!, Receiver = partner
        });
        ThrowIfFail(resp.Payload);

        var packets = JsonSerializer.Deserialize<List<Packet>>(resp.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        return packets
            .OrderBy(p => p.Timestamp)
            .Select(p =>
            {
                var (text, ok) = TryDecrypt(p.Payload);
                return new HistoryItem(p.Sender, p.Receiver, text, p.Timestamp, ok);
            })
            .ToList();
    }

    /// <summary>Báo Server đánh dấu OFFLINE; lỗi mạng thì bỏ qua (Server tự phát hiện khi socket đóng).</summary>
    public async Task LogoutAsync()
    {
        try
        {
            await _net.SendAsync(new Packet { Type = PacketType.Logout, Sender = _state.Username ?? "" });
        }
        catch (IOException) { }
        _publicKeys.Clear();
    }

    private static void ThrowIfFail(string payload)
    {
        if (payload.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(payload[5..].Trim());
    }

    /// <summary>Giải mã bằng khóa riêng của mình; lỗi thì trả thông báo thay vì làm app crash.</summary>
    private (string Text, bool Ok) TryDecrypt(string payload)
    {
        try
        {
            return (MessageCipher.Decrypt(payload, _state.Keys!.PrivateKey), true);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ("[không giải mã được]", false);
        }
    }
}
