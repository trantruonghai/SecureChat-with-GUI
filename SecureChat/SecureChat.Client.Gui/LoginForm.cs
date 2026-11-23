using System.Net.Sockets;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Services;

namespace SecureChat.Client.Gui;

/// <summary>Màn hình đăng nhập / đăng ký (thay cho AuthMenuAsync của ConsoleUI).</summary>
public sealed class LoginForm : Form
{
    public ClientNetworkHandler Net { get; } = new();
    public ClientState State { get; } = new();
    public AuthService Auth { get; }
    public ChatService Chat { get; }

    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly TextBox _user = new();
    private readonly TextBox _pass = new();
    private readonly Button _loginBtn = new();
    private readonly Button _registerBtn = new();
    private readonly Label _status = new();
    private bool _busy;

    public LoginForm(string host, int port)
    {
        Auth = new AuthService(Net, State);
        Chat = new ChatService(Net, State);

        Text = "SecureChat - Đăng nhập";
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 430);

        _host.Text = host;
        _port.Text = port.ToString();
        _pass.UseSystemPasswordChar = true;

        var title = new Label
        {
            Text = "SecureChat",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 90, 180),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomCenter
        };
        var subtitle = new Label
        {
            Text = "Chat mã hóa đầu cuối (AES-256-GCM + RSA-2048)",
            ForeColor = Color.Gray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0, 8, 0, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        AddRow(grid, 0, "Server", _host);
        AddRow(grid, 1, "Cổng", _port);
        AddRow(grid, 2, "Tên đăng nhập", _user);
        AddRow(grid, 3, "Mật khẩu", _pass);

        _loginBtn.Text = "Đăng nhập";
        _registerBtn.Text = "Đăng ký";
        foreach (var b in new[] { _loginBtn, _registerBtn })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(4);
        }
        _loginBtn.BackColor = Color.FromArgb(0, 120, 215);
        _loginBtn.ForeColor = Color.White;
        _loginBtn.FlatStyle = FlatStyle.Flat;

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.Controls.Add(_loginBtn, 0, 0);
        buttons.Controls.Add(_registerBtn, 1, 0);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 16, 24, 16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(subtitle, 0, 1);
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        root.Controls.Add(_status, 0, 4);
        Controls.Add(root);

        AcceptButton = _loginBtn; // Enter = đăng nhập
        _loginBtn.Click += async (_, _) => await SubmitAsync(register: false);
        _registerBtn.Click += async (_, _) => await SubmitAsync(register: true);

        // Đóng form mà không đăng nhập -> đóng kết nối. (Đăng nhập xong thì Program đóng sau.)
        FormClosed += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) _ = Net.DisposeAsync();
        };
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, TextBox box)
    {
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(box, 1, row);
    }

    private async Task SubmitAsync(bool register)
    {
        if (_busy) return;

        var user = _user.Text.Trim();
        var pass = _pass.Text;
        if (user.Length == 0 || pass.Length == 0) { SetStatus("Không được để trống.", error: true); return; }
        if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        { SetStatus("Cổng không hợp lệ.", error: true); return; }

        SetBusy(true);
        try
        {
            if (!Net.IsConnected)
            {
                SetStatus("Đang kết nối tới server...", error: false);
                await Net.ConnectAsync(_host.Text.Trim(), port);
            }

            var (ok, msg) = register
                ? await Auth.RegisterAsync(user, pass)
                : await Auth.LoginAsync(user, pass);
            SetStatus(msg, error: !ok);

            if (ok && !register)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (SocketException ex)
        {
            SetStatus($"Không kết nối được tới {_host.Text}:{port} - {ex.Message}", error: true);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            SetStatus($"Lỗi: {ex.Message}", error: true);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _loginBtn.Enabled = _registerBtn.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.SeaGreen;
    }
}
