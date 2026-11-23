using Xunit;

namespace SecureChat.E2E.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task Register_Login_Succeeds_WrongPasswordAndDuplicateFail()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();

        Assert.True((await a.Auth.RegisterAsync(a.Username, "pw-dung")).Ok);
        Assert.False((await a.Auth.RegisterAsync(a.Username, "pw-dung")).Ok);       // trùng tên
        Assert.False((await a.Auth.LoginAsync(a.Username, "pw-sai")).Ok);           // sai mật khẩu
        Assert.True((await a.Auth.LoginAsync(a.Username, "pw-dung")).Ok);
        Assert.Equal(a.Username, a.State.Username);
    }

    [Fact]
    public async Task Register_InvalidUsername_IsRejectedByClient()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();

        var (ok, _) = await a.Auth.RegisterAsync("a b!", "pw");
        Assert.False(ok);
    }

    [Fact]
    public async Task Chat_AtoB_IsDeliveredAndDecrypted_AndDbHoldsOnlyCiphertext()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();
        var b = await env.NewClientAsync();
        await a.RegisterAndLoginAsync();
        await b.RegisterAndLoginAsync();

        const string text = "Xin chào B, tin bí mật 🔒";
        await a.Chat.SendChatAsync(b.Username, text);

        await E2EClient.WaitUntilAsync(() => !b.Messages.IsEmpty);
        Assert.True(b.Messages.TryPeek(out var got));
        Assert.Equal(a.Username, got.Sender);
        Assert.Equal(text, got.Text);

        // Ngày 5: nội dung lưu trong DB phải là bản mã, không chứa chữ gốc
        var stored = env.Query("SELECT EncryptedContent FROM Messages");
        Assert.Single(stored);
        Assert.DoesNotContain("bí mật", stored[0]);
        Assert.DoesNotContain("Xin chào", stored[0]);
    }

    [Fact]
    public async Task History_OfflineMessage_BothSidesCanReadIt()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();
        var b = await env.NewClientAsync();
        await a.RegisterAndLoginAsync();

        // B đăng ký rồi chưa đăng nhập -> offline
        Assert.True((await b.Auth.RegisterAsync(b.Username, "pw-b")).Ok);

        await a.Chat.SendChatAsync(b.Username, "tin gửi lúc B offline");
        await E2EClient.WaitUntilAsync(() => env.Query("SELECT EncryptedContent FROM Messages").Count == 1);

        Assert.True((await b.Auth.LoginAsync(b.Username, "pw-b")).Ok);

        var historyB = await b.Chat.GetHistoryAsync(a.Username);
        Assert.Single(historyB);
        Assert.True(historyB[0].Decrypted);
        Assert.Equal("tin gửi lúc B offline", historyB[0].Text);

        // người gửi cũng đọc lại được (đã mã hóa cho cả hai khóa)
        var historyA = await a.Chat.GetHistoryAsync(b.Username);
        Assert.Single(historyA);
        Assert.Equal("tin gửi lúc B offline", historyA[0].Text);
    }

    [Fact]
    public async Task Status_OnlineThenOffline_AfterAbruptDisconnect()
    {
        await using var env = new E2EEnv();
        var b = await env.NewClientAsync();
        var a = await env.NewClientAsync();
        await b.RegisterAndLoginAsync();
        await a.RegisterAndLoginAsync();

        await E2EClient.WaitUntilAsync(() => b.Statuses.Contains((a.Username, true)));
        Assert.True(b.State.Online[a.Username]);

        await a.Net.DisposeAsync(); // đóng socket đột ngột, không gửi LOGOUT

        await E2EClient.WaitUntilAsync(() => b.Statuses.Contains((a.Username, false)));
        Assert.False(b.State.Online[a.Username]);
    }

    [Fact]
    public async Task Status_Logout_NotifiesOthers()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();
        var b = await env.NewClientAsync();
        await a.RegisterAndLoginAsync();
        await b.RegisterAndLoginAsync();

        await a.Chat.LogoutAsync();
        await E2EClient.WaitUntilAsync(() => b.Statuses.Contains((a.Username, false)));
    }

    [Fact]
    public async Task Send_ToUnknownUser_ThrowsInsteadOfSending()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();
        await a.RegisterAndLoginAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => a.Chat.SendChatAsync("khong_ton_tai_xyz", "hi"));
        Assert.Empty(env.Query("SELECT EncryptedContent FROM Messages"));
    }

    [Fact]
    public async Task Chat_ManyMessages_ArriveInOrder()
    {
        await using var env = new E2EEnv();
        var a = await env.NewClientAsync();
        var b = await env.NewClientAsync();
        await a.RegisterAndLoginAsync();
        await b.RegisterAndLoginAsync();

        for (int i = 0; i < 20; i++)
            await a.Chat.SendChatAsync(b.Username, $"msg-{i}");

        await E2EClient.WaitUntilAsync(() => b.Messages.Count == 20);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"msg-{i}"), b.Messages.Select(m => m.Text));
    }
}
