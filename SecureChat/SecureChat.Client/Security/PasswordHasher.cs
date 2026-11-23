using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Client.Security;

/// <summary>
/// Băm mật khẩu ở Client (Server chỉ nhận và lưu bản băm, không bao giờ thấy mật khẩu thô).
/// PBKDF2-SHA256, 100.000 vòng, 32 byte -> Base64 (44 ký tự, Server giới hạn 200).
/// Salt cố định theo username ("SecureChat:" + username thường) để cùng 1 tài khoản luôn ra cùng 1 bản băm
/// ở mọi máy; salt riêng cho từng tài khoản nên 2 người cùng mật khẩu vẫn có bản băm khác nhau.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int HashBytes = 32;

    public static string Hash(string username, string password)
    {
        var salt = Encoding.UTF8.GetBytes("SecureChat:" + username.Trim().ToLowerInvariant());
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return Convert.ToBase64String(hash);
    }
}
