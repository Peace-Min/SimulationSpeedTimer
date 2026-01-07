using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// GlobalDataService + SimulationContext 통합 테스트
    /// - 새로운 아키텍처(Context -> Service)의 라이프사이클 격리 검증
    /// </summary>
    public static class GlobalDataServiceTest
    {
        public static void Run(int iterations = 1)
        {
            Console.WriteLine("=== Integration Test: Context & Service Lifecycle ===");
            Console.WriteLine($"Iterations: {iterations}\n");
            
            for (int i = 1; i <= iterations; i++)
            {
                Console.WriteLine($"--- Iteration {i}/{iterations} ---");
                RunIteration(i);
                Thread.Sleep(300); // 쿨다운
            }

            Console.WriteLine("\n=== All Tests Completed Successfully ===");
            
            // 추가: VM 통합 테스트
            VerifyTableDataViewModel();
        }

        private static void VerifyTableDataViewModel()
        {
            Console.WriteLine("\n=== TableDataViewModel Integration Test ===");

            // 1. Setup Scenario Schema (Fake)
            var scenarioSchema = new SimulationSchema();
            var table = new SchemaTableInfo("TestTable", "TestObj");
            table.AddColumn(new SchemaColumnInfo("COL1", "Value", "DOUBLE"));
            scenarioSchema.AddTable(table);

            // 2. ViewModel Init
            var vm = new TableDataViewModel();
            vm.OnScenarioLoaded(scenarioSchema);

            // 3. Start Session & Service
            Console.WriteLine("[Test] Starting Session...");
            SimulationContext.Instance.Start();
            
            string dbPath = CreateTempDatabaseWithData(0.0, 5, 0.5);
            GlobalDataService.Instance.Start(dbPath, 0.5);
            
            // Inject Data
            for(int k=0; k<5; k++) GlobalDataService.Instance.EnqueueTime(k * 0.1);

            // 4. Select Table & Verify
            Console.WriteLine("[Test] Selecting 'TestObj'...");
            vm.SelectedTableName = "TestObj"; // Valid
            
            // Wait for data
            WaitForCondition(() => vm.Rows.Count > 0, 3000, "VM Rows Population");
            
            if (vm.Rows.Count > 0) Console.WriteLine($"[PASS] VM Rows Populated: {vm.Rows.Count}");
            else Console.WriteLine("[FAIL] VM Rows Empty!");

            if (vm.Columns.Contains("Value")) Console.WriteLine("[PASS] VM Columns Correct.");
            else Console.WriteLine($"[FAIL] VM Columns Missing 'Value'. Actual: {string.Join(", ", vm.Columns)}");

            // 5. Select Invalid Table
            Console.WriteLine("[Test] Selecting 'InvalidObj'...");
            vm.SelectedTableName = "InvalidObj";
            
            // Rows should be cleared immediately
            if (vm.Rows.Count == 0) Console.WriteLine("[PASS] Invalid Table -> Empty Rows.");
            else Console.WriteLine($"[FAIL] Invalid Table has rows! ({vm.Rows.Count})");

            // Cleanup
            GlobalDataService.Instance.Stop();
            SimulationContext.Instance.Stop();
            CleanupDatabase(dbPath);
            Console.WriteLine("=== VM Test Done ===");
        }

        private static void RunIteration(int iteration)
        {
            // --- Phase 1: Session A Start ---
            Console.WriteLine("[Phase 1] Session A Start (1000.0~)");
            
            // [NEW ARCHITECTURE] 1. Context Start (ID 생성 & Repo 초기화)
            SimulationContext.Instance.Start();
            
            string dbPathA = CreateTempDatabaseWithData(1000.0, 5, 0.5); 
            GlobalDataService.Instance.Start(dbPathA, 0.5);

            // [FIX] Inject Time Signals to trigger queries
            for(int k=0; k<5; k++) GlobalDataService.Instance.EnqueueTime(1000.0 + k * 0.1);

            Guid sessionA_Id = SimulationContext.Instance.CurrentSessionId;
            Console.WriteLine($"[Observe] Session A ID: {sessionA_Id}");

            // 데이터 도착 대기
            WaitForCondition(() => SharedFrameRepository.Instance.GetFrameCount() > 0, 2000, "Waiting for frames (A)");
            Thread.Sleep(500); 

            // --- Phase 2: STOP Session A ---
            Console.WriteLine("[Phase 2] STOP Session A (Graceful Drain)");
            
            // Stop 순서: Service 먼저 끄고 -> Context 종료
            GlobalDataService.Instance.Stop();
            SimulationContext.Instance.Stop();

            // 검증: 아직 Repo에는 데이터가 남아있어야 함 (Context.Start()가 불리기 전까지)
            int countAfterStop = SharedFrameRepository.Instance.GetFrameCount();
            Console.WriteLine($"[Observe] Repository count after Session A Stop: {countAfterStop}");
            if (countAfterStop == 0) Console.WriteLine("[FAIL] Repository shouldn't be empty yet.");

            // --- Phase 3: START Session B ---
            Console.WriteLine("[Phase 3] START Session B (0.0~)");
            
            // Context Start -> 이 시점에 Repo Clear가 발생해야 함!
            SimulationContext.Instance.Start(); 
            Guid sessionB_Id = SimulationContext.Instance.CurrentSessionId;

            // 검증: Repo가 비워졌는지
            int countAtStartB = SharedFrameRepository.Instance.GetFrameCount();
            Console.WriteLine($"[Observe] Session B ID: {sessionB_Id}");
            Console.WriteLine($"[Observe] Repository count at Session B Start: {countAtStartB}");
            
            if (countAtStartB != 0) Console.WriteLine("[FAIL] Repository NOT Cleared!");
            else Console.WriteLine("[PASS] Repository Cleared (Count is 0).");

            if (sessionA_Id == sessionB_Id) Console.WriteLine("[FAIL] Session IDs are identical!");

            // Service Start (Session B 데이터)
            string dbPathB = CreateTempDatabaseWithData(0.0, 5, 0.5);
            GlobalDataService.Instance.Start(dbPathB, 0.5);

            // [FIX] Inject Time Signals for B
            for(int k=0; k<5; k++) GlobalDataService.Instance.EnqueueTime(0.0 + k * 0.1);

            Thread.Sleep(500); // Collect data

            Console.WriteLine("[Phase 3] Session B Data Flowing...");
            WaitForCondition(() => SharedFrameRepository.Instance.GetFrameCount() >= 5, 3000, "Session B Frames");

            // --- Phase 4: Final STOP ---
            Console.WriteLine("[Phase 4] Final STOP");
            GlobalDataService.Instance.Stop();
            SimulationContext.Instance.Stop();

            int finalCount = SharedFrameRepository.Instance.GetFrameCount();
            Console.WriteLine($"[Observe] Final Repository Frame Count: {finalCount}");

            CleanupDatabase(dbPathA);
            CleanupDatabase(dbPathB);
            
            Console.WriteLine($"[Summary] Iteration {iteration} - OK");
        }

        private static string CreateTempDb()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"chaos_{Guid.NewGuid().ToString().Substring(0, 8)}.db");
        }

        private static void SetupDatabase(string dbPath, double startTime, double endTime)
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            
            SQLiteConnection.CreateFile(dbPath);
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE Object_Info (object_name TEXT, table_name TEXT);
                        INSERT INTO Object_Info VALUES ('TestObj', 'TestTable');
                        
                        CREATE TABLE Column_Info (table_name TEXT, column_name TEXT, attribute_name TEXT, data_type TEXT);
                        INSERT INTO Column_Info VALUES ('TestTable', 'COL1', 'Value', 'DOUBLE');
                        
                        CREATE TABLE TestTable (s_time DOUBLE, COL1 DOUBLE);
                    ";
                    cmd.ExecuteNonQuery();
                    
                    using (var tx = conn.BeginTransaction())
                    {
                        for (double t = startTime; t <= endTime; t += 0.1)
                        {
                            var insertCmd = conn.CreateCommand();
                            insertCmd.CommandText = $"INSERT INTO TestTable VALUES ({t}, {t * 10})";
                            insertCmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                }
            }
        }

        private static void CleanupDb(string dbPath)
        {
            try
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
            }
            catch { }
        }

        // --- Helpers used by RunIteration ---

        private static string CreateTempDatabaseWithData(double startTime, int count, double step)
        {
            string dbPath = CreateTempDb();
            double endTime = startTime + (count - 1) * 0.1; // 데이터 생성 간격은 0.1 고정 (SetupDatabase 구현상)
            SetupDatabase(dbPath, startTime, endTime);
            return dbPath;
        }

        private static void CleanupDatabase(string dbPath) => CleanupDb(dbPath);

        private static void WaitForCondition(Func<bool> condition, int timeoutMs, string conditionName)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (condition()) return;
                Thread.Sleep(50);
                waited += 50;
            }
            Console.WriteLine($"[Wait Timeout] {conditionName} condition not met after {timeoutMs}ms");
        }
    }
}
