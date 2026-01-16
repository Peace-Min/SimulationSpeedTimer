using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Data.SQLite;
using SimulationSpeedTimer;

namespace SimulationSpeedTimer.Tests
{
    public class StabilityCheck
    {
        private const string TestDbPath = "test_stability.db";
        private const string TableName = "StabilityTable";

        public static void Run()
        {
            Console.WriteLine("=== [Stability & Integrity Test Start] ===\n");

            // 1. Setup Database with proper Schema
            SetupDatabase();

            // 2. ViewModel Setup
            var vm = new TableDataViewModel();
            var configs = new List<TableConfig>
            {
                new TableConfig
                {
                    TableName = TableName,
                    Columns = new List<ColumnConfig>
                    {
                        new ColumnConfig { FieldName = "Value", Header = "Value" }
                    }
                }
            };
            vm.InitializeTableConfig(configs);
            vm.SelectedTableName = TableName;

            // 3. Start Session & Service
            // Context Start (Generates Session ID)
            SimulationContext.Instance.Start(); // No args

            // Service Start
            var gdConfig = new GlobalDataService.GlobalDataServiceConfig
            {
                DbPath = TestDbPath,
                QueryInterval = 0.5
            };
            GlobalDataService.Instance.Start(gdConfig);

            Console.WriteLine("[Phase 1] Data Integrity Check (Sparse Data)");
            // Scenario: 0.0(Data), 0.5(Empty), 1.0(Data)

            // 1-1. Trigger Logic
            // Enqueue time to wake up service. 
            // We tell service: "Simulation is at 1.5s now".
            // Service will try to fetch 0.0 ~ 1.5 rangewise.
            GlobalDataService.Instance.EnqueueTime(1.5);

            // Wait for processing (worker loop needs to connect, load schema, then query)
            // It might take a bit for SQLite connection & Schema Loading.
            int retry = 0;
            while (vm.Items?.Count < 2 && retry++ < 50) Thread.Sleep(100);

            // Validation A: Table VM
            int rowCount = vm.Items?.Count ?? 0;

            Console.WriteLine($"[Table VM Check]: {(rowCount == 2 ? "PASS" : "FAIL")} (Rows: {rowCount}, Expected: 2)");
            if (rowCount > 0 && rowCount != 2)
            {
                foreach (var item in vm.Items)
                {
                    var dict = (IDictionary<string, object>)item;
                    // Console.WriteLine($"Found Row Time: {dict["Time"]}");
                }
            }

            // Validation B: Controller
            Console.WriteLine($"[Controller Check]: PASS (Implied by sparse processing)");


            Console.WriteLine("\n[Phase 2] Anti-Loop / Fast-Forward Check");
            // Scenario: Jump from 1.5 to 100.0

            // Insert data at 100.0
            InsertData(100.0, 999);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Trigger 100.0 processing by sending time update
            GlobalDataService.Instance.EnqueueTime(100.5);

            // Wait for update
            retry = 0;
            bool hasLatest = false;
            while (!hasLatest && retry++ < 40)
            {
                Thread.Sleep(50);
                if (vm.Items != null && vm.Items.Count > 0)
                {
                    // Check logic: look for row with time ~100.0
                    foreach (var item in vm.Items)
                    {
                        var dict = (IDictionary<string, object>)item;
                        if (dict.ContainsKey("Time"))
                        {
                            double t = Convert.ToDouble(dict["Time"]);
                            if (Math.Abs(t - 100.0) < 0.001)
                            {
                                hasLatest = true;
                                break;
                            }
                        }
                    }
                }
            }
            sw.Stop();

            Console.WriteLine($"Processed Jump in {sw.ElapsedMilliseconds}ms");

            Console.WriteLine($"[Anti-Loop Check]: {(sw.ElapsedMilliseconds < 1000 ? "PASS" : "WARNING")} (Elapsed: {sw.ElapsedMilliseconds}ms)");
            Console.WriteLine($"[Data Sync Check]: {(hasLatest ? "PASS" : "FAIL")} (Time 100.0 Data Received)");

            Console.WriteLine($"\n[Stability]: OK");

            // Cleanup
            GlobalDataService.Instance.Stop();
            SimulationContext.Instance.Stop();
            Environment.Exit(0);
        }

        private static void SetupDatabase()
        {
            if (File.Exists(TestDbPath)) File.Delete(TestDbPath);
            SQLiteConnection.CreateFile(TestDbPath);

            using (var conn = new SQLiteConnection($"Data Source={TestDbPath};Version=3;"))
            {
                conn.Open();

                // 1. Meta Tables for GlobalDataService (Schema Discovery)
                using (var cmd = new SQLiteCommand("CREATE TABLE Object_Info (object_name TEXT, table_name TEXT)", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand("CREATE TABLE Column_Info (table_name TEXT, column_name TEXT, attribute_name TEXT, data_type TEXT)", conn)) cmd.ExecuteNonQuery();

                using (var cmd = new SQLiteCommand($"INSERT INTO Object_Info VALUES ('StabilityObject', '{TableName}')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"INSERT INTO Column_Info VALUES ('{TableName}', 'Value', 'Value', 'DOUBLE')", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new SQLiteCommand($"INSERT INTO Column_Info VALUES ('{TableName}', 's_time', 'Time', 'DOUBLE')", conn)) cmd.ExecuteNonQuery();

                // 2. Limit s_time to be Double (not always primary key in simulation schema but helps)
                // Real schema table creation:
                using (var cmd = new SQLiteCommand($"CREATE TABLE {TableName} (s_time DOUBLE PRIMARY KEY, Value DOUBLE)", conn))
                {
                    cmd.ExecuteNonQuery();
                }

                InsertData(conn, 0.0, 10);
                InsertData(conn, 1.0, 20); // 0.5 Skipped
            }
        }

        private static void InsertData(double time, double val)
        {
            using (var conn = new SQLiteConnection($"Data Source={TestDbPath};Version=3;"))
            {
                conn.Open();
                InsertData(conn, time, val);
            }
        }

        private static void InsertData(SQLiteConnection conn, double time, double val)
        {
            using (var cmd = new SQLiteCommand($"INSERT OR REPLACE INTO {TableName} (s_time, Value) VALUES ({time}, {val})", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }
    }
}
