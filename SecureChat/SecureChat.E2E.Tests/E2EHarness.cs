using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Security;
using SecureChat.Client.Services;
using Xunit;

namespace SecureChat.E2E.Tests;

/// <summary>Server thật (cổng ngẫu nhiên, file SQLite tạm) cho mỗi test.</summary>
public sealed class E2EEnv : IAsyncDisposable
{
    private static readonly string KeyDir = Path.Combine(Path.GetTempPath(), "securechat-e2e-keys");
    private readonly Thread _acceptThread;
    private readonly List<E2EClient> _clients = new();

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"securechat-e2e-{Guid.NewGuid():N}.db");
    public SecureChat.Server.ServerCore Server { get; }

    public E2EEnv()
    {
        Directory.CreateDirectory(KeyDir);
        LocalKeyStore.DirectoryOverride = KeyDir; // khóa riêng của test không ghi vào AppData thật

        Server = new SecureChat.Server.ServerCore(0, DbPath);
        Server.Listen();
        _acceptThread = new Thread(Server.AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    public async Task<E2EClient> NewClientAsync()
    {
        var c = new E2EClient();
        _clients.Add(c);
        await c.Net.ConnectAsync("127.0.0.1", Server.Port);
        return c;
    }

    /// <summary>Đọc cột đầu tiên của câu SELECT chạy thẳng lên file DB.</summary>
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

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        Server.Stop();
        _acceptThread.Join(2000);

        var until = DateTime.UtcNow.AddSeconds(3);
        while (Server.ConnectionCount > 0 && DateTime.UtcNow < until) await Task.Delay(20);

        SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

/// <summary>Một Client thật (ClientNetworkHandler + AuthService + ChatService) kèm hàng đợi ghi lại sự kiện.</summary>
public sealed class E2EClient : IAsyncDisposable
{
    public string Username { get; } = "u" + Guid.NewGuid().ToString("N")[..12];
    public ClientNetworkHandler Net { get; } = new();
    public ClientState State { get; } = new();
    public AuthService Auth { get; }
    public ChatService Chat { get; }

    public ConcurrentQueue<(string Sender, string Text)> Messages { get; } = new();
    public ConcurrentQueue<(string User, bool Online)> Statuses { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();

    public E2EClient()
    {
        Auth = new AuthService(Net, State);
        Chat = new ChatService(Net, State);
        Chat.MessageReceived += (s, t, _) => Messages.Enqueue((s, t));
        Chat.StatusChanged += (u, o) => Statuses.Enqueue((u, o));
        Chat.ErrorReceived += e => Errors.Enqueue(e);
    }

    public async Task RegisterAndLoginAsync(string password = "mat-khau-123")
    {
        var reg = await Auth.RegisterAsync(Username, password);
        Assert.True(reg.Ok, reg.Message);
        var login = await Auth.LoginAsync(Username, password);
        Assert.True(login.Ok, login.Message);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Điều kiện chờ không xảy ra trong thời gian cho phép.");
            await Task.Delay(20);
        }
    }

    public ValueTask DisposeAsync() => Net.DisposeAsync();
}
