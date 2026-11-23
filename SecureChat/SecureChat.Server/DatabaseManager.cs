using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SecureChat.Server
{
    public class DatabaseManager
    {
        private const int SqliteConstraint = 19; // SQLITE_CONSTRAINT (trùng UNIQUE...)
        private readonly string connectionString;

        public DatabaseManager(string dbPath = "securechat.db")
        {
            connectionString = "Data Source=" + dbPath;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                MigrateOldSchemaIfNeeded(connection);

                // Username không phân biệt hoa/thường (Alice = alice) để khớp với Client.
                string createUsersTable = @"
                    CREATE TABLE IF NOT EXISTS Users (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Username TEXT UNIQUE NOT NULL COLLATE NOCASE,
                        PasswordHash TEXT NOT NULL,
                        PublicKey TEXT NOT NULL,
                        Status TEXT DEFAULT 'Offline'
                    );";

                string createMessagesTable = @"
                    CREATE TABLE IF NOT EXISTS Messages (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Sender TEXT NOT NULL COLLATE NOCASE,
                        Receiver TEXT NOT NULL COLLATE NOCASE,
                        EncryptedContent TEXT NOT NULL,
                        Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                    );";

                string createIndex = "CREATE INDEX IF NOT EXISTS IX_Messages_Pair ON Messages (Sender, Receiver);";

                foreach (string sql in new[] { createUsersTable, createMessagesTable, createIndex })
                {
                    using (var command = new SqliteCommand(sql, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Database của phiên bản cũ có bảng Users chưa có cột PublicKey.
        /// Nếu bảng còn trống thì tự tạo lại; nếu đã có dữ liệu thì báo lỗi rõ ràng thay vì chạy sai.
        /// </summary>
        private void MigrateOldSchemaIfNeeded(SqliteConnection connection)
        {
            bool tableExists = false;
            bool hasPublicKey = false;
            using (var command = new SqliteCommand("PRAGMA table_info(Users);", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    tableExists = true;
                    if (string.Equals(reader.GetString(1), "PublicKey", StringComparison.OrdinalIgnoreCase))
                        hasPublicKey = true;
                }
            }

            if (!tableExists || hasPublicKey) return;

            long userCount;
            using (var command = new SqliteCommand("SELECT COUNT(1) FROM Users", connection))
                userCount = (long)(command.ExecuteScalar() ?? 0);

            if (userCount > 0)
                throw new InvalidOperationException(
                    "File database cũ (thiếu cột PublicKey) đang có dữ liệu. Hãy xóa file securechat.db rồi chạy lại.");

            using (var command = new SqliteCommand("DROP TABLE Users;", connection))
                command.ExecuteNonQuery();
        }

        public bool RegisterUser(string username, string passwordHash, string publicKey)
        {
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    string query = "INSERT INTO Users (Username, PasswordHash, PublicKey, Status) VALUES (@username, @password, @publicKey, 'Offline')";
                    using (var command = new SqliteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@username", username);
                        command.Parameters.AddWithValue("@password", passwordHash);
                        command.Parameters.AddWithValue("@publicKey", publicKey);
                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                return false; // Trùng username
            }
        }

        public bool ValidateUser(string username, string passwordHash)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                string query = "SELECT COUNT(1) FROM Users WHERE Username = @username AND PasswordHash = @password";
                using (var command = new SqliteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@username", username);
                    command.Parameters.AddWithValue("@password", passwordHash);
                    long count = (long)(command.ExecuteScalar() ?? 0);
                    return count > 0;
                }
            }
        }

        /// <summary>Trả về tên đăng nhập đúng như lúc đăng ký (đúng hoa/thường), hoặc null nếu không tồn tại.</summary>
        public string? FindUser(string username)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand("SELECT Username FROM Users WHERE Username = @username", connection))
                {
                    command.Parameters.AddWithValue("@username", username);
                    return command.ExecuteScalar() as string;
                }
            }
        }

        public string? GetPublicKey(string username)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand("SELECT PublicKey FROM Users WHERE Username = @username", connection))
                {
                    command.Parameters.AddWithValue("@username", username);
                    return command.ExecuteScalar() as string;
                }
            }
        }

        public void SetStatus(string username, string status)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand("UPDATE Users SET Status = @status WHERE Username = @username", connection))
                {
                    command.Parameters.AddWithValue("@status", status);
                    command.Parameters.AddWithValue("@username", username);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Khi server vừa khởi động thì chưa ai online.</summary>
        public void ResetAllStatus()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand("UPDATE Users SET Status = 'Offline'", connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        public void SaveMessage(string sender, string receiver, string encryptedContent, DateTime timestampUtc)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                string query = "INSERT INTO Messages (Sender, Receiver, EncryptedContent, Timestamp) VALUES (@sender, @receiver, @content, @time)";
                using (var command = new SqliteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@sender", sender);
                    command.Parameters.AddWithValue("@receiver", receiver);
                    command.Parameters.AddWithValue("@content", encryptedContent);
                    command.Parameters.AddWithValue("@time", timestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Tin nhắn giữa hai người (cả hai chiều), cũ → mới, tối đa <paramref name="limit"/> tin mới nhất.</summary>
        public List<Packet> GetHistory(string userA, string userB, int limit)
        {
            var result = new List<Packet>();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                string query = @"
                    SELECT Sender, Receiver, EncryptedContent, Timestamp FROM (
                        SELECT Id, Sender, Receiver, EncryptedContent, Timestamp FROM Messages
                        WHERE (Sender = @a AND Receiver = @b) OR (Sender = @b AND Receiver = @a)
                        ORDER BY Id DESC
                        LIMIT @limit
                    ) ORDER BY Id ASC";
                using (var command = new SqliteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@a", userA);
                    command.Parameters.AddWithValue("@b", userB);
                    command.Parameters.AddWithValue("@limit", limit);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new Packet
                            {
                                Type = "CHAT",
                                Sender = reader.GetString(0),
                                Receiver = reader.GetString(1),
                                Payload = reader.GetString(2),
                                Timestamp = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                            });
                        }
                    }
                }
            }
            return result;
        }
    }
}
