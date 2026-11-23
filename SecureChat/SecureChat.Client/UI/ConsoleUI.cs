using System.Text;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Services;

namespace SecureChat.Client.UI;

/// <summary>Giao diện console: menu đăng nhập/đăng ký + vòng lặp lệnh chat.</summary>
public class ConsoleUI
{
    private readonly ClientNetworkHandler _net;
    private readonly AuthService _auth;
    private readonly ChatService _chat;
    private readonly ClientState _state;
    private volatile bool _connectionLost;

    public ConsoleUI(ClientNetworkHandler net, AuthService auth, ChatService chat, ClientState state)
    {
        _net = net;
        _auth = auth;
        _chat = chat;
        _state = state;

        _net.Disconnected += () =>
        {
            _connectionLost = true;
            WriteLine("\n[!] Mất kết nối tới server. Nhấn Enter để thoát.", ConsoleColor.Red);
        };
        _chat.MessageReceived += (sender, text, time) =>
            WriteLine($"\n[{time.ToLocalTime():HH:mm}] {sender}: {text}", ConsoleColor.Cyan);
        _chat.ErrorReceived += msg => WriteLine($"\n[Lỗi từ server] {msg}", ConsoleColor.Red);
        _chat.StatusChanged += (user, online) =>
            WriteLine($"\n* {user} {(online ? "đã online" : "đã offline")}",
                online ? ConsoleColor.Green : ConsoleColor.DarkGray);
    }

    public async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        WriteLine("===== SECURECHAT =====", ConsoleColor.Yellow);

        while (!_connectionLost)
        {
            bool keepGoing = _state.IsLoggedIn ? await ChatLoopAsync() : await AuthMenuAsync();
            if (!keepGoing) break;
        }
    }

    // ---------- Menu đăng nhập / đăng ký ----------

    private async Task<bool> AuthMenuAsync()
    {
        Console.WriteLine("\n1. Đăng nhập\n2. Đăng ký\n0. Thoát");
        Console.Write("Chọn: ");
        var choice = Console.ReadLine()?.Trim();
        if (_connectionLost) return false;

        switch (choice)
        {
            case "1":
            case "2":
                Console.Write("Tên đăng nhập: ");
                var user = Console.ReadLine()?.Trim() ?? "";
                var pass = ReadPassword("Mật khẩu: ");
                if (user.Length == 0 || pass.Length == 0)
                {
                    WriteLine("Không được để trống.", ConsoleColor.Red);
                    return true;
                }
                try
                {
                    var (ok, msg) = choice == "1"
                        ? await _auth.LoginAsync(user, pass)
                        : await _auth.RegisterAsync(user, pass);
                    WriteLine(msg, ok ? ConsoleColor.Green : ConsoleColor.Red);
                    if (ok && choice == "1") PrintHelp();
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
                {
                    WriteLine($"Lỗi: {ex.Message}", ConsoleColor.Red);
                }
                return true;

            case "0":
                return false;

            default:
                WriteLine("Lựa chọn không hợp lệ.", ConsoleColor.Red);
                return true;
        }
    }

    // ---------- Vòng lặp chat ----------

    private async Task<bool> ChatLoopAsync()
    {
        Console.Write(_state.CurrentPartner == null ? "> " : $"[{_state.CurrentPartner}] > ");
        var line = Console.ReadLine();
        if (_connectionLost || line == null) return false;
        line = line.Trim();
        if (line.Length == 0) return true;

        if (!line.StartsWith('/'))
        {
            if (_state.CurrentPartner == null)
            {
                WriteLine("Chưa chọn người nhận. Dùng /to <tên> trước.", ConsoleColor.Red);
                return true;
            }
            try
            {
                await _chat.SendChatAsync(_state.CurrentPartner, line);
                WriteLine($"[{DateTime.Now:HH:mm}] Bạn: {line}", ConsoleColor.Gray);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
                WriteLine($"Không gửi được: {ex.Message}", ConsoleColor.Red);
            }
            return true;
        }

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (parts[0].ToLowerInvariant())
        {
            case "/help":
                PrintHelp();
                break;

            case "/online":
                PrintOnline();
                break;

            case "/to":
                if (arg.Length == 0) { WriteLine("Dùng: /to <tên>", ConsoleColor.Red); break; }
                _state.CurrentPartner = arg;
                await ShowHistoryAsync(arg);
                break;

            case "/history":
                var target = arg.Length > 0 ? arg : _state.CurrentPartner;
                if (target == null) { WriteLine("Dùng: /history <tên>", ConsoleColor.Red); break; }
                await ShowHistoryAsync(target);
                break;

            case "/logout":
                await _chat.LogoutAsync();
                _state.Reset();
                WriteLine("Đã đăng xuất.", ConsoleColor.Yellow);
                break;

            case "/quit":
                await _chat.LogoutAsync();
                return false;

            default:
                WriteLine("Lệnh không hợp lệ. Gõ /help để xem danh sách lệnh.", ConsoleColor.Red);
                break;
        }
        return true;
    }

    private async Task ShowHistoryAsync(string partner)
    {
        try
        {
            var items = await _chat.GetHistoryAsync(partner);
            WriteLine($"--- Lịch sử với {partner} ({items.Count} tin) ---", ConsoleColor.Yellow);
            foreach (var m in items)
            {
                var who = m.Sender.Equals(_state.Username, StringComparison.OrdinalIgnoreCase) ? "Bạn" : m.Sender;
                WriteLine($"[{m.Timestamp.ToLocalTime():dd/MM HH:mm}] {who}: {m.Text}",
                    m.Decrypted ? ConsoleColor.Gray : ConsoleColor.DarkRed);
            }
            WriteLine("--- Hết ---", ConsoleColor.Yellow);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException
                                       or System.Text.Json.JsonException)
        {
            WriteLine($"Không tải được lịch sử: {ex.Message}", ConsoleColor.Red);
        }
    }

    private void PrintOnline()
    {
        var online = _state.Online.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(x => x).ToList();
        WriteLine(online.Count == 0 ? "Chưa có ai online." : "Đang online: " + string.Join(", ", online),
            ConsoleColor.Green);
    }

    private static void PrintHelp() => Console.WriteLine(
        "Lệnh: /to <tên> (chọn người chat + xem lịch sử) | /history [tên] | /online | /logout | /quit | /help");

    // ---------- Tiện ích ----------

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static readonly object ConsoleLock = new();
    private static void WriteLine(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
    }
}
