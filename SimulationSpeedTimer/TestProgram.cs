using System;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// Stop/Start 재시작 테스트 프로그램
    /// </summary>
    internal class TestProgram
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== SimulationTimer & DatabaseQueryService 재시작 테스트 ===\n");

            // 테스트 1: 기본 시작/정지
            Console.WriteLine("[테스트 1] 기본 시작 -> 정지");
            TestBasicStartStop();
            Thread.Sleep(500);

            // 테스트 2: 여러 번 재시작
            Console.WriteLine("\n[테스트 2] 여러 번 재시작 (5회)");
            TestMultipleRestarts(5);
            Thread.Sleep(500);

            // 테스트 3: 일시정지 후 재시작
            Console.WriteLine("\n[테스트 3] 일시정지 -> 재시작");
            TestPauseResume();
            Thread.Sleep(500);

            // 테스트 4: 배속 변경 재시작
            Console.WriteLine("\n[테스트 4] 배속 변경 재시작");
            TestSpeedChange();

            Console.WriteLine("\n\n=== 모든 테스트 완료 ===");
            Console.WriteLine("아무 키나 누르면 종료합니다...");
            Console.ReadKey();
        }

        static void TestBasicStartStop()
        {
            long tickCount = 0;
            long queryCount = 0;

            SimulationTimer.OnTick += (simTime) =>
            {
                tickCount++;
                DatabaseQueryService.EnqueueQuery(simTime);
            };

            DatabaseQueryService.OnDataQueried += (chartData) =>
            {
                queryCount++;
            };

            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);

            Console.WriteLine("  시작됨...");
            Thread.Sleep(500);

            Console.WriteLine($"  Ticks: {tickCount}, Queries: {queryCount}");

            SimulationTimer.Stop();
            DatabaseQueryService.Stop();

            Console.WriteLine("  정지 완료");
            Console.WriteLine($"  IsRunning - Timer: {SimulationTimer.IsRunning}, DB: {DatabaseQueryService.IsRunning}");
        }

        static void TestMultipleRestarts(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                long tickCount = 0;

                Action<TimeSpan> tickHandler = (simTime) =>
                {
                    tickCount++;
                    DatabaseQueryService.EnqueueQuery(simTime);
                };

                var config = new DatabaseQueryConfig
                {
                    TableName = "TestTable",
                    XAxisColumnName = "Col1",
                    YAxisColumnName = "Col2"
                };

                SimulationTimer.OnTick += tickHandler;
                DatabaseQueryService.Start(config);
                SimulationTimer.Start(1.0);

                Thread.Sleep(200);

                SimulationTimer.Stop();
                DatabaseQueryService.Stop();
                SimulationTimer.OnTick -= tickHandler;

                Console.WriteLine($"  재시작 #{i}: Ticks={tickCount}, Timer.IsRunning={SimulationTimer.IsRunning}, DB.IsRunning={DatabaseQueryService.IsRunning}");

                Thread.Sleep(100);
            }
        }

        static void TestPauseResume()
        {
            long tickCount = 0;

            Action<TimeSpan> tickHandler = (simTime) =>
            {
                tickCount++;
                DatabaseQueryService.EnqueueQuery(simTime);
            };

            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };

            SimulationTimer.OnTick += tickHandler;
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);

            Console.WriteLine("  시작...");
            Thread.Sleep(300);
            Console.WriteLine($"  일시정지 전 Ticks: {tickCount}");

            SimulationTimer.Pause();
            long pausedTicks = tickCount;

            Thread.Sleep(300);
            Console.WriteLine($"  일시정지 중 Ticks: {tickCount} (변화 없어야 함)");

            SimulationTimer.Start(1.0);
            Thread.Sleep(300);
            Console.WriteLine($"  재시작 후 Ticks: {tickCount} (증가해야 함)");

            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            SimulationTimer.OnTick -= tickHandler;

            Console.WriteLine($"  정지 완료");
        }

        static void TestSpeedChange()
        {
            long tickCount = 0;

            Action<TimeSpan> tickHandler = (simTime) =>
            {
                tickCount++;
                DatabaseQueryService.EnqueueQuery(simTime);
            };

            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };

            SimulationTimer.OnTick += tickHandler;
            DatabaseQueryService.Start(config);

            // 1배속
            SimulationTimer.Start(1.0);
            Console.WriteLine("  1배속 시작...");
            Thread.Sleep(500);
            long ticks1x = tickCount;
            Console.WriteLine($"  1배속 Ticks: {ticks1x}");
            SimulationTimer.Stop();

            Thread.Sleep(200);
            tickCount = 0;

            // 2배속
            SimulationTimer.Start(2.0);
            Console.WriteLine("  2배속 시작...");
            Thread.Sleep(500);
            long ticks2x = tickCount;
            Console.WriteLine($"  2배속 Ticks: {ticks2x} (1배속보다 많아야 함)");
            SimulationTimer.Stop();

            DatabaseQueryService.Stop();
            SimulationTimer.OnTick -= tickHandler;

            Console.WriteLine($"  배속 테스트 완료 (1x={ticks1x}, 2x={ticks2x})");
        }
    }
}
