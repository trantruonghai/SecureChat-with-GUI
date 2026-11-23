using Microsoft.Data.Sqlite;
using Xunit;

namespace SecureChat.Server.Tests;

/// <summary>Test DatabaseManager trên file SQLite thật trong thư mục tạm.</summary>
public class DatabaseManagerTests
{
    private static void WithDb(Action<DatabaseManager, string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"securechat-dbtest-{Guid.NewGuid():N}.db");
        try
        {
            body(new DatabaseManager(path), path);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // nhả khóa file trên Windows
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void RegisterUser_Then_Validate_And_FindUser_AreCaseInsensitive()
    {
        WithDb((db, _) =>
        {
            Assert.True(db.RegisterUser("Alice", "hash1", "pubkey1"));

            Assert.True(db.ValidateUser("alice", "hash1"));
            Assert.False(db.ValidateUser("alice", "sai-hash"));
            Assert.False(db.ValidateUser("bob", "hash1"));
            Assert.Equal("Alice", db.FindUser("ALICE"));
            Assert.Null(db.FindUser("bob"));
            Assert.Equal("pubkey1", db.GetPublicKey("alice"));
        });
    }

    [Fact]
    public void RegisterUser_Duplicate_ReturnsFalse()
    {
        WithDb((db, _) =>
        {
            Assert.True(db.RegisterUser("alice", "h", "k"));
            Assert.False(db.RegisterUser("ALICE", "h2", "k2"));
        });
    }

    [Fact]
    public void History_ReturnsBothDirections_OldestFirst_AndRespectsLimit()
    {
        WithDb((db, _) =>
        {
            var now = DateTime.UtcNow;
            db.SaveMessage("alice", "bob", "m1", now);
            db.SaveMessage("bob", "alice", "m2", now.AddSeconds(1));
            db.SaveMessage("alice", "carol", "khác cuộc trò chuyện", now.AddSeconds(2));
            db.SaveMessage("alice", "bob", "m3", now.AddSeconds(3));

            var all = db.GetHistory("alice", "bob", 10);
            Assert.Equal(3, all.Count);
            Assert.Equal("m1", all[0].Payload);
            Assert.Equal("m2", all[1].Payload);
            Assert.Equal("m3", all[2].Payload);
            Assert.Equal("CHAT", all[0].Type);

            var last2 = db.GetHistory("BOB", "Alice", 2); // không phân biệt hoa/thường, đảo chiều vẫn đúng
            Assert.Equal(2, last2.Count);
            Assert.Equal("m2", last2[0].Payload);
            Assert.Equal("m3", last2[1].Payload);
        });
    }

    [Fact]
    public void OldEmptyDatabase_IsUpgradedAutomatically()
    {
        WithDb((_, path) =>
        {
            // giả lập file securechat.db cũ của phiên bản đầu (không có cột PublicKey, chưa có user)
            var oldPath = path + ".old";
            using (var conn = new SqliteConnection($"Data Source={oldPath}"))
            {
                conn.Open();
                using var cmd = new SqliteCommand(
                    "CREATE TABLE Users (Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT UNIQUE NOT NULL, PasswordHash TEXT NOT NULL, Status TEXT DEFAULT 'Offline');", conn);
                cmd.ExecuteNonQuery();
            }
            try
            {
                var upgraded = new DatabaseManager(oldPath);
                Assert.True(upgraded.RegisterUser("alice", "h", "k"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(oldPath); } catch (IOException) { }
            }
        });
    }
}
