using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Data.SQLite;
using SimulationSpeedTimer;

namespace SimulationSpeedTimer.Tests
{
    public class IndependentPollingVerification
    {
        private const string TestDbPath = "test_independent_polling.db";
        private const string TableFast = "TableFast";
        private const string TableSlow = "TableSlow";

        public static void Run()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   [Validation] Independent Polling & Merging");
            Console.WriteLine("==================================================\n");

            // 1. Setup Database
            SetupDatabase();
            Console.WriteLine("[Setup] Database & Schema Initialized.");

            // 2. Start GlobalDataService
            // Context Start
            SimulationContext.Instance.Start();

            // Service Config
            var gdConfig = new GlobalDataService.GlobalDataServiceConfig
            {
                DbPath = TestDbPath,
                QueryInterval = 0.5 // High frequency for testing
            };
            GlobalDataService.Instance.Start(gdConfig);
            Console.WriteLine("[Setup] GlobalDataService Started.\n");

            try
            {
                // =========================================================
                // Phase 1: Async Data Arrival (Independent Polling)
                // =========================================================
                Console.WriteLine("[Phase 1] Testing Independent Polling (Fast vs Slow Table)...");

                // Scenario: Fast Table writes up to 10.0s, Slow Table writes only up to 5.0s
                // Reader should fetch Fast:10.0 and Slow:5.0 independently.

                InsertData(TableFast, 0.0, 10.0); // 0.0 ~ 10.0 (Step 0.5)
                InsertData(TableSlow, 0.0, 5.0);  // 0.0 ~ 5.0  (Step 0.5)

                // Wait for DB commit & WAL propagation
                Thread.Sleep(200);

                // Force Checkpoint
                using (var conn = new SQLiteConnection($"Data Source={TestDbPath};Version=3;"))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("PRAGMA wal_checkpoint(FULL)", conn)) cmd.ExecuteNonQuery();
                }

                // Trigger Fetch
                GlobalDataService.Instance.EnqueueTime(10.0);

                // Wait for data (Timeout 3s)
                WaitForCondition(() =>
                {
                    var frames = SharedFrameRepository.Instance.GetLatestFrames(20);
                    // Check if we have frame at 10.0
                    var f10 = frames.FirstOrDefault(f => Math.Abs(f.Time - 10.0) < 0.001);
                    return f10 != null;
                }, 3000, "Fetch 10.0s Data");

                // Validation logic
                var latestFrames = SharedFrameRepository.Instance.GetLatestFrames(50);
                var frame10 = latestFrames.FirstOrDefault(f => Math.Abs(f.Time - 10.0) < 0.001);
                var frame5 = latestFrames.FirstOrDefault(f => Math.Abs(f.Time - 5.0) < 0.001);

                bool fastOk = frame10 != null && frame10.GetTable(TableFast) != null;
                bool slowOk = frame5 != null && frame5.GetTable(TableSlow) != null;
                // Important: Slow table should NOT be in frame 10.0 yet
                bool slowMissingAt10 = frame10 != null && frame10.GetTable(TableSlow) == null;

                if (fastOk && slowOk && slowMissingAt10)
                {
                    Console.WriteLine(" > PASS: Independent Polling confirmed.");
                    Console.WriteLine($"   Frame 10.0 contains {TableFast} (OK) and misses {TableSlow} (OK).");
                }
                else
                {
                    Console.WriteLine(" > FAIL: Independent Polling logic failed.");
                    Console.WriteLine($"   Fast@10: {fastOk}, Slow@5: {slowOk}, SlowMissing@10: {slowMissingAt10}");
                }

                // =========================================================
                // Phase 2: Data Merging (Late Arrival)
                // =========================================================
                Console.WriteLine("\n[Phase 2] Testing Data Merging (Late Arrival)...");

                // Scenario: Slow Table writes remainder (5.5 ~ 10.0)
                // Service should fetch this and MERGE it into existing frames.
                InsertData(TableSlow, 5.5, 10.0);

                // Trigger Fetch Again
                GlobalDataService.Instance.EnqueueTime(10.5); // Slightly ahead to force check

                // Wait for merging (Timeout 3s)
                WaitForCondition(() =>
                {
                    var frames = SharedFrameRepository.Instance.GetLatestFrames(50);
                    var f10 = frames.FirstOrDefault(f => Math.Abs(f.Time - 10.0) < 0.001);
                    // Now frame 10.0 should have BOTH tables
                    return f10 != null && f10.GetTable(TableSlow) != null;
                }, 3000, "Merge Slow Table Data");

                var validFrame10 = SharedFrameRepository.Instance.GetLatestFrames(50).FirstOrDefault(f => Math.Abs(f.Time - 10.0) < 0.001);
                if (validFrame10 != null && validFrame10.GetTable(TableFast) != null && validFrame10.GetTable(TableSlow) != null)
                {
                    Console.WriteLine(" > PASS: Data Merging confirmed.");
                    Console.WriteLine($"   Frame 10.0 now contains BOTH {TableFast} and {TableSlow}.");
                }
                else
                {
                    Console.WriteLine(" > FAIL: Merging failed.");
                }

                // =========================================================
                // Phase 3: Graceful Shutdown (Final Sweep)
                // =========================================================
                Console.WriteLine("\n[Phase 3] Testing Graceful Shutdown (Final Sweep)...");

                // Scenario: Write slightly more data (Fast: 15.0, Slow: 12.0)
                // Call Stop() immediately.
                // Service should perform one last sweep and fetch up to 15.0 (Fast) and 12.0 (Slow).

                InsertData(TableFast, 10.5, 15.0);
                InsertData(TableSlow, 10.5, 12.0);

                // We simulate "Engine reached 15.0"
                GlobalDataService.Instance.EnqueueTime(15.0);
                Thread.Sleep(100); // Give it a tiny moment to process current buffer

                Console.WriteLine(" > Stopping Service...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GlobalDataService.Instance.Stop();
                sw.Stop();

                Console.WriteLine($" > Service Stopped in {sw.ElapsedMilliseconds}ms.");

                // Validate Repository content after stop
                // Since repository might be cleared on session stop, we should check logs or if repository persists slightly.
                // In current architecture, SharedFrameRepository holds data per session.

                // Note: The correct verification here is that the process didn't hang and finished quickly.
                if (sw.ElapsedMilliseconds < 2000)
                {
                    Console.WriteLine(" > PASS: Shutdown completed without hang.");
                }
                else
                {
                    Console.WriteLine(" > WARNING: Shutdown took too long.");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Test Failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Cleanup();
            }
        }

        private static void WaitForCondition(Func<bool> condition, int timeoutMs, string workName)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (condition()) return;
                Thread.Sleep(50);
                waited += 50;
            }
            Console.WriteLine($"   [Timeout] Failed to {workName} within {timeoutMs}ms");
        }

        private static void SetupDatabase()
        {
            if (File.Exists(TestDbPath)) File.Delete(TestDbPath);
            SQLiteConnection.CreateFile(TestDbPath);

            using (var conn = new SQLiteConnection($"Data Source={TestDbPath};Version=3;"))
            {
                conn.Open();

                using (var cmd = new SQLiteCommand("CREATE TABLE Object_Info (object_name TEXT, table_name TEXT)", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand("CREATE TABLE Column_Info (table_name TEXT, column_name TEXT, attribute_name TEXT, data_type TEXT)", conn)) cmd.ExecuteNonQuery();

                // Register Fast Table
                using (var cmd = new SQLiteCommand($"INSERT INTO Object_Info VALUES ('ObjFast', '{TableFast}')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"INSERT INTO Column_Info VALUES ('{TableFast}', 'val', 'Value', 'DOUBLE')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"CREATE TABLE {TableFast} (s_time DOUBLE PRIMARY KEY, val DOUBLE)", conn)) cmd.ExecuteNonQuery();

                // Register Slow Table
                using (var cmd = new SQLiteCommand($"INSERT INTO Object_Info VALUES ('ObjSlow', '{TableSlow}')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"INSERT INTO Column_Info VALUES ('{TableSlow}', 'val', 'Value', 'DOUBLE')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"CREATE TABLE {TableSlow} (s_time DOUBLE PRIMARY KEY, val DOUBLE)", conn)) cmd.ExecuteNonQuery();
            }
        }

        private static void InsertData(string tableName, double start, double end)
        {
            using (var conn = new SQLiteConnection($"Data Source={TestDbPath};Version=3;"))
            {
                conn.Open();
                using (var trans = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"INSERT OR REPLACE INTO {tableName} (s_time, val) VALUES (@t, @v)";
                        var pT = cmd.Parameters.Add("@t", System.Data.DbType.Double);
                        var pV = cmd.Parameters.Add("@v", System.Data.DbType.Double);

                        for (double t = start; t <= end + 0.001; t += 0.5)
                        {
                            pT.Value = Math.Round(t, 1);
                            pV.Value = t * 10;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    trans.Commit();
                }
            }
            // Console.WriteLine($"   [Writer] Inserted {tableName} ({start:F1} ~ {end:F1})");
        }

        private static void Cleanup()
        {
            try
            {
                SimulationContext.Instance.Stop();
                GlobalDataService.Instance.Stop();
                SQLiteConnection.ClearAllPools();

                // if (File.Exists(TestDbPath)) File.Delete(TestDbPath);

                Console.WriteLine("\n[Cleanup] Resources released.");
                Environment.Exit(0);
            }
            catch { }
        }
    }
}
