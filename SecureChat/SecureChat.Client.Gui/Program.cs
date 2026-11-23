namespace SecureChat.Client.Gui;

internal static class Program
{
    // Dùng: dotnet run --project SecureChat.Client.Gui -- [host] [port]   (mặc định 127.0.0.1:8888)
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8888;

        // Đăng xuất -> quay lại màn hình đăng nhập (kết nối mới); đóng cửa sổ -> thoát.
        while (true)
        {
            using var login = new LoginForm(host, port);
            if (login.ShowDialog() != DialogResult.OK) return;

            using var main = new MainForm(login.Net, login.Chat, login.State);
            Application.Run(main);

            login.Net.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!main.LoggedOut) return;
        }
    }
}
