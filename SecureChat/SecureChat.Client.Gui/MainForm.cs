using System.Text.RegularExpressions;
using SecureChat.Client.Models;
using SecureChat.Client.Network;
using SecureChat.Client.Services;

namespace SecureChat.Client.Gui;

/// <summary>
/// Cửa sổ chat chính (thay cho ChatLoopAsync của ConsoleUI):
/// danh sách người dùng + trạng thái online bên trái, khung tin nhắn bên phải.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Regex NameRegex = new("^[A-Za-z0-9_]{3,20}$");
    private const string DecryptFailed = "[không giải mã được]";

    private sealed class Contact
    {
        public string Name { get; init; } = "";
        public bool Online { get; set; }
        public int Unread { get; set; }
    }

    private sealed record Msg(string Sender, string Text, DateTime Time, bool Mine, bool Ok);

    private readonly ClientNetworkHandler _net;
    private readonly ChatService _chat;
    private readonly ClientState _state;

    private readonly List<Contact> _contacts = new();
    private readonly Dictionary<string, List<Msg>> _convos = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly ListBox _list = new();
    private readonly TextBox _addBox = new();
    private readonly Button _addBtn = new();
    private readonly Button _logoutBtn = new();
    private readonly Label _welcome = new();
    private readonly Label _header = new();
    private readonly RichTextBox _log = new();
    private readonly TextBox _input = new();
    private readonly Button _send = new();
    private readonly Label _status = new();

    private readonly Font _nameFont;
    private readonly Font _bodyFont;
    private readonly Font _metaFont;

    private bool _rebuilding;
    private bool _logoutSent;
    private bool _disconnectHandled;

    /// <summary>true nếu người dùng bấm Đăng xuất (hoặc mất kết nối) -> Program quay lại màn hình đăng nhập.</summary>
    public bool LoggedOut { get; private set; }

    public MainForm(ClientNetworkHandler net, ChatService chat, ClientState state)
    {
        _net = net;
        _chat = chat;
        _state = state;

        Text = $"SecureChat - {state.Username}";
        Font = new Font("Segoe UI", 10f);
        _nameFont = new Font(Font, FontStyle.Bold);
        _bodyFont = Font;
        _metaFont = new Font("Segoe UI", 8.5f);
        MinimumSize = new Size(780, 480);
        Size = new Size(980, 660);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();

        // Server gửi danh sách người đang online ngay sau khi đăng nhập -> ChatService đã lưu vào state.Online
        foreach (var kv in _state.Online)
            if (!IsMe(kv.Key)) GetOrAddContact(kv.Key, kv.Value).Online = kv.Value;
        RefreshContacts();
        UpdateHeader();
        SetInputEnabled(false);
        SetStatus("Đã đăng nhập. Chọn người ở danh sách bên trái hoặc nhập tên để thêm.", error: false);

        _chat.MessageReceived += OnMessage;
        _chat.StatusChanged += OnStatus;
        _chat.ErrorReceived += OnServerError;
        _net.Disconnected += OnDisconnected;

        FormClosing += (_, _) => LogoutSync();
        FormClosed += (_, _) =>
        {
            _chat.MessageReceived -= OnMessage;
            _chat.StatusChanged -= OnStatus;
            _chat.ErrorReceived -= OnServerError;
            _net.Disconnected -= OnDisconnected;
            _nameFont.Dispose();
            _metaFont.Dispose();
        };
    }

    // ================= Giao diện =================

    private void BuildLayout()
    {
        // ---- cột trái: tài khoản + danh sách + thêm người ----
        _welcome.Text = $"Xin chào, {_state.Username}";
        _welcome.Font = _nameFont;
        _welcome.Dock = DockStyle.Fill;
        _welcome.TextAlign = ContentAlignment.MiddleLeft;

        _logoutBtn.Text = "Đăng xuất";
        _logoutBtn.Dock = DockStyle.Fill;

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        top.Controls.Add(_welcome, 0, 0);
        top.Controls.Add(_logoutBtn, 1, 0);

        _list.Dock = DockStyle.Fill;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 34;
        _list.IntegralHeight = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.DrawItem += DrawContact;
        _list.SelectedIndexChanged += OnContactSelected;

        _addBox.PlaceholderText = "Thêm người dùng...";
        _addBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _addBtn.Text = "Thêm";
        _addBtn.Dock = DockStyle.Fill;

        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        addRow.Controls.Add(_addBox, 0, 0);
        addRow.Controls.Add(_addBtn, 1, 0);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 8, 4, 4)
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        left.Controls.Add(top, 0, 0);
        left.Controls.Add(_list, 0, 1);
        left.Controls.Add(addRow, 0, 2);

        // ---- cột phải: tiêu đề + khung tin nhắn + ô nhập ----
        _header.Dock = DockStyle.Fill;
        _header.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _header.TextAlign = ContentAlignment.MiddleLeft;

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = Color.White;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.DetectUrls = false;

        _input.Multiline = true;
        _input.Dock = DockStyle.Fill;
        _input.PlaceholderText = "Nhập tin nhắn (Enter để gửi, Shift+Enter xuống dòng)";
        _send.Text = "Gửi";
        _send.Dock = DockStyle.Fill;
        _send.BackColor = Color.FromArgb(0, 120, 215);
        _send.ForeColor = Color.White;
        _send.FlatStyle = FlatStyle.Flat;

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        inputRow.Controls.Add(_input, 0, 0);
        inputRow.Controls.Add(_send, 1, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4, 8, 8, 4)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        right.Controls.Add(_header, 0, 0);
        right.Controls.Add(_log, 0, 1);
        right.Controls.Add(inputRow, 0, 2);

        // ---- khung ngoài + thanh trạng thái ----
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(10, 0, 0, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(_status, 0, 1);
        root.SetColumnSpan(_status, 2);
        Controls.Add(root);

        // ---- sự kiện ----
        _addBtn.Click += (_, _) => AddContact();
        _addBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddContact(); }
        };
        _send.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };
        _logoutBtn.Click += (_, _) =>
        {
            LoggedOut = true;
            Close(); // FormClosing sẽ gửi LOGOUT
        };
    }

    /// <summary>Vẽ một dòng danh sách: chấm trạng thái + tên + số tin chưa đọc.</summary>
    private void DrawContact(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var c = (Contact)_list.Items[e.Index];
        e.DrawBackground();

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;

        using (var dot = new SolidBrush(c.Online ? Color.LimeGreen : Color.Gray))
        {
            e.Graphics.FillEllipse(dot, e.Bounds.Left + 8, e.Bounds.Top + (e.Bounds.Height - 10) / 2, 10, 10);
        }

        var nameRect = new Rectangle(e.Bounds.Left + 26, e.Bounds.Top, e.Bounds.Width - 70, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, c.Name, c.Unread > 0 ? _nameFont : _bodyFont, nameRect, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (c.Unread > 0)
        {
            var badge = new Rectangle(e.Bounds.Right - 34, e.Bounds.Top + (e.Bounds.Height - 20) / 2, 26, 20);
            using (var red = new SolidBrush(Color.Crimson)) e.Graphics.FillEllipse(red, badge);
            TextRenderer.DrawText(e.Graphics, c.Unread > 99 ? "99+" : c.Unread.ToString(), _metaFont, badge,
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        e.DrawFocusRectangle();
    }

    // ================= Danh sách người dùng =================

    private bool IsMe(string name) => name.Equals(_state.Username, StringComparison.OrdinalIgnoreCase);

    private Contact GetOrAddContact(string name, bool online)
    {
        var c = _contacts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (c != null) return c;
        c = new Contact { Name = name, Online = online };
        _contacts.Add(c);
        return c;
    }

    /// <summary>Dựng lại danh sách (online lên trước, rồi theo tên), giữ nguyên người đang chọn.</summary>
    private void RefreshContacts()
    {
        var selected = _state.CurrentPartner;
        _rebuilding = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in _contacts
                         .OrderByDescending(x => x.Online)
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _list.Items.Add(c);

            if (selected != null)
                for (int i = 0; i < _list.Items.Count; i++)
                    if (((Contact)_list.Items[i]).Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    {
                        _list.SelectedIndex = i;
                        break;
                    }
        }
        finally
        {
            _list.EndUpdate();
            _rebuilding = false;
        }
    }

    private void AddContact()
    {
        var name = _addBox.Text.Trim();
        if (!NameRegex.IsMatch(name))
        {
            SetStatus("Tên phải 3-20 ký tự gồm chữ, số hoặc dấu gạch dưới.", error: true);
            return;
        }
        if (IsMe(name))
        {
            SetStatus("Đó là tài khoản của bạn.", error: true);
            return;
        }

        var c = GetOrAddContact(name, _state.Online.TryGetValue(name, out var on) && on);
        _addBox.Clear();
        RefreshContacts();
        for (int i = 0; i < _list.Items.Count; i++)
            if (ReferenceEquals(_list.Items[i], c)) { _list.SelectedIndex = i; break; }
    }

    private async void OnContactSelected(object? sender, EventArgs e)
    {
        if (_rebuilding || _list.SelectedItem is not Contact c) return;
        await OpenConversationAsync(c);
    }

    // ================= Mở cuộc trò chuyện + lịch sử =================

    private async Task OpenConversationAsync(Contact c)
    {
        _state.CurrentPartner = c.Name;
        c.Unread = 0;
        _list.Invalidate();
        UpdateHeader();
        _log.Clear();
        SetInputEnabled(false);

        if (!_loaded.Contains(c.Name))
        {
            SetStatus($"Đang tải lịch sử với {c.Name}...", error: false);
            _list.Enabled = false; // tránh hai GET_HISTORY chồng nhau (RequestAsync chỉ cho 1 yêu cầu cùng loại)
            try
            {
                var hist = (await _chat.GetHistoryAsync(c.Name))
                    .Select(h => new Msg(h.Sender, h.Text, h.Timestamp, IsMe(h.Sender), h.Decrypted))
                    .ToList();

                // Gộp các tin nhận trực tiếp trước/trong lúc tải (nếu chưa có trong lịch sử)
                if (_convos.TryGetValue(c.Name, out var live))
                    foreach (var l in live)
                        if (!hist.Any(h => h.Text == l.Text
                                           && h.Sender.Equals(l.Sender, StringComparison.OrdinalIgnoreCase)
                                           && Math.Abs((h.Time - l.Time).TotalSeconds) < 2))
                            hist.Add(l);

                _convos[c.Name] = hist.OrderBy(x => x.Time).ToList();
                _loaded.Add(c.Name);
                SetStatus($"Lịch sử với {c.Name}: {hist.Count} tin.", error: false);
            }
            catch (Exception ex)
            {
                SetStatus($"Không tải được lịch sử: {ex.Message}", error: true);
            }
            finally
            {
                _list.Enabled = true;
            }
        }

        if (!string.Equals(_state.CurrentPartner, c.Name, StringComparison.OrdinalIgnoreCase)) return;
        Render(c.Name);
        SetInputEnabled(true);
        _input.Focus();
    }

    private void Render(string partner)
    {
        _log.Clear();
        if (!_convos.TryGetValue(partner, out var list)) return;
        foreach (var m in list) AppendMsg(m);
    }

    private void AppendMsg(Msg m)
    {
        var align = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var who = m.Mine ? "Bạn" : m.Sender;
        var color = !m.Ok ? Color.Firebrick
            : m.Mine ? Color.FromArgb(0, 90, 180)
            : Color.FromArgb(30, 30, 30);

        AppendRun($"{who}  {m.Time.ToLocalTime():dd/MM HH:mm}\n", _metaFont, Color.Gray, align);
        AppendRun(m.Text + "\n\n", _bodyFont, color, align);

        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void AppendRun(string text, Font font, Color color, HorizontalAlignment align)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionAlignment = align;
        _log.SelectionFont = font;
        _log.SelectionColor = color;
        _log.SelectedText = text;
    }

    private void AddToConversation(string partner, Msg m)
    {
        if (!_convos.TryGetValue(partner, out var list))
            _convos[partner] = list = new List<Msg>();
        list.Add(m);
    }

    // ================= Gửi tin =================

    private async Task SendAsync()
    {
        var to = _state.CurrentPartner;
        var text = _input.Text.Trim();
        if (to == null) { SetStatus("Chưa chọn người nhận.", error: true); return; }
        if (text.Length == 0) return;

        _send.Enabled = false;
        try
        {
            await _chat.SendChatAsync(to, text);
            _input.Clear();

            var m = new Msg(_state.Username!, text, DateTime.UtcNow, Mine: true, Ok: true);
            AddToConversation(to, m);
            if (string.Equals(_state.CurrentPartner, to, StringComparison.OrdinalIgnoreCase)) AppendMsg(m);
            SetStatus("Đã gửi (mã hóa đầu cuối).", error: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Không gửi được: {ex.Message}", error: true);
        }
        finally
        {
            _send.Enabled = true;
            _input.Focus();
        }
    }

    // ================= Sự kiện từ ChatService (chạy ở luồng nền -> chuyển sang luồng UI) =================

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* form đang đóng */ }
    }

    private void OnMessage(string sender, string text, DateTime time) => OnUi(() =>
    {
        var c = GetOrAddContact(sender, _state.Online.TryGetValue(sender, out var on) && on);
        var m = new Msg(sender, text, time, Mine: false, Ok: text != DecryptFailed);
        AddToConversation(c.Name, m);

        bool isCurrent = string.Equals(_state.CurrentPartner, c.Name, StringComparison.OrdinalIgnoreCase);
        if (isCurrent) AppendMsg(m); else c.Unread++;
        if (!isCurrent || !ContainsFocus) System.Media.SystemSounds.Asterisk.Play();

        RefreshContacts();
    });

    private void OnStatus(string user, bool online) => OnUi(() =>
    {
        if (IsMe(user)) return;
        GetOrAddContact(user, online).Online = online;
        RefreshContacts();
        UpdateHeader();
    });

    private void OnServerError(string message) => OnUi(() => SetStatus($"Lỗi từ server: {message}", error: true));

    private void OnDisconnected() => OnUi(() =>
    {
        if (_disconnectHandled || LoggedOut) return;
        _disconnectHandled = true;
        MessageBox.Show(this, "Mất kết nối tới server. Bạn sẽ quay lại màn hình đăng nhập.",
            "SecureChat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        LoggedOut = true;
        Close();
    });

    // ================= Tiện ích =================

    private void UpdateHeader()
    {
        var partner = _state.CurrentPartner;
        if (partner == null)
        {
            _header.Text = "Chọn một người để bắt đầu trò chuyện";
            _header.ForeColor = Color.Gray;
            return;
        }
        bool online = _contacts.FirstOrDefault(c => c.Name.Equals(partner, StringComparison.OrdinalIgnoreCase))?.Online == true;
        _header.Text = $"{partner}  -  {(online ? "đang online" : "offline")}";
        _header.ForeColor = online ? Color.SeaGreen : Color.Gray;
    }

    private void SetInputEnabled(bool enabled)
    {
        _input.Enabled = enabled;
        _send.Enabled = enabled;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    /// <summary>Gửi LOGOUT trên luồng nền (tránh deadlock với UI thread), chờ tối đa 1,5 giây.</summary>
    private void LogoutSync()
    {
        if (_logoutSent) return;
        _logoutSent = true;
        try { Task.Run(() => _chat.LogoutAsync()).Wait(1500); }
        catch { /* mất kết nối thì Server tự đánh dấu offline */ }
    }
}
