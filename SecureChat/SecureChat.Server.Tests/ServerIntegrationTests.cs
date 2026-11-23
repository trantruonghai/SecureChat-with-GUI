using System.Text.Json;
using Xunit;

namespace SecureChat.Server.Tests;

/// <summary>
/// Chạy Server thật rồi nói chuyện bằng TCP + JSON đúng đặc tả (docs/PROTOCOL.md).
/// Mỗi test ứng với một mục trong lộ trình của Hải (xem docs/NHIEM_VU_HAI.md).
/// </summary>
public class ServerIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // ============================================================ Ngày 2: đăng ký / đăng nhập

    [Fact]
    public async Task Register_Then_Login_Succeeds()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();

        Assert.Equal("SUCCESS", await a.RegisterAsync("alice", "secret1"));
        Assert.Equal("SUCCESS", await a.LoginAsync("alice", "secret1"));
    }

    [Fact]
    public async Task Register_DuplicateUsername_IsRejected_CaseInsensitive()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();
        var b = await env.ConnectAsync();

        Assert.Equal("SUCCESS", await a.RegisterAsync("alice", "secret1"));
        Assert.StartsWith("FAIL", await b.RegisterAsync("ALICE", "another1"));
    }

    [Fact]
    public async Task Register_InvalidUsername_Or_BadPublicKey_Or_MissingHash_IsRejected()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();

        Assert.StartsWith("FAIL", await a.RegisterAsync("a b", "secret1"));
        Assert.StartsWith("FAIL", await a.RegisterAsync("al", "secret1"));

        var badKey = JsonSerializer.Serialize(new { PasswordHash = "x", PublicKey = "không-phải-key" });
        Assert.StartsWith("FAIL", (await a.RequestAsync("REGISTER", "SERVER", badKey)).Payload);

        var noHash = JsonSerializer.Serialize(new { PasswordHash = "", a.PublicKey });
        Assert.StartsWith("FAIL", (await a.RequestAsync("REGISTER", "SERVER", noHash)).Payload);

        Assert.StartsWith("FAIL", (await a.RequestAsync("REGISTER", "SERVER", "đây không phải JSON")).Payload);
    }

    [Fact]
    public async Task Login_WrongPassword_Or_UnknownUser_IsRejected()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();
        await a.RegisterAsync("alice", "secret1");

        Assert.StartsWith("FAIL", await a.LoginAsync("alice", "wrong-password"));
        Assert.StartsWith("FAIL", await a.LoginAsync("nobody", "secret1"));
    }

    [Fact]
    public async Task Login_UsernameIsCaseInsensitive()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();
        await a.RegisterAsync("Alice", "secret1");

        Assert.Equal("SUCCESS", await a.LoginAsync("alice", "secret1"));
    }

    [Fact]
    public async Task Login_WhileAlreadyOnlineElsewhere_IsRejected()
    {
        await using var env = new TestEnv();
        var a1 = await env.ConnectAsync();
        var a2 = await env.ConnectAsync();
        await a1.RegisterAsync("alice", "secret1");

        Assert.Equal("SUCCESS", await a1.LoginAsync("alice", "secret1"));
        Assert.StartsWith("FAIL", await a2.LoginAsync("alice", "secret1"));
    }

    [Fact]
    public async Task Database_StoresOnlyThePasswordHashTheClientSent()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();
        await a.RegisterAsync("alice", "secret1");

        var stored = env.Query("SELECT PasswordHash FROM Users WHERE Username = 'alice'");
        Assert.Equal(1, stored.Count);
        Assert.Equal(TestClient.HashOf("alice", "secret1"), stored[0]);
        Assert.False(stored[0].Contains("secret1"));
    }

    // ============================================================ Ngày 2 + 4: danh sách online và trạng thái

    [Fact]
    public async Task StatusUpdates_AreSent_OnLoginAndOnDisconnect()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");

        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");

        // alice (đã online) được báo bob online; bob (người mới) được báo alice đang online
        var s1 = await alice.WaitForAsync(p => p.Type == "STATUS_UPDATE" && p.Sender == "bob");
        Assert.Equal("ONLINE", s1.Payload);
        var s2 = await bob.WaitForAsync(p => p.Type == "STATUS_UPDATE" && p.Sender == "alice");
        Assert.Equal("ONLINE", s2.Payload);
        Assert.Equal("Online", env.Query("SELECT Status FROM Users WHERE Username = 'bob'")[0]);

        await bob.DisposeAsync(); // bob rớt kết nối đột ngột
        var s3 = await alice.WaitForAsync(p => p.Type == "STATUS_UPDATE" && p.Sender == "bob" && p.Payload == "OFFLINE");
        Assert.Equal("OFFLINE", s3.Payload);

        var until = DateTime.UtcNow.AddSeconds(3); // cột Status trong DB được cập nhật ngay sau đó
        while (env.Query("SELECT Status FROM Users WHERE Username = 'bob'")[0] != "Offline" && DateTime.UtcNow < until)
            await Task.Delay(20);
        Assert.Equal("Offline", env.Query("SELECT Status FROM Users WHERE Username = 'bob'")[0]);
    }

    [Fact]
    public async Task Logout_MarksUserOffline_And_AllowsLoginAsAnotherUserOnSameConnection()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        var carol = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await carol.RegisterAsync("carol", "secret3");
        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");

        await alice.SendAsync("LOGOUT");
        var s = await bob.WaitForAsync(p => p.Type == "STATUS_UPDATE" && p.Sender == "alice" && p.Payload == "OFFLINE");
        Assert.Equal("OFFLINE", s.Payload);

        // cùng kết nối đó đăng nhập tiếp bằng tài khoản khác
        Assert.Equal("SUCCESS", await alice.LoginAsync("carol", "secret3"));
        var on = await bob.WaitForAsync(p => p.Type == "STATUS_UPDATE" && p.Sender == "carol" && p.Payload == "ONLINE");
        Assert.Equal("ONLINE", on.Payload);
    }

    // ============================================================ Ngày 3: định tuyến tin nhắn

    [Fact]
    public async Task GetPublicKey_ReturnsRegisteredKey_Or_Fail()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");

        Assert.StartsWith("FAIL", (await alice.RequestAsync("GET_PUBLIC_KEY", "bob")).Payload); // chưa đăng nhập

        await alice.LoginAsync("alice", "secret1");
        Assert.Equal(bob.PublicKey, (await alice.RequestAsync("GET_PUBLIC_KEY", "bob")).Payload);
        Assert.StartsWith("FAIL", (await alice.RequestAsync("GET_PUBLIC_KEY", "nobody")).Payload);
    }

    [Fact]
    public async Task Chat_IsForwardedToOnlineReceiver_Unchanged()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");

        var cipher = TestClient.FakeCipher();
        await alice.SendAsync("CHAT", "bob", cipher);

        var pushed = await bob.WaitForAsync(p => p.Type == "CHAT");
        Assert.Equal("alice", pushed.Sender);
        Assert.Equal("bob", pushed.Receiver);
        Assert.Equal(cipher, pushed.Payload); // Server không đọc/sửa nội dung
        Assert.True(pushed.Timestamp > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Chat_SenderCannotBeSpoofed()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");

        await alice.SendAsync("CHAT", "bob", TestClient.FakeCipher(), sender: "mallory"); // giả mạo

        var pushed = await bob.WaitForAsync(p => p.Type == "CHAT");
        Assert.Equal("alice", pushed.Sender);
    }

    [Fact]
    public async Task Chat_ToUnknownUser_Or_WithoutLogin_ReturnsErrorPacket()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var anon = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await alice.LoginAsync("alice", "secret1");

        await alice.SendAsync("CHAT", "nobody", TestClient.FakeCipher());
        var err1 = await alice.WaitForAsync(p => p.Type == "ERROR");
        Assert.False(string.IsNullOrEmpty(err1.Payload));

        await anon.SendAsync("CHAT", "alice", TestClient.FakeCipher(), sender: "anon");
        var err2 = await anon.WaitForAsync(p => p.Type == "ERROR");
        Assert.False(string.IsNullOrEmpty(err2.Payload));
        Assert.Equal(0, alice.CountReceived(p => p.Type == "CHAT")); // tin của người chưa đăng nhập không được chuyển đi
    }

    [Fact]
    public async Task Chat_WhenReceiverDropsConnection_DoesNotBreakSender()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");
        await bob.DisposeAsync();

        await alice.SendAsync("CHAT", "bob", TestClient.FakeCipher());
        await env.WaitForMessagesAsync(1);

        // alice vẫn dùng được kết nối
        Assert.Equal(bob.PublicKey, (await alice.RequestAsync("GET_PUBLIC_KEY", "bob")).Payload);
    }

    // ============================================================ Ngày 4: lưu tin nhắn + truy xuất lịch sử

    [Fact]
    public async Task Chat_IsStoredInDatabase_ExactlyAsReceived()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await alice.LoginAsync("alice", "secret1");
        await bob.LoginAsync("bob", "secret2");

        var cipher = TestClient.FakeCipher();
        await alice.SendAsync("CHAT", "bob", cipher);
        await env.WaitForMessagesAsync(1);

        Assert.Equal(cipher, env.Query("SELECT EncryptedContent FROM Messages")[0]);
        Assert.Equal("alice", env.Query("SELECT Sender FROM Messages")[0]);
        Assert.Equal("bob", env.Query("SELECT Receiver FROM Messages")[0]);
    }

    [Fact]
    public async Task History_WorksForBothSides_InOrder_EvenWhenReceiverWasOffline()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2"); // bob đăng ký nhưng chưa đăng nhập
        await alice.LoginAsync("alice", "secret1");

        var c1 = TestClient.FakeCipher();
        var c2 = TestClient.FakeCipher();
        await alice.SendAsync("CHAT", "bob", c1);
        await env.WaitForMessagesAsync(1);

        await bob.LoginAsync("bob", "secret2");
        await bob.SendAsync("CHAT", "alice", c2);
        await env.WaitForMessagesAsync(2);

        foreach (var who in new[] { alice, bob })
        {
            var other = who == alice ? "bob" : "alice";
            var resp = await who.RequestAsync("GET_HISTORY", other);
            var list = JsonSerializer.Deserialize<List<WirePacket>>(resp.Payload, Json)!;
            Assert.Equal(2, list.Count);
            Assert.Equal(c1, list[0].Payload);
            Assert.Equal("alice", list[0].Sender);
            Assert.Equal(c2, list[1].Payload);
            Assert.Equal("bob", list[1].Sender);
            Assert.Equal("CHAT", list[0].Type);
        }
    }

    [Fact]
    public async Task History_IsPerConversation_Empty_And_RequiresLogin()
    {
        await using var env = new TestEnv();
        var alice = await env.ConnectAsync();
        var bob = await env.ConnectAsync();
        var carol = await env.ConnectAsync();
        await alice.RegisterAsync("alice", "secret1");
        await bob.RegisterAsync("bob", "secret2");
        await carol.RegisterAsync("carol", "secret3");

        Assert.StartsWith("FAIL", (await alice.RequestAsync("GET_HISTORY", "bob")).Payload); // chưa đăng nhập

        await alice.LoginAsync("alice", "secret1");
        await alice.SendAsync("CHAT", "bob", TestClient.FakeCipher());
        await env.WaitForMessagesAsync(1);

        var withCarol = await alice.RequestAsync("GET_HISTORY", "carol");
        Assert.Equal(0, JsonSerializer.Deserialize<List<WirePacket>>(withCarol.Payload, Json)!.Count);
    }

    // ============================================================ Độ bền của Server

    [Fact]
    public async Task Server_IgnoresMalformedLines_And_ReportsUnknownTypes_And_OversizedPayload()
    {
        await using var env = new TestEnv();
        var a = await env.ConnectAsync();

        await a.SendLineAsync("{đây không phải JSON");
        await a.SendLineAsync("");
        await a.SendLineAsync("null");

        await a.SendAsync("HACK_THE_PLANET");
        var err = await a.WaitForAsync(p => p.Type == "ERROR");
        Assert.False(string.IsNullOrEmpty(err.Payload));

        var big = new string('x', 250_000);
        Assert.StartsWith("FAIL", (await a.RequestAsync("REGISTER", "SERVER", big)).Payload);

        // kết nối vẫn sống sau tất cả những gói lỗi ở trên
        Assert.Equal("SUCCESS", await a.RegisterAsync("alice", "secret1"));
    }

    [Fact]
    public async Task ManyClients_Concurrently_AllMessagesAreDelivered()
    {
        await using var env = new TestEnv();
        const int clients = 12;
        const int perClient = 5;

        var list = new List<TestClient>();
        for (int i = 0; i < clients; i++)
        {
            var c = await env.ConnectAsync();
            Assert.Equal("SUCCESS", await c.RegisterAsync($"user{i}", "secret1"));
            Assert.Equal("SUCCESS", await c.LoginAsync($"user{i}", "secret1"));
            list.Add(c);
        }

        // mỗi client gửi perClient tin cho client kế tiếp (vòng tròn), tất cả cùng lúc
        await Task.WhenAll(list.Select(async (c, i) =>
        {
            for (int k = 0; k < perClient; k++)
                await c.SendAsync("CHAT", $"user{(i + 1) % clients}", TestClient.FakeCipher());
        }));

        foreach (var c in list)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (c.CountReceived(p => p.Type == "CHAT") < perClient)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"{c.Username} chưa nhận đủ tin.");
                await Task.Delay(20);
            }
            Assert.Equal(perClient, c.CountReceived(p => p.Type == "CHAT"));
        }
        await env.WaitForMessagesAsync(clients * perClient);
        Assert.Equal(clients * perClient, env.Query("SELECT EncryptedContent FROM Messages").Count);
    }
}
