using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace SimulationSpeedTimer.Tests
{
    public static class ArchitectureValidator
    {
        public static void Run()
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("[Architecture Validator] Starting Tests");
            Console.WriteLine("========================================");

            try
            {
                // 1. 설계 준수 및 격리 테스트
                TestSessionIsolation();

                Console.WriteLine("\n[Architecture Check: PASS]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Architecture Check: FAIL] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Console.WriteLine("\n[Architecture Validator] Tests Finished.");
                Environment.Exit(0);
            }
        }

        private static void TestSessionIsolation()
        {
            Console.WriteLine("\n[Test] Session Isolation (Stop A -> Start B)");

            string dbPathA = CreateTempDb("A");
            string dbPathB = CreateTempDb("B");

            try
            {
                // --- Session A setup ---
                Console.WriteLine("1. Starting Session A...");
                SimulationContext.Instance.Start();
                var sessionA_Id = SimulationContext.Instance.CurrentSessionId;
                
                var configA = new GlobalDataService.GlobalDataServiceConfig { DbPath = dbPathA, QueryInterval = 0.1 };
                GlobalDataService.Instance.Start(configA);

                // Inject Data A
                Console.WriteLine("2. Injecting 10 frames to Session A...");
                for (int i = 0; i < 10; i++)
                {
                    GlobalDataService.Instance.EnqueueTime(i * 0.1); // 0.0 ~ 0.9
                }
                
                // Wait briefly for processing
                Thread.Sleep(500);
                int countA = SharedFrameRepository.Instance.GetFrameCount();
                Console.WriteLine($"   -> Session A Stored Frames: {countA} (Expected >= 9)");

                // --- Scenario: Stop A and Immediately Start B ---
                Console.WriteLine("3. Calling Stop() on Session A...");
                GlobalDataService.Instance.Stop();
                SimulationContext.Instance.Stop();

                Console.WriteLine("4. Starting Session B IMMEDIATELY...");
                
                // Start Session B
                SimulationContext.Instance.Start(); // New ID generated, Repo cleared
                var sessionB_Id = SimulationContext.Instance.CurrentSessionId;
                
                if (sessionA_Id == sessionB_Id) throw new Exception("Session IDs must be different!");
                
                var configB = new GlobalDataService.GlobalDataServiceConfig { DbPath = dbPathB, QueryInterval = 0.1 };
                GlobalDataService.Instance.Start(configB);

                // Inject Data B (Use different time range to detect mixing)
                // Session A는 0.0~0.9이고, Session B는 100.0~100.9를 사용.
                // 만약 Session A의 잔여 데이터가 들어온다면, Key가 다르므로 Repository의 전체 Count가 증가할 것임.
                Console.WriteLine("5. Injecting 10 frames to Session B (Time 100.0~)...");
                for (int i = 0; i < 10; i++)
                {
                    GlobalDataService.Instance.EnqueueTime(100.0 + (i * 0.1));
                }

                // Wait for B to process
                Console.WriteLine("6. Waiting for Session B processing (Max 3s)...");
                var cts = new CancellationTokenSource(3000); // 타임아웃 3초
                bool finished = false;
                
                while (!cts.IsCancellationRequested)
                {
                    int countB = SharedFrameRepository.Instance.GetFrameCount();
                    // B 세션 데이터만 정상적으로 들어오면 약 9~10개 일 것임.
                    if (countB >= 5) 
                    {
                        finished = true;
                        Console.WriteLine($"   -> Session B Stored Frames: {countB}");
                        break;
                    }
                    Thread.Sleep(100);
                }

                if (!finished)
                {
                    throw new TimeoutException("Session B processing timed out.");
                }

                // Verify Cross-Contamination
                int finalCount = SharedFrameRepository.Instance.GetFrameCount();
                Console.WriteLine($"7. Final Verification: Repository Count = {finalCount}");
                
                // B 세션 데이터(최대 10개) + 여유분 고려해도 12개를 넘으면 A 데이터가 섞인 것.
                if (finalCount > 12) 
                {
                    throw new Exception($"Isolation Failed! Found {finalCount} frames. Likely contaminated by Session A.");
                }

                Console.WriteLine("[Session Isolation Test: Pass]");

                // 2. 정상 종료 및 강제 중단 테스트
                TestGracefulShutdownOverride();

                Console.WriteLine("\n[Architecture Check: PASS]");
            }
            finally
            {
                GlobalDataService.Instance.Stop();
                SimulationContext.Instance.Stop();
                CleanupDb(dbPathA);
                CleanupDb(dbPathB);
            }
        }

        private static void TestGracefulShutdownOverride()
        {
            Console.WriteLine("\n[Test] Graceful Shutdown Override (Complete -> Stop)");
            string dbPath = CreateTempDb("Graceful");
            
            try
            {
                SimulationContext.Instance.Start();
                var config = new GlobalDataService.GlobalDataServiceConfig { DbPath = dbPath, QueryInterval = 0.1 };
                GlobalDataService.Instance.Start(config);

                // 1. Data Injection
                for (int i = 0; i < 50; i++) GlobalDataService.Instance.EnqueueTime(i * 0.1);

                // 2. Request CompleteSession with Callback
                bool callbackInvoked = false;
                Console.WriteLine("2. Requesting CompleteSession...");
                
                GlobalDataService.Instance.CompleteSession(() => 
                {
                    callbackInvoked = true;
                    Console.WriteLine("   -> Callback Invoked (Should NOT happen if Stopped early)");
                });

                // 3. Immediately Override with Stop()
                Console.WriteLine("3. Calling Stop() IMMEDIATELY (Override)...");
                GlobalDataService.Instance.Stop();

                // 4. Wait a bit to ensure worker finishes
                Thread.Sleep(1000);

                if (callbackInvoked)
                {
                    throw new Exception("Graceful Shutdown Callback was invoked despite Forced Stop! (Fail)");
                }
                else
                {
                    Console.WriteLine("   -> Callback NOT Invoked (Correct behavior).");
                    Console.WriteLine("[Graceful Shutdown Override Test: Pass]");
                }
            }
            finally 
            {
                CleanupDb(dbPath);
            }
        }

        private static string CreateTempDb(string suffix)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"test_iso_{suffix}_{Guid.NewGuid().ToString().Substring(0, 4)}.db");
            SQLiteConnection.CreateFile(path);
            
            using (var conn = new SQLiteConnection($"Data Source={path};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                     // 필수 테이블 생성 (test_iso_A, B 둘 다 0.0 ~ 110.0까지 커버되도록 insert)
                    cmd.CommandText = @"
                        CREATE TABLE Object_Info (object_name TEXT, table_name TEXT);
                        INSERT INTO Object_Info VALUES ('TestObj', 'TestTable');
                        CREATE TABLE Column_Info (table_name TEXT, column_name TEXT, attribute_name TEXT, data_type TEXT);
                        CREATE TABLE TestTable (s_time DOUBLE);
                    ";
                    cmd.ExecuteNonQuery();
                    
                    // 데이터 미리 채워두기 (0.0 ~ 200.0)
                    using(var tx = conn.BeginTransaction())
                    {
                        for(int i=0; i<2000; i++) // 0.1초씩 2000개 -> 200.0초
                        {
                            var cmd2 = conn.CreateCommand();
                            cmd2.CommandText = $"INSERT INTO TestTable VALUES ({i * 0.1})";
                            cmd2.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                }
            }
            return path;
        }

        private static void CleanupDb(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
            try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
        }
    }
}
