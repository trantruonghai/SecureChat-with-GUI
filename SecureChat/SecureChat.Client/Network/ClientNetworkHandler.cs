using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SecureChat.Client.Models;

namespace SecureChat.Client.Network;

/// <summary>
/// Tầng mạng phía Client: kết nối TcpClient, gửi/nhận Packet (mỗi gói 1 dòng JSON).
/// - RequestAsync: gửi gói và chờ phản hồi "&lt;TYPE&gt;_RESPONSE" (REGISTER, LOGIN, GET_HISTORY, GET_PUBLIC_KEY).
/// - PacketReceived: các gói do server chủ động đẩy xuống (CHAT, STATUS_UPDATE, ERROR).
/// </summary>
public sealed class ClientNetworkHandler : IAsyncDisposable
{
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Packet>> _pending = new();
    private volatile bool _closing;

    public event Action<Packet>? PacketReceived;
    public event Action? Disconnected;

    public bool IsConnected => !_closing && _tcp?.Connected == true;

    public async Task ConnectAsync(string host, int port)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);

        var stream = _tcp.GetStream();
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" };

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendAsync(Packet packet)
    {
        if (_writer == null || !IsConnected)
            throw new IOException("Chưa kết nối tới server.");

        await _sendLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(packet.ToJson());
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<Packet> RequestAsync(Packet request, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseType = PacketType.ResponseOf(request.Type);
        if (!_pending.TryAdd(responseType, tcs))
            throw new InvalidOperationException($"Đang chờ phản hồi {request.Type}, thử lại sau.");

        try
        {
            await SendAsync(request);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            using var reg = cts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Server không phản hồi.")));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(responseType, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line == null) break; // server đóng kết nối

                Packet? packet;
                try { packet = Packet.FromJson(line); }
                catch (JsonException) { continue; } // bỏ qua gói lỗi
                if (packet == null) continue;

                // X_RESPONSE khớp request đang chờ; còn lại (CHAT, STATUS_UPDATE, ERROR) là gói server đẩy xuống
                if (_pending.TryGetValue(packet.Type, out var tcs))
                    tcs.TrySetResult(packet);
                else
                    PacketReceived?.Invoke(packet);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // mất kết nối đột ngột hoặc đang tự đóng
        }
        finally
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new IOException("Mất kết nối tới server."));

            if (!_closing) Disconnected?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _cts?.Cancel();
        _tcp?.Close();
        _sendLock.Dispose();
        await Task.CompletedTask;
    }
}
