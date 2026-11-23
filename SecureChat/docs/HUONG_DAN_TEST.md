# Hướng dẫn test Server của Hải

## A. Test tự động (2 phút)
```bash
dotnet restore
dotnet build
dotnet test
```
Kết quả đúng: `Passed: 48` (23 test Server + 17 test Client + 8 test end-to-end Client↔Server) (Visual Studio: mở **Test → Test Explorer → Run All**).
Mỗi test chạy một server thật trên cổng ngẫu nhiên với file DB tạm, nên **không cần tắt server đang chạy** và không làm bẩn `securechat.db`.

## B. Test thủ công bằng `SecureChat.TestConsole` (khi chưa có Client của Nhân)
Mở 3 cửa sổ terminal tại thư mục solution.

**Terminal 1: server**
```bash
dotnet run --project SecureChat.Server
```
Phải thấy: `Đang khởi động và lắng nghe kết nối trên cổng 8888...`

**Terminal 2: Alice** và **Terminal 3: Bob** (cùng lệnh):
```bash
dotnet run --project SecureChat.TestConsole
```
Gõ `help` để xem lệnh. Chạy lần lượt kịch bản sau:

| Bước | Làm ở | Gõ | Kết quả đúng |
|---|---|---|---|
| 1 | Bob | `register bob 123456` | `REGISTER_RESPONSE: SUCCESS` |
| 2 | Alice | `register alice 123456` | `SUCCESS` |
| 3 | Alice | `register alice 123456` (lần 2) | `FAIL: Tên đăng nhập đã tồn tại.` |
| 4 | Alice | `login alice saimatkhau` | `FAIL: Sai tên đăng nhập hoặc mật khẩu.` |
| 5 | Bob | `login bob 123456` | `LOGIN_RESPONSE: SUCCESS` |
| 6 | Alice | `login alice 123456` | `SUCCESS`; Alice thấy `bob đã ONLINE`, Bob thấy `alice đã ONLINE` |
| 7 | Alice | `key bob` | `GET_PUBLIC_KEY_RESPONSE: MIIB...` (public key của Bob) |
| 8 | Alice | `chat bob Xin chào Bob` | Bob **tức thì** thấy `[giờ] alice: Xin chào Bob` |
| 9 | Alice | `chat nobody hello` | `ERROR: Người nhận 'nobody' không tồn tại.` |
| 10 | Bob | `history alice` | Lịch sử có 1 tin của Alice |
| 11 | Bob | đóng cửa sổ (hoặc Ctrl+C) | Alice thấy `bob đã OFFLINE` |
| 12 | Alice | `chat bob Tin nhắn lúc bob offline` | Không lỗi (tin được lưu) |
| 13 | Bob | mở lại, `login bob 123456`, `history alice` | Thấy cả 2 tin, kể cả tin gửi lúc offline |
| 14 | Alice | `raw {hỏng` rồi `chat bob vẫn sống` | Server không sập, tin vẫn gửi được; server in `[WARN] Gói tin sai định dạng` |

Terminal server sẽ in log `[AUTH]`, `[CHAT]`, `[DISCONNECT]` cho từng sự kiện, chụp lại làm bằng chứng cho báo cáo.

## C. Kiểm tra database
Tắt server (Ctrl+C) hoặc để chạy cũng được, rồi:
```bash
sqlite3 securechat.db "SELECT Username, Status, substr(PasswordHash,1,16) FROM Users;"
sqlite3 securechat.db "SELECT Sender, Receiver, EncryptedContent FROM Messages;"
```
- `PasswordHash` là chuỗi Base64 vô nghĩa, không phải `123456`.
- `Status` của mọi người là `Offline` sau khi tắt hết kết nối.
- `EncryptedContent` của TestConsole là Base64 của chữ (mã hóa **giả** để thử đường đi nên giải Base64 ra vẫn đọc được). Khi dùng Client của Nhân với AES/RSA thật, cột này phải là dữ liệu không đọc được, đó là bước kiểm tra bảo mật ở Ngày 5.

## D. Test thủ công bằng Client thật (`SecureChat.Client`)
Mở 3 terminal tại thư mục solution: server, rồi hai client (Alice, Bob).
```bash
dotnet run --project SecureChat.Server
dotnet run --project SecureChat.Client            # chạy 2 lần ở 2 cửa sổ; mặc định 127.0.0.1:8888
```
| Bước | Làm ở | Thao tác | Kết quả đúng |
|---|---|---|---|
| 1 | Alice, Bob | Menu `2` (Đăng ký) rồi menu `1` (Đăng nhập) | `Đăng ký thành công`, `Đăng nhập thành công`; người đăng nhập trước thấy `... đã online` |
| 2 | Alice | `/to bob` | Hiện lịch sử với bob (rỗng nếu chưa chat) |
| 3 | Alice | gõ `Xin chào Bob` | Bob thấy ngay `[giờ] alice: Xin chào Bob` |
| 4 | Bob | `/to alice` rồi gõ trả lời | Alice thấy tin trả lời |
| 5 | Bob | đóng cửa sổ đột ngột | Alice thấy `bob đã offline` |
| 6 | Alice | gửi tiếp cho bob | Không lỗi (server lưu tin) |
| 7 | Bob | mở lại, đăng nhập, `/to alice` | Thấy đủ lịch sử, gồm tin gửi lúc offline |
| 8 | Alice | `/to khong_ton_tai` rồi gõ chữ | `Không gửi được: ...` |

Sau đó kiểm tra DB như mục C: `EncryptedContent` lúc này là bản mã thật (AES + RSA), không giải Base64 ra chữ được.

**Lưu ý:** tài khoản tạo bằng `SecureChat.TestConsole` (băm SHA-256 đơn giản, mã hóa giả) **không** đăng nhập được bằng `SecureChat.Client` (băm PBKDF2, mã hóa thật). Mỗi công cụ dùng tài khoản riêng. Khóa riêng RSA của Client lưu ở `%AppData%/SecureChat/<tên>.key`, nên phải đăng ký và đăng nhập trên cùng một máy.

## E. Lỗi thường gặp
| Triệu chứng | Cách xử lý |
|---|---|
| `Unable to find package ...` / NuGet lỗi khi restore | Cần internet; kiểm tra proxy/VPN, chạy lại `dotnet restore`. |
| `The framework 'Microsoft.NETCore.App', version '8.0.0' not found` | Cài .NET 8 SDK/Runtime (kiểm tra bằng `dotnet --list-runtimes`). |
| `Address already in use` / `Only one usage of each socket address` | Cổng 8888 đang bị chiếm (server cũ chưa tắt). Tắt nó hoặc chạy `dotnet run --project SecureChat.Server -- 9999`. |
| TestConsole: `Không kết nối được tới 127.0.0.1:8888` | Server chưa chạy hoặc khác cổng; chạy TestConsole với `-- 127.0.0.1 9999` nếu đổi cổng. |
| `no such column` / báo database cũ có dữ liệu | Xóa file `securechat.db` rồi chạy lại. |
| Test báo lỗi xóa file `.db` (Windows) | Chạy lại `dotnet test`; nếu còn, đóng các chương trình đang mở file `.db` (DB Browser...). |
