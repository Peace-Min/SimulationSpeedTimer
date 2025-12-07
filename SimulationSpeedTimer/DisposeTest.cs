using System;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DatabaseQueryService와 SimulationTimer의 디스포즈 테스트 프로그램
    /// </summary>
    internal class DisposeTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Dispose Test for DatabaseQueryService & SimulationTimer ===\n");

            // 테스트 1: 정상적인 Start -> Stop 시나리오
            Console.WriteLine("Test 1: Normal Start -> Stop");
            TestNormalStartStop();
            Thread.Sleep(500);

            // 테스트 2: Start -> Pause -> Stop 시나리오
            Console.WriteLine("\nTest 2: Start -> Pause -> Stop");
            TestStartPauseStop();
            Thread.Sleep(500);

            // 테스트 3: 여러 번 Start/Stop 반복
            Console.WriteLine("\nTest 3: Multiple Start/Stop cycles");
            TestMultipleStartStop();
            Thread.Sleep(500);

            // 테스트 4: 이벤트 핸들러 메모리 누수 확인
            Console.WriteLine("\nTest 4: Event handler cleanup verification");
            TestEventHandlerCleanup();
            Thread.Sleep(500);

            // 테스트 5: Stop 중복 호출 테스트
            Console.WriteLine("\nTest 5: Multiple Stop calls");
            TestMultipleStopCalls();

            Console.WriteLine("\n=== All tests completed ===");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        static void TestNormalStartStop()
        {
            int tickCount = 0;
            int queryCount = 0;

            // 이벤트 핸들러 등록
            SimulationTimer.OnTick += (time) => tickCount++;
            DatabaseQueryService.OnDataQueried += (chartData) => queryCount++;
            SimulationTimer.OnTick += (time) => DatabaseQueryService.EnqueueQuery(time);

            // 서비스 시작
            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);
            Console.WriteLine("  Started services...");

            Thread.Sleep(100); // 100ms 대기

            // 서비스 정지
            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            Console.WriteLine($"  Stopped services. Ticks: {tickCount}, Queries: {queryCount}");

            // 상태 확인
            Console.WriteLine($"  SimulationTimer.IsRunning: {SimulationTimer.IsRunning}");
            Console.WriteLine($"  DatabaseQueryService.IsRunning: {DatabaseQueryService.IsRunning}");
            Console.WriteLine($"  DatabaseQueryService.QueueCount: {DatabaseQueryService.QueueCount}");
            Console.WriteLine($"  SimulationTimer.CurrentTime: {SimulationTimer.CurrentTime}");

            // 검증
            if (!SimulationTimer.IsRunning && !DatabaseQueryService.IsRunning &&
                DatabaseQueryService.QueueCount == 0 && SimulationTimer.CurrentTime == TimeSpan.Zero)
            {
                Console.WriteLine("  ✓ PASS: All resources properly disposed");
            }
            else
            {
                Console.WriteLine("  ✗ FAIL: Resources not properly disposed");
            }
        }

        static void TestStartPauseStop()
        {
            int tickCount = 0;

            SimulationTimer.OnTick += (time) => tickCount++;
            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(2.0);
            Console.WriteLine("  Started services at 2x speed...");

            Thread.Sleep(50);

            SimulationTimer.Pause();
            Console.WriteLine($"  Paused. Ticks: {tickCount}");
            TimeSpan pausedTime = SimulationTimer.CurrentTime;

            Thread.Sleep(50);

            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            Console.WriteLine($"  Stopped. Time at pause: {pausedTime}, Time after stop: {SimulationTimer.CurrentTime}");

            if (!SimulationTimer.IsRunning && SimulationTimer.CurrentTime == TimeSpan.Zero)
            {
                Console.WriteLine("  ✓ PASS: Pause and Stop working correctly");
            }
            else
            {
                Console.WriteLine("  ✗ FAIL: Pause or Stop not working correctly");
            }
        }

        static void TestMultipleStartStop()
        {
            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };

            for (int i = 0; i < 3; i++)
            {
                DatabaseQueryService.Start(config);
                SimulationTimer.Start(1.0);
                Console.WriteLine($"  Cycle {i + 1}: Started");

                Thread.Sleep(30);

                SimulationTimer.Stop();
                DatabaseQueryService.Stop();
                Console.WriteLine($"  Cycle {i + 1}: Stopped");
            }

            if (!SimulationTimer.IsRunning && !DatabaseQueryService.IsRunning)
            {
                Console.WriteLine("  ✓ PASS: Multiple cycles handled correctly");
            }
            else
            {
                Console.WriteLine("  ✗ FAIL: State inconsistent after multiple cycles");
            }
        }

        static void TestEventHandlerCleanup()
        {
            int tickCount = 0;
            int queryCount = 0;

            // 핸들러 등록
            Action<TimeSpan> tickHandler = (time) => tickCount++;
            Action<ChartDataPoint> queryHandler = (chartData) => queryCount++;

            SimulationTimer.OnTick += tickHandler;
            DatabaseQueryService.OnDataQueried += queryHandler;
            SimulationTimer.OnTick += (time) => DatabaseQueryService.EnqueueQuery(time);

            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);
            Thread.Sleep(50);

            int ticksBeforeStop = tickCount;
            int queriesBeforeStop = queryCount;

            // Stop 호출 (이벤트 핸들러 제거됨)
            SimulationTimer.Stop();
            DatabaseQueryService.Stop();

            Console.WriteLine($"  Ticks before stop: {ticksBeforeStop}, Queries before stop: {queriesBeforeStop}");

            // 다시 시작 (이전 핸들러는 제거되었으므로 카운트가 증가하지 않아야 함)
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);
            Thread.Sleep(50);
            SimulationTimer.Stop();
            DatabaseQueryService.Stop();

            Console.WriteLine($"  Ticks after restart: {tickCount}, Queries after restart: {queryCount}");

            if (tickCount == ticksBeforeStop && queryCount == queriesBeforeStop)
            {
                Console.WriteLine("  ✓ PASS: Event handlers properly cleaned up");
            }
            else
            {
                Console.WriteLine("  ✗ FAIL: Event handlers not cleaned up (memory leak possible)");
            }
        }

        static void TestMultipleStopCalls()
        {
            var config = new DatabaseQueryConfig
            {
                TableName = "TestTable",
                XAxisColumnName = "Col1",
                YAxisColumnName = "Col2"
            };
            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);
            Thread.Sleep(30);

            // 여러 번 Stop 호출
            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            Console.WriteLine("  First Stop called");

            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            Console.WriteLine("  Second Stop called");

            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
            Console.WriteLine("  Third Stop called");

            if (!SimulationTimer.IsRunning && !DatabaseQueryService.IsRunning)
            {
                Console.WriteLine("  ✓ PASS: Multiple Stop calls handled safely");
            }
            else
            {
                Console.WriteLine("  ✗ FAIL: Multiple Stop calls caused issues");
            }
        }
    }
}
