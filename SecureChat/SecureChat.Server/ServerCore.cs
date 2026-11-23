using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SecureChat.Server
{
    public class ServerCore
    {
        private const int MaxPayloadLength = 200000;
        private const int HistoryLimit = 200;
        private const string ServerName = "SERVER";
        private static readonly Regex UsernameRegex = new Regex("^[A-Za-z0-9_]{3,20}$");

        private TcpListener listener;
        private volatile bool isRunning = false;
        // username (không phân biệt hoa/thường) -> kết nối đang online
        private Dictionary<string, ClientConnection> onlineClients = new Dictionary<string, ClientConnection>(StringComparer.OrdinalIgnoreCase);
        private List<ClientConnection> allConnections = new List<ClientConnection>();
        private DatabaseManager dbManager;
        private readonly object lockObj = new object();

        public int Port { get; private set; }

        /// <summary>Số kết nối TCP đang mở (kể cả chưa đăng nhập).</summary>
        public int ConnectionCount
        {
            get { lock (lockObj) { return allConnections.Count; } }
        }

        /// <param name="port">0 = để hệ điều hành chọn cổng trống (dùng khi test).</param>
        public ServerCore(int port, string dbPath = "securechat.db")
        {
            listener = new TcpListener(IPAddress.Any, port);
            dbManager = new DatabaseManager(dbPath);
        }

        public void Start()
        {
            Listen();
            AcceptLoop();
        }

        public void Listen()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            isRunning = true;
            dbManager.ResetAllStatus();
            Console.WriteLine($"[SERVER] Đang khởi động và lắng nghe kết nối trên cổng {Port}...");
        }

        public void AcceptLoop()
        {
            while (isRunning)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException) { break; }          // listener đã bị Stop()
                catch (ObjectDisposedException) { break; }

                Thread clientThread = new Thread(() => HandleClient(client));
                clientThread.IsBackground = true;
                clientThread.Start();
            }
        }

        public void Stop()
        {
            isRunning = false;
            listener.Stop();
            List<ClientConnection> toClose;
            lock (lockObj) { toClose = allConnections.ToList(); }
            foreach (var c in toClose) c.Close();
        }

        // ---------------------------------------------------------------- mỗi client một luồng

        private void HandleClient(TcpClient client)
        {
            ClientConnection connection = new ClientConnection(client);
            lock (lockObj) { allConnections.Add(connection); }
            Console.WriteLine($"[CONNECT] {connection.Endpoint}");

            try
            {
                StreamReader reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));

                while (client.Connected)
                {
                    string? messageJson = reader.ReadLine();
                    if (messageJson == null) break;          // client đóng kết nối
                    if (messageJson.Length == 0) continue;

                    Packet? packet;
                    try { packet = Packet.FromJson(messageJson); }
                    catch (JsonException)
                    {
                        Console.WriteLine($"[WARN] Gói tin sai định dạng từ {connection.Endpoint}");
                        continue;
                    }
                    if (packet == null || string.IsNullOrEmpty(packet.Type)) continue;

                    try
                    {
                        Dispatch(packet, connection);
                    }
                    catch (Exception ex) when (!(ex is IOException || ex is ObjectDisposedException || ex is SocketException))
                    {
                        Console.WriteLine($"[ERROR] Lỗi xử lý {packet.Type}: {ex.Message}");
                        Fail(connection, packet.Type, "Lỗi xử lý trên server.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                // client ngắt đột ngột
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Lỗi kết nối client: {ex.Message}");
            }
            finally
            {
                // Dọn dẹp tuyệt đối không được ném lỗi ra ngoài: một exception không bắt trong luồng sẽ làm sập cả server.
                try { Logout(connection); }
                catch (Exception ex) { Console.WriteLine($"[ERROR] Lỗi khi đăng xuất {connection.Endpoint}: {ex.Message}"); }
                lock (lockObj) { allConnections.Remove(connection); }
                connection.Close();
                Console.WriteLine($"[DISCONNECT] {connection.Endpoint}");
            }
        }

        private void Dispatch(Packet packet, ClientConnection conn)
        {
            if (packet.Payload != null && packet.Payload.Length > MaxPayloadLength)
            {
                Fail(conn, packet.Type, "Gói tin quá lớn.");
                return;
            }

            switch (packet.Type.ToUpper())
            {
                case "REGISTER": HandleRegister(packet, conn); break;
                case "LOGIN": HandleLogin(packet, conn); break;
                case "LOGOUT": Logout(conn); break;
                case "CHAT": HandleChat(packet, conn); break;
                case "GET_HISTORY": HandleGetHistory(packet, conn); break;
                case "GET_PUBLIC_KEY": HandleGetPublicKey(packet, conn); break;
                case "STATUS_UPDATE": break; // Server tự quản lý trạng thái, bỏ qua
                default:
                    conn.Send(new Packet { Type = "ERROR", Sender = ServerName, Payload = $"Loại gói tin không hợp lệ: {packet.Type}" });
                    break;
            }
        }

        // ---------------------------------------------------------------- phản hồi

        private static void Respond(ClientConnection conn, string requestType, string payload)
        {
            conn.Send(new Packet
            {
                Type = requestType.ToUpper() + "_RESPONSE",
                Sender = ServerName,
                Receiver = conn.Username ?? "",
                Payload = payload
            });
        }

        /// <summary>Lỗi: với yêu cầu có phản hồi thì trả *_RESPONSE "FAIL: ...", còn lại (CHAT...) trả gói ERROR.</summary>
        private static void Fail(ClientConnection conn, string requestType, string reason)
        {
            switch (requestType.ToUpper())
            {
                case "REGISTER":
                case "LOGIN":
                case "GET_HISTORY":
                case "GET_PUBLIC_KEY":
                    Respond(conn, requestType, "FAIL: " + reason);
                    break;
                default:
                    conn.Send(new Packet { Type = "ERROR", Sender = ServerName, Payload = reason });
                    break;
            }
        }

        // ---------------------------------------------------------------- REGISTER / LOGIN / LOGOUT

        private void HandleRegister(Packet packet, ClientConnection conn)
        {
            string username = (packet.Sender ?? "").Trim();
            RegisterRequest? req = Packet.ParsePayload<RegisterRequest>(packet.Payload ?? "");

            if (!UsernameRegex.IsMatch(username))
            {
                Fail(conn, packet.Type, "Tên đăng nhập gồm 3-20 ký tự: chữ, số hoặc dấu gạch dưới.");
                return;
            }
            if (req == null || string.IsNullOrWhiteSpace(req.PasswordHash) || req.PasswordHash.Length > 200)
            {
                Fail(conn, packet.Type, "Dữ liệu đăng ký không hợp lệ.");
                return;
            }
            if (!IsValidPublicKey(req.PublicKey))
            {
                Fail(conn, packet.Type, "Public key không hợp lệ.");
                return;
            }

            bool regSuccess = dbManager.RegisterUser(username, req.PasswordHash, req.PublicKey);
            if (regSuccess)
            {
                Console.WriteLine($"[AUTH] Đăng ký mới: {username}");
                Respond(conn, packet.Type, "SUCCESS");
            }
            else
            {
                Fail(conn, packet.Type, "Tên đăng nhập đã tồn tại.");
            }
        }

        private static bool IsValidPublicKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                return false;
            }
        }

        private void HandleLogin(Packet packet, ClientConnection conn)
        {
            // Client /logout rồi đăng nhập lại trên cùng kết nối = đăng xuất phiên cũ.
            if (conn.Username != null) Logout(conn);

            string username = (packet.Sender ?? "").Trim();
            bool loginSuccess = username.Length > 0 && dbManager.ValidateUser(username, packet.Payload ?? "");
            if (!loginSuccess)
            {
                Console.WriteLine($"[AUTH] Đăng nhập thất bại: '{username}' từ {conn.Endpoint}");
                Fail(conn, packet.Type, "Sai tên đăng nhập hoặc mật khẩu.");
                return;
            }

            string canonical = dbManager.FindUser(username) ?? username;
            bool alreadyOnline;
            lock (lockObj)
            {
                alreadyOnline = onlineClients.ContainsKey(canonical);
                if (!alreadyOnline)
                {
                    onlineClients.Add(canonical, conn);
                    conn.Username = canonical;
                }
            }
            if (alreadyOnline)
            {
                Fail(conn, packet.Type, "Tài khoản đang đăng nhập ở nơi khác.");
                return;
            }

            dbManager.SetStatus(canonical, "Online");
            Console.WriteLine($"[AUTH] User '{canonical}' đã đăng nhập thành công.");
            Respond(conn, packet.Type, "SUCCESS");

            // 1) cho người mới biết ai đang online, 2) báo mọi người khác là người này vừa online
            List<string> others;
            lock (lockObj) { others = onlineClients.Keys.Where(k => !k.Equals(canonical, StringComparison.OrdinalIgnoreCase)).ToList(); }
            foreach (string other in others)
                conn.Send(StatusPacket(other, "ONLINE"));
            Broadcast(StatusPacket(canonical, "ONLINE"), canonical);
        }

        private void Logout(ClientConnection conn)
        {
            string? name = conn.Username;
            if (name == null) return;

            conn.Username = null;
            lock (lockObj)
            {
                // chỉ gỡ nếu đúng kết nối này (tránh xóa nhầm phiên đăng nhập khác của cùng tài khoản)
                if (onlineClients.TryGetValue(name, out ClientConnection? current) && ReferenceEquals(current, conn))
                    onlineClients.Remove(name);
            }
            try { dbManager.SetStatus(name, "Offline"); }
            catch (Exception ex) { Console.WriteLine($"[ERROR] Không cập nhật được Status của '{name}': {ex.Message}"); }
            Console.WriteLine($"[DISCONNECT] User '{name}' đã đăng xuất / ngắt kết nối.");
            Broadcast(StatusPacket(name, "OFFLINE"), null);
        }

        private static Packet StatusPacket(string username, string status) =>
            new Packet { Type = "STATUS_UPDATE", Sender = username, Receiver = "", Payload = status };

        /// <summary>Gửi một gói cho mọi người đang online (trừ exceptUser). Lỗi của một client không ảnh hưởng client khác.</summary>
        private void Broadcast(Packet packet, string? exceptUser)
        {
            List<KeyValuePair<string, ClientConnection>> snapshot;
            lock (lockObj) { snapshot = onlineClients.ToList(); }
            foreach (var kv in snapshot)
            {
                if (exceptUser != null && kv.Key.Equals(exceptUser, StringComparison.OrdinalIgnoreCase)) continue;
                try { kv.Value.Send(packet); }
                catch (Exception) { /* client đã rớt, luồng của nó sẽ tự dọn */ }
            }
        }

        // ---------------------------------------------------------------- CHAT / HISTORY / PUBLIC KEY

        private void HandleChat(Packet packet, ClientConnection conn)
        {
            if (conn.Username == null) { Fail(conn, packet.Type, "Bạn chưa đăng nhập."); return; }
            if (string.IsNullOrWhiteSpace(packet.Payload)) return;

            string? receiver = dbManager.FindUser((packet.Receiver ?? "").Trim());
            if (receiver == null) { Fail(conn, packet.Type, $"Người nhận '{packet.Receiver}' không tồn tại."); return; }

            // Không tin Sender/Timestamp do Client gửi: dùng danh tính của phiên đăng nhập và giờ của Server.
            Packet message = new Packet
            {
                Type = "CHAT",
                Sender = conn.Username,
                Receiver = receiver,
                Payload = packet.Payload,   // đã mã hóa từ Client, Server không đọc được
                Timestamp = DateTime.UtcNow
            };

            dbManager.SaveMessage(message.Sender, message.Receiver, message.Payload, message.Timestamp);
            Console.WriteLine($"[CHAT] Tin nhắn từ {message.Sender} gửi tới {message.Receiver}");

            // lưu cả khi người nhận offline -> xem lại qua GET_HISTORY
            ClientConnection? target;
            lock (lockObj) { onlineClients.TryGetValue(receiver, out target); }
            if (target != null)
            {
                try { target.Send(message); }
                catch (Exception) { /* người nhận vừa rớt mạng, tin đã được lưu */ }
            }
        }

        private void HandleGetHistory(Packet packet, ClientConnection conn)
        {
            if (conn.Username == null) { Fail(conn, packet.Type, "Bạn chưa đăng nhập."); return; }
            string partner = (packet.Receiver ?? "").Trim();
            if (partner.Length == 0) { Fail(conn, packet.Type, "Thiếu tên người đối thoại."); return; }

            List<Packet> history = dbManager.GetHistory(conn.Username, partner, HistoryLimit);
            Respond(conn, packet.Type, JsonSerializer.Serialize(history));
        }

        private void HandleGetPublicKey(Packet packet, ClientConnection conn)
        {
            if (conn.Username == null) { Fail(conn, packet.Type, "Bạn chưa đăng nhập."); return; }
            string? key = dbManager.GetPublicKey((packet.Receiver ?? "").Trim());
            if (key == null) { Fail(conn, packet.Type, "Không tìm thấy người dùng."); return; }

            Respond(conn, packet.Type, key); // thành công: Payload chính là public key (Base64)
        }

        // ---------------------------------------------------------------- một kết nối TCP

        /// <summary>Gắn với mỗi TcpClient một StreamWriter + khóa ghi riêng, vì nhiều luồng có thể cùng gửi vào một client.</summary>
        private class ClientConnection
        {
            private readonly StreamWriter writer;
            private readonly object writeLock = new object();

            public TcpClient Client { get; }
            public string Endpoint { get; }
            public string? Username { get; set; }

            public ClientConnection(TcpClient client)
            {
                Client = client;
                Endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
                writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            }

            public void Send(Packet packet)
            {
                lock (writeLock) { writer.WriteLine(packet.ToJson()); }
            }

            public void Close()
            {
                try { Client.Close(); } catch (Exception) { }
            }
        }
    }
}
