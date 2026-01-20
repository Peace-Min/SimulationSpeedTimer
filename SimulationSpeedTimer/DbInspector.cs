using System;
using System.Data.SQLite;

namespace DbInspector
{
    class Program
    {
        static void Main(string[] args)
        {
            string dbPath = "test_independent_polling.db";
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                conn.Open();

                Console.WriteLine("=== Tables ===");
                using (var cmd = new SQLiteCommand("SELECT name, sql FROM sqlite_master WHERE type='table'", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Console.WriteLine($"Table: {reader["name"]}");
                        Console.WriteLine($"SQL: {reader["sql"]}");
                        Console.WriteLine("-------------------");
                    }
                }

                Console.WriteLine("\n=== Data Count ===");
                CheckCount(conn, "TableFast");
                CheckCount(conn, "TableSlow");
            }
        }

        static void CheckCount(SQLiteConnection conn, string tableName)
        {
            try
            {
                using (var cmd = new SQLiteCommand($"SELECT count(*) FROM {tableName}", conn))
                {
                    var count = Convert.ToInt32(cmd.ExecuteScalar());
                    Console.WriteLine($"{tableName} Count: {count}");
                }

                using (var cmd = new SQLiteCommand($"SELECT * FROM {tableName} LIMIT 3", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Console.WriteLine($"Row: s_time={reader["s_time"]}, val={reader["val"]}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {tableName}: {ex.Message}");
            }
        }
    }
}
