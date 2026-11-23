using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Công cụ gõ lệnh để thử Server của Hải bằng tay (khi chưa có Client của Nhân).
// Dùng:  dotnet run --project SecureChat.TestConsole -- [host] [cổng]      (mặc định 127.0.0.1 8888)
//
// LƯU Ý: nội dung chat ở đây chỉ được "mã hóa giả" bằng Base64 để thử đường đi của tin nhắn.
// Mã hóa thật (AES/RSA) là phần của Client (Nhân).

Console.OutputEncoding = Encoding.UTF8;
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8888;

using var tcp = new TcpClient();
try { await tcp.ConnectAsync(host, port); }
catch (Exception ex)
{
    Console.WriteLine($"Không kết nối được tới {host}:{port} - {ex.Message}");
    Console.WriteLine("Server đã chạy chưa? Đúng cổng chưa?");
    return;
}

var stream = tcp.GetStream();
var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var reader = new StreamReader(stream, new UTF8Encoding(false));
string me = "";

_ = Task.Run(async () =>
{
    try
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null) Show(line);
    }
    catch (Exception) { }
    Write("\n[!] Server đã đóng kết nối.", ConsoleColor.Red);
});

Console.WriteLine($"Đã kết nối {host}:{port}. Gõ 'help' để xem lệnh.");

while (true)
{
    var input = Console.ReadLine();
    if (input == null) { await Task.Delay(1500); break; } // hết dữ liệu nhập (chạy từ file): chờ nốt phản hồi
    input = input.Trim();
    if (input.Length == 0) continue;

    var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();
    string a1 = parts.Length > 1 ? parts[1] : "";
    string rest = parts.Length > 2 ? parts[2] : "";

    switch (cmd)
    {
        case "help":
            Console.WriteLine(
                "register <tên> <mật khẩu>   đăng ký (tự sinh public key RSA)\n" +
                "login <tên> <mật khẩu>      đăng nhập\n" +
                "logout                      đăng xuất\n" +
                "key <tên>                   lấy public key của người khác (GET_PUBLIC_KEY)\n" +
                "chat <tên> <nội dung>       gửi tin (mã hóa giả bằng Base64)\n" +
                "history <tên>               xem lịch sử với một người (GET_HISTORY)\n" +
                "raw <json>                  gửi nguyên một dòng JSON (thử gói lỗi)\n" +
                "wait <ms>                   chờ (dùng khi chạy từ file kịch bản)\n" +
                "quit                        thoát (ngắt kết nối đột ngột nếu đang online)");
            break;

        case "register" when a1.Length > 0 && rest.Length > 0:
            me = a1;
            var publicKey = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo());
            Send("REGISTER", "SERVER", JsonSerializer.Serialize(new { PasswordHash = HashOf(a1, rest), PublicKey = publicKey }));
            break;

        case "login" when a1.Length > 0 && rest.Length > 0:
            me = a1;
            Send("LOGIN", "SERVER", HashOf(a1, rest));
            break;

        case "logout": Send("LOGOUT", "", ""); break;
        case "key" when a1.Length > 0: Send("GET_PUBLIC_KEY", a1, ""); break;
        case "history" when a1.Length > 0: Send("GET_HISTORY", a1, ""); break;
        case "chat" when a1.Length > 0 && rest.Length > 0:
            Send("CHAT", a1, Convert.ToBase64String(Encoding.UTF8.GetBytes(rest)));
            break;

        case "raw": writer.WriteLine(input.Substring(3).Trim()); break;
        case "wait" when int.TryParse(a1, out var ms): await Task.Delay(ms); break;
        case "quit": return;
        default: Write("Lệnh không hợp lệ hoặc thiếu tham số. Gõ 'help'.", ConsoleColor.Red); break;
    }
}

// ---------------------------------------------------------------- hàm phụ

void Send(string type, string receiver, string payload)
{
    var json = JsonSerializer.Serialize(new { Type = type, Sender = me, Receiver = receiver, Payload = payload, Timestamp = DateTime.UtcNow });
    writer.WriteLine(json);
    Write($"→ {type}", ConsoleColor.DarkGray);
}

static string HashOf(string user, string password) =>
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToLowerInvariant() + ":" + password)));

static string Decode(string payload)
{
    try { return Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
    catch (FormatException) { return payload; }
}

void Show(string line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        string type = root.GetProperty("Type").GetString() ?? "";
        string sender = root.GetProperty("Sender").GetString() ?? "";
        string payload = root.GetProperty("Payload").GetString() ?? "";

        switch (type)
        {
            case "CHAT":
                var time = root.GetProperty("Timestamp").GetDateTime().ToLocalTime();
                Write($"\n[{time:HH:mm:ss}] {sender}: {Decode(payload)}", ConsoleColor.Cyan);
                break;
            case "STATUS_UPDATE":
                Write($"\n* {sender} {(payload == "ONLINE" ? "đã ONLINE" : "đã OFFLINE")}", payload == "ONLINE" ? ConsoleColor.Green : ConsoleColor.DarkGray);
                break;
            case "GET_HISTORY_RESPONSE":
                if (payload.StartsWith("FAIL")) { Write($"\n← {type}: {payload}", ConsoleColor.Red); break; }
                using (var arr = JsonDocument.Parse(payload))
                {
                    Write($"\n← Lịch sử ({arr.RootElement.GetArrayLength()} tin):", ConsoleColor.Yellow);
                    foreach (var m in arr.RootElement.EnumerateArray())
                        Write($"   [{m.GetProperty("Timestamp").GetDateTime().ToLocalTime():dd/MM HH:mm:ss}] {m.GetProperty("Sender").GetString()}: {Decode(m.GetProperty("Payload").GetString() ?? "")}", ConsoleColor.Gray);
                }
                break;
            case "ERROR":
                Write($"\n← ERROR: {payload}", ConsoleColor.Red);
                break;
            default:
                bool fail = payload.StartsWith("FAIL");
                string shown = payload.Length > 60 ? payload.Substring(0, 60) + "…" : payload;
                Write($"\n← {type}: {shown}", fail ? ConsoleColor.Red : ConsoleColor.Green);
                break;
        }
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        Write($"\n← (gói lạ) {line}", ConsoleColor.Magenta);
    }
}

static void Write(string text, ConsoleColor color)
{
    var old = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = old;
}
