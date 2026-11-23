using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Services;
using SecureChat.Client.UI;

// Dùng: dotnet run -- [host] [port]   (mặc định 127.0.0.1:8888 - khớp Server của Hải)
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8888;

var state = new ClientState();
await using var net = new ClientNetworkHandler();

try
{
    await net.ConnectAsync(host, port);
}
catch (Exception ex)
{
    Console.WriteLine($"Không kết nối được tới {host}:{port} - {ex.Message}");
    return;
}

var auth = new AuthService(net, state);
var chat = new ChatService(net, state);
await new ConsoleUI(net, auth, chat, state).RunAsync();
