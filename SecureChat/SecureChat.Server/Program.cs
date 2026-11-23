using System;
using System.IO;

namespace SecureChat.Server
{
    class Program
    {
        // Cách chạy:  dotnet run --project SecureChat.Server -- [cổng] [đường-dẫn-file-db]
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            int port = 8888;
            if (args.Length > 0 && int.TryParse(args[0], out int p) && p > 0 && p < 65536)
                port = p;

            string dbPath = args.Length > 1
                ? args[1]
                : Path.Combine(Environment.CurrentDirectory, "securechat.db");

            ServerCore server = new ServerCore(port, dbPath);
            Console.WriteLine($"[SERVER] Database: {dbPath}");
            Console.WriteLine("[SERVER] Nhấn Ctrl+C để dừng.");
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                server.Stop();
            };

            server.Start();
            Console.WriteLine("[SERVER] Đã dừng.");
        }
    }
}
