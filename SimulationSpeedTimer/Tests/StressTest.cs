using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer.Tests
{
    // Mock DB Config for testing (using memory or temp file)
    public class MockTest
    {
        public static void RunStressTest()
        {
            Console.WriteLine("=== Starting Start/Stop Stress Test ===");

            try 
            {
                // 1. 임시 DB 파일 생성 (실제 파일 I/O 테스트를 위해)
                string dbPath = Path.Combine(Path.GetTempPath(), "test_simulation.db");
                if (File.Exists(dbPath)) File.Delete(dbPath);
                
                Console.WriteLine($"[Test] Initializing DB at {dbPath}...");
                InitializeTestDb(dbPath);
                Console.WriteLine("[Test] DB Initialized.");

                // 2. 컨트롤러 설정
                var config = new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "TestObj",
                    XAxisAttributeName = "TestAttr",
                    YAxisObjectName = "TestObj",
                    YAxisAttributeName = "TestAttr",
                    RetryCount = 0
                };

                SimulationController.Instance.AddService("Chart1", config);
                Console.WriteLine("[Test] Service Added.");

                // 3. 스트레스 테스트 실행 (매우 빠른 Start/Stop 반복)
                int iterations = 20;
                var random = new Random();

                for (int i = 0; i < iterations; i++)
                {
                    Console.Write($"[{i + 1}]");
                    
                    // Start
                    SimulationController.Instance.Start(queryInterval: 1.0);

                    // Random fast delay
                    int delay = random.Next(1, 50); 
                    Thread.Sleep(delay);
                    
                    // Send Time
                    SimulationController.Instance.OnTimeReceived(new STime { Sec = (uint)i, Usec = 0 });

                    // Stop
                    SimulationController.Instance.Stop();
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Test Error] {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine("\n=== Stress Test Completed ===");
            
            // Clean up
            SimulationController.Instance.Dispose();
        }

        private static void InitializeTestDb(string path)
        {
            try
            {
                var builder = new System.Data.SQLite.SQLiteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false
                };

                using (var conn = new System.Data.SQLite.SQLiteConnection(builder.ToString()))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS TestTable (id INTEGER PRIMARY KEY, s_time REAL, val REAL);";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"[Init DB Error] {ex.Message}");
                 throw;
            }
        }
    }
}
