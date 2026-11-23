# Đối chiếu nhiệm vụ của Hải (Backend & Network Engineer)

## Lộ trình trong kế hoạch ↔ code ↔ test

| Ngày | Nhiệm vụ trong kế hoạch | Làm ở đâu | Test kiểm chứng |
|---|---|---|---|
| 1 | Dự án Server (.NET Console) | `SecureChat.Server.csproj`, `Program.cs` | — |
| 1 | Khung TcpListener, nhiều client đồng thời | `ServerCore.Listen/AcceptLoop/HandleClient` (mỗi client một `Thread`) | `ManyClients_Concurrently_AllMessagesAreDelivered` |
| 1 | Thống nhất cấu trúc DB và gói tin | `DatabaseManager.InitializeDatabase`, `Packet.cs`, `docs/PROTOCOL.md` | — |
| 2 | Kết nối SQLite | `DatabaseManager` (bảng `Users`, `Messages`) | `DatabaseManagerTests.*` |
| 2 | Đăng ký | `ServerCore.HandleRegister` → `DatabaseManager.RegisterUser` | `Register_*` |
| 2 | Đăng nhập | `ServerCore.HandleLogin` → `DatabaseManager.ValidateUser` | `Login_*` |
| 2 | Danh sách client online `Dictionary<string, …>` | `ServerCore.onlineClients` (có `lock`) | `Login_WhileAlreadyOnlineElsewhere_IsRejected` |
| 3 | Phản hồi đăng nhập thành công | `Respond(...)` → `LOGIN_RESPONSE: SUCCESS` | `Register_Then_Login_Succeeds` |
| 3 | Đọc gói tin, định tuyến, chuyển tiếp đến Client B | `ServerCore.HandleChat` | `Chat_IsForwardedToOnlineReceiver_Unchanged`, `Chat_SenderCannotBeSpoofed` |
| 3 | Lấy public key để Client mã hóa tin cho người nhận | `HandleGetPublicKey` | `GetPublicKey_ReturnsRegisteredKey_Or_Fail` |
| 4 | Lưu tin nhắn vào DB khi nhận gói CHAT hợp lệ | `HandleChat` → `DatabaseManager.SaveMessage` | `Chat_IsStoredInDatabase_ExactlyAsReceived` |
| 4 | Truy xuất lịch sử trả về Client | `HandleGetHistory` → `DatabaseManager.GetHistory` | `History_*` |
| 4 | Trạng thái Online/Offline (Server báo, cập nhật cột `Status`) | `Logout`, `Broadcast`, `DatabaseManager.SetStatus` | `StatusUpdates_AreSent_OnLoginAndOnDisconnect`, `Logout_*` |
| 5 | Nhiều Client cùng lúc, qua LAN | `IPAddress.Any`; hướng dẫn trong `README.md` | `ManyClients_*` |
| 5 | DB lưu tin nhắn ở dạng mã hóa | Server chỉ lưu/chuyển nguyên `Payload` | `Chat_IsStoredInDatabase_ExactlyAsReceived` + kiểm tra thủ công (checklist dưới) |

## Những điểm đã làm thêm so với yêu cầu tối thiểu
- Mật khẩu: Server chỉ lưu **bản băm** do Client gửi (cột `PasswordHash`), không bao giờ thấy mật khẩu thô.
- Chống giả mạo: Server ghi đè `Sender` và `Timestamp` của gói CHAT bằng danh tính phiên đăng nhập và giờ Server.
- Username không phân biệt hoa/thường (`Alice` = `alice`).
- Một tài khoản chỉ online ở một kết nối; gói quá lớn (> 200.000 ký tự), JSON hỏng, loại gói lạ đều bị xử lý an toàn.
- Người nhận rớt mạng giữa chừng không làm sập kết nối của người gửi; lỗi dọn dẹp không làm sập server.
- Mỗi kết nối có khóa ghi riêng vì nhiều luồng có thể cùng gửi vào một client.
- File `securechat.db` cũ (chưa có cột `PublicKey`) và còn trống sẽ tự được nâng cấp.

## Điều Client (Nhân) cần khớp với Server
Chi tiết ở `docs/PROTOCOL.md`. Tóm tắt:
1. Cổng **8888**.
2. `REGISTER`: `Sender` = username, `Payload` = `{"PasswordHash","PublicKey"}`. `LOGIN`: `Payload` = bản băm mật khẩu.
3. Phản hồi có `Type = "<TYPE>_RESPONSE"` (`LOGIN_RESPONSE`...), `Payload` bắt đầu bằng `SUCCESS` hoặc `FAIL: lý do`.
4. Dùng thêm `GET_PUBLIC_KEY` (trước khi mã hóa tin), `GET_HISTORY`, `LOGOUT`; nhận `STATUS_UPDATE`, `CHAT`, `ERROR`.

## Checklist kiểm thử Ngày 5 (làm cùng Nhân khi đã có Client)
- [ ] Mở 3-5 Client cùng lúc trên một máy: đăng ký, đăng nhập, chat chéo giữa các cặp.
- [ ] Thử qua LAN bằng 2 máy (xem `README.md`), chat hai chiều, nhận tin gần như tức thì.
- [ ] Tắt đột ngột một Client (đóng cửa sổ): các Client khác thấy người đó **offline**.
- [ ] Gửi tin cho người đang offline, người đó đăng nhập và mở `/history` thì thấy tin.
- [ ] Mở `securechat.db`: `EncryptedContent` là Base64 vô nghĩa; `PasswordHash` không phải mật khẩu.
- [ ] Chụp màn hình các bước trên để đưa vào báo cáo và slide demo.
