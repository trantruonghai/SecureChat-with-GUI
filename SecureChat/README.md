# SecureChat

Ứng dụng chat một-một thời gian thực qua TCP, tin nhắn **mã hóa đầu cuối** (AES-256-GCM + RSA-2048). Server chỉ lưu và chuyển tiếp bản mã nên không đọc được nội dung. C# / .NET 8, lưu trữ SQLite.

| Thành viên | Phần việc |
|---|---|
| **Hải** | Server đa luồng, SQLite, đăng ký/đăng nhập, định tuyến tin nhắn, lịch sử, trạng thái online/offline |
| **Nhân** | Client console, tầng mạng Client, mã hóa AES/RSA, băm mật khẩu, hiển thị lịch sử và trạng thái |

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `SecureChat.Server/` | Server: `ServerCore` (TcpListener, mỗi client một luồng, định tuyến), `DatabaseManager` (SQLite), `Packet` |
| `SecureChat.Client/` | Client console: `Network/` (TcpClient), `Services/` (đăng nhập, chat, lịch sử, trạng thái), `Security/` (AES, RSA, mã hóa lai, băm mật khẩu, lưu khóa), `UI/` (menu và lệnh) |
| `SecureChat.Server.Tests/` | 23 test Server bằng client TCP thuần |
| `SecureChat.Client.Tests/` | 17 test mã hóa, băm mật khẩu, gói tin |
| `SecureChat.E2E.Tests/` | 8 test end-to-end: Client thật + Server thật chạy trong tiến trình |
| `SecureChat.TestConsole/` | Công cụ gõ lệnh để thử Server bằng tay (mã hóa giả, không dùng chung tài khoản với Client) |
| `docs/` | `PROTOCOL.md` (giao thức), `HUONG_DAN_TEST.md` (test tự động và thủ công), `NHIEM_VU_HAI.md`, `DES_OF_SECURECHAT.docx` (kế hoạch 5 ngày) |

## Chạy nhanh
Cần .NET 8 SDK (`dotnet --version`).

```bash
dotnet restore
dotnet build
dotnet test                                   # 48 test: Server + Client + end-to-end

# Terminal 1: server (cổng 8888, DB ./securechat.db)
dotnet run --project SecureChat.Server

# Terminal 2 và 3: hai client
dotnet run --project SecureChat.Client                  # mặc định 127.0.0.1 8888
dotnet run --project SecureChat.Client -- 192.168.1.10 8888   # kết nối server qua LAN
```

Lệnh trong Client: `/to <tên>` (chọn người chat và xem lịch sử), gõ chữ để gửi, `/history [tên]`, `/online`, `/logout`, `/quit`, `/help`.

Kịch bản kiểm thử từng bước (và checklist Ngày 5) nằm trong [docs/HUONG_DAN_TEST.md](docs/HUONG_DAN_TEST.md).

## Cách mã hóa hoạt động
1. Lúc đăng ký, Client sinh cặp khóa RSA, gửi **public key** lên Server, giữ **private key** ở `%AppData%/SecureChat/<tên>.key`.
2. Gửi tin cho B: Client lấy public key của B (`GET_PUBLIC_KEY`), tạo khóa AES ngẫu nhiên, mã hóa tin bằng AES-GCM, rồi bọc khóa AES bằng RSA cho **cả B và chính mình** (để người gửi đọc lại được lịch sử).
3. Server lưu và chuyển tiếp nguyên bản mã. Client B giải mã bằng private key của mình.
4. Mật khẩu được băm PBKDF2 ở Client; Server chỉ thấy và lưu bản băm.

Giao thức chi tiết: [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Xem dữ liệu trong DB
```bash
sqlite3 securechat.db "SELECT Username, Status, substr(PasswordHash,1,20) FROM Users;"
sqlite3 securechat.db "SELECT Sender, Receiver, substr(EncryptedContent,1,40), Timestamp FROM Messages;"
```
`EncryptedContent` phải là Base64 vô nghĩa (yêu cầu kiểm tra Ngày 5).

## Chạy qua mạng LAN
1. Trên máy server: `ipconfig` rồi lấy địa chỉ IPv4 (vd `192.168.1.10`).
2. Mở cổng 8888 trên firewall (PowerShell quyền Admin):
   `netsh advfirewall firewall add rule name="SecureChat" dir=in action=allow protocol=TCP localport=8888`
3. Trên máy client: `dotnet run --project SecureChat.Client -- 192.168.1.10 8888`.

## Lưu ý
- Đăng ký và đăng nhập một tài khoản phải trên **cùng một máy** (private key nằm trên máy đó). Đăng nhập từ máy khác sẽ không giải mã được lịch sử.
- Xóa `securechat.db` để reset dữ liệu server.

## Giao diện đồ họa (WinForms)
`SecureChat.Client.Gui/` là giao diện cửa sổ thay cho `ConsoleUI`, dùng lại nguyên `Network/`, `Services/`, `Security/` của `SecureChat.Client` (không sửa Server hay mã hóa). Chỉ chạy trên Windows.

```bash
dotnet sln add SecureChat.Client.Gui/SecureChat.Client.Gui.csproj   # một lần, để project nằm trong .sln
dotnet run --project SecureChat.Client.Gui                           # mặc định 127.0.0.1 8888
dotnet run --project SecureChat.Client.Gui -- 192.168.1.10 8888      # qua LAN
```
- Màn hình đăng nhập/đăng ký (có ô Server và Cổng); danh sách người dùng có chấm online/offline và số tin chưa đọc.
- Chọn một người để tải lịch sử; Enter để gửi, Shift+Enter xuống dòng; "Thêm" để chat với người chưa hiện trong danh sách (Server chỉ báo những người đang online).
