# Giao thức SecureChat (Server của Hải ⇄ Client của Nhân)

**Đường truyền:** TCP, cổng **8888**. Mỗi gói tin = **một dòng JSON** kết thúc bằng `\n` (UTF-8).
Nội dung tin nhắn (Payload của CHAT) luôn là **bản mã** do Client tạo; Server không đọc được.

```json
{ "Type": "...", "Sender": "...", "Receiver": "...", "Payload": "...", "Timestamp": "2026-11-14T10:00:00Z" }
```

## Quy ước phản hồi
- Yêu cầu có phản hồi (`REGISTER`, `LOGIN`, `GET_HISTORY`, `GET_PUBLIC_KEY`): Server trả gói có `Type = "<TYPE>_RESPONSE"`
  (vd `LOGIN` → `LOGIN_RESPONSE`), `Sender = "SERVER"`.
- Thành công: `Payload = "SUCCESS"` (REGISTER, LOGIN) hoặc dữ liệu (GET_HISTORY, GET_PUBLIC_KEY).
  Thất bại: `Payload = "FAIL: lý do"`.
- `CHAT`, `LOGOUT`: không có phản hồi khi thành công. `CHAT` thất bại → Server gửi gói `Type = "ERROR"`, `Payload = lý do`.

## Các loại gói

| Type | Chiều | Dữ liệu |
|---|---|---|
| `REGISTER` | C→S | `Sender` = username. `Payload` = `{"PasswordHash","PublicKey"}`. Username 3-20 ký tự `[A-Za-z0-9_]`, không phân biệt hoa/thường. `PasswordHash` = PBKDF2 do Client tính (`PasswordHasher.Hash`). `PublicKey` = RSA SubjectPublicKeyInfo Base64. → `REGISTER_RESPONSE` |
| `LOGIN` | C→S | `Sender` = username, `Payload` = bản băm mật khẩu. Một tài khoản chỉ online ở một kết nối. → `LOGIN_RESPONSE` |
| `LOGOUT` | C→S | Không cần Payload. Server đánh dấu OFFLINE và báo cho mọi người. Đăng nhập lại trên cùng kết nối cũng tự đăng xuất phiên cũ. |
| `GET_PUBLIC_KEY` | C→S | `Receiver` = tên người cần lấy key. → `GET_PUBLIC_KEY_RESPONSE`, `Payload` = public key Base64 hoặc `FAIL: ...` |
| `CHAT` | C→S | `Receiver` = người nhận, `Payload` = bản mã. Server **ghi đè** `Sender` và `Timestamp`, lưu DB (kể cả khi người nhận offline), rồi chuyển tiếp nếu người nhận đang online. |
| `CHAT` | S→C | Tin nhắn đến: `Sender` = người gửi, `Payload` = bản mã, `Timestamp` = giờ Server. |
| `GET_HISTORY` | C→S | `Receiver` = người đối thoại. → `GET_HISTORY_RESPONSE`, `Payload` = **mảng JSON các gói CHAT** (cũ → mới, tối đa 200) hoặc `FAIL: ...` |
| `STATUS_UPDATE` | S→C | `Sender` = username, `Payload` = `ONLINE` / `OFFLINE`. Gửi khi ai đó đăng nhập, đăng xuất hoặc rớt kết nối. Người vừa đăng nhập nhận danh sách những người đang online. |
| `ERROR` | S→C | Lỗi chung, vd gửi CHAT khi chưa đăng nhập hoặc gửi cho người không tồn tại. |

## Mã hóa (do Client làm)
Gửi tin cho B: lấy public key của B bằng `GET_PUBLIC_KEY`, rồi
`MessageCipher.Encrypt(text, publicKeyB, publicKeyCuaMinh)` và gửi gói `CHAT`.
Mã hóa cho cả hai để người gửi cũng đọc lại được lịch sử.

## Cơ sở dữ liệu (SQLite, file `securechat.db`)
- `Users(Id, Username UNIQUE NOCASE, PasswordHash, PublicKey, Status)`
- `Messages(Id, Sender, Receiver, EncryptedContent, Timestamp)`
