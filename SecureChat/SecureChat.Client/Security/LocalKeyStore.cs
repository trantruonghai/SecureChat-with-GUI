using System.Text.Json;

namespace SecureChat.Client.Security;

/// <summary>Lưu khóa riêng RSA trên máy để đăng nhập lại / xem lịch sử sau khi tắt app.</summary>
public static class LocalKeyStore
{
    /// <summary>Đổi thư mục lưu khóa (dùng trong test để không ghi vào AppData thật).</summary>
    public static string? DirectoryOverride { get; set; }

    private static string PathFor(string username)
    {
        var dir = DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Uri.EscapeDataString(username.ToLowerInvariant()) + ".key");
    }

    public static void Save(string username, RsaKeyPair keys) =>
        File.WriteAllText(PathFor(username), JsonSerializer.Serialize(keys));

    public static RsaKeyPair? Load(string username)
    {
        var path = PathFor(username);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RsaKeyPair>(File.ReadAllText(path))
            : null;
    }
}
