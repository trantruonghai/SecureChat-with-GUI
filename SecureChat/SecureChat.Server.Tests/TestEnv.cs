using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SecureChat.Server.Tests;

/// <summary>Khởi động ServerCore thật (cổng ngẫu nhiên, file SQLite tạm) cho mỗi test.</summary>
public sealed class TestEnv : IAsyncDisposable
{
    private readonly Thread _acceptThread;
    private readonly List<TestClient> _clients = new();

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"securechat-test-{Guid.NewGuid():N}.db");
    public ServerCore Server { get; }

    public TestEnv()
    {
        Server = new ServerCore(0, DbPath);
        Server.Listen();
        _acceptThread = new Thread(Server.AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    public async Task<TestClient> ConnectAsync()
    {
        var client = new TestClient();
        _clients.Add(client);
        await client.ConnectAsync(Server.Port);
        return client;
    }

    /// <summary>Chạy một câu SELECT trực tiếp lên file DB, trả về cột đầu tiên dạng chuỗi.</summary>
    public List<string> Query(string sql)
    {
        var result = new List<string>();
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Chờ đến khi Server đã lưu đủ số tin nhắn (gói CHAT không có phản hồi khi thành công).</summary>
    public async Task WaitForMessagesAsync(int count, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Query("SELECT EncryptedContent FROM Messages").Count < count)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Server chưa lưu đủ {count} tin nhắn.");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        Server.Stop();
        _acceptThread.Join(2000);

        // chờ các luồng xử lý client dọn xong (chúng còn ghi Status vào DB) rồi mới xóa file
        var until = DateTime.UtcNow.AddSeconds(3);
        while (Server.ConnectionCount > 0 && DateTime.UtcNow < until) await Task.Delay(20);

        SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

/// <summary>Gói tin đúng như trên đường truyền. Đọc theo kiểu phân biệt hoa/thường để bắt lỗi sai tên trường.</summary>
public class WirePacket
{
    public string Type { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Receiver { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Client thử độc lập (không dùng code của Nhân): nói chuyện với Server bằng TCP thuần + JSON mỗi dòng,
/// đúng như đặc tả trong docs/PROTOCOL.md.
/// </summary>
public sealed class TestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private StreamWriter _writer = null!;
    private readonly List<WirePacket> _received = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Username { get; private set; } = "";
    public string PublicKey { get; } = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo());

    public async Task ConnectAsync(int port)
    {
        await _tcp.ConnectAsync("127.0.0.1", port);
        var stream = _tcp.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var p = JsonSerializer.Deserialize<WirePacket>(line); // mặc định: phân biệt hoa/thường
                    if (p != null) lock (_gate) _received.Add(p);
                }
            }
            catch (Exception) { /* ngắt kết nối */ }
        });
    }

    /// <summary>Gửi một dòng bất kỳ (kể cả JSON hỏng) để thử độ bền của Server.</summary>
    public async Task SendLineAsync(string line)
    {
        await _sendLock.WaitAsync();
        try { await _writer.WriteLineAsync(line); }
        finally { _sendLock.Release(); }
    }

    public Task SendAsync(string type, string receiver = "", string payload = "", string? sender = null) =>
        SendLineAsync(JsonSerializer.Serialize(new
        {
            Type = type,
            Sender = sender ?? Username,
            Receiver = receiver,
            Payload = payload,
            Timestamp = DateTime.UtcNow
        }));

    /// <summary>Gửi yêu cầu và chờ gói &lt;TYPE&gt;_RESPONSE tương ứng.</summary>
    public async Task<WirePacket> RequestAsync(string type, string receiver = "", string payload = "", int timeoutMs = 5000)
    {
        int before;
        lock (_gate) before = _received.Count(p => p.Type == type + "_RESPONSE");
        await SendAsync(type, receiver, payload);

        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_gate)
            {
                var hits = _received.Where(p => p.Type == type + "_RESPONSE").ToList();
                if (hits.Count > before) return hits[before];
            }
            if (DateTime.UtcNow > until) throw new TimeoutException($"Không có {type}_RESPONSE.");
            await Task.Delay(10);
        }
    }

    /// <returns>Payload phản hồi: "SUCCESS" hoặc "FAIL: ..."</returns>
    public async Task<string> RegisterAsync(string user, string password)
    {
        Username = user;
        var payload = JsonSerializer.Serialize(new { PasswordHash = HashOf(user, password), PublicKey });
        return (await RequestAsync("REGISTER", "SERVER", payload)).Payload;
    }

    public async Task<string> LoginAsync(string user, string password)
    {
        Username = user;
        return (await RequestAsync("LOGIN", "SERVER", HashOf(user, password))).Payload;
    }

    /// <summary>Bản băm giả lập của Client (Server chỉ lưu và so sánh chuỗi, không cần biết thuật toán).</summary>
    public static string HashOf(string user, string password) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToLowerInvariant() + ":" + password)));

    /// <summary>"Bản mã" giả lập: chuỗi Base64 ngẫu nhiên. Server phải lưu và chuyển tiếp nguyên vẹn, không đọc/sửa.</summary>
    public static string FakeCipher() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public async Task<WirePacket> WaitForAsync(Func<WirePacket, bool> predicate, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_gate)
            {
                var hit = _received.FirstOrDefault(predicate);
                if (hit != null) return hit;
            }
            if (DateTime.UtcNow > until) throw new TimeoutException("Không nhận được gói tin mong đợi.");
            await Task.Delay(10);
        }
    }

    public int CountReceived(Func<WirePacket, bool> predicate)
    {
        lock (_gate) return _received.Count(predicate);
    }

    public ValueTask DisposeAsync()
    {
        try { _tcp.Close(); } catch (Exception) { }
        return ValueTask.CompletedTask;
    }
}
