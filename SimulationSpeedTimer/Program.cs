using System;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DatabaseQueryService 메인 테스트 프로그램
    /// </summary>
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== DatabaseQueryService + SimulationTimer 테스트 ===\n");

            // Config 설정
            var config = new DatabaseQueryConfig
            {
                DatabasePath = @"C:\Data\simulation.db",  // SQLite DB 경로
                TableName = "SimulationData",
                XAxisColumnName = "Temperature",
                YAxisColumnName = "Pressure",
                TimeColumnName = "Time",
                RetryCount = 3,
                RetryIntervalMs = 50
            };

            int dataCount = 0;
            int endCount = 0;

            // 데이터 조회 성공 이벤트
            DatabaseQueryService.OnDataQueried += (chartData) =>
            {
                dataCount++;
                if (dataCount <= 10 || dataCount % 10 == 0)
                {
                    Console.WriteLine($"[{dataCount}] Time: {SimulationTimer.CurrentTime.TotalSeconds:F2}s, " +
                                    $"X: {chartData.X:F2}, Y: {chartData.Y:F2}");
                }
            };

            // 시뮬레이션 종료 감지 이벤트
            DatabaseQueryService.OnSimulationEnded += (failedTime, retryCount) =>
            {
                endCount++;
                Console.WriteLine($"\n[시뮬레이션 종료 감지]");
                Console.WriteLine($"  실패 시간: {failedTime.TotalSeconds:F2}초");
                Console.WriteLine($"  재시도 횟수: {retryCount}회");

                // 타이머 정지
                SimulationTimer.Stop();
                DatabaseQueryService.Stop();
            };

            // 타이머 Tick 이벤트
            SimulationTimer.OnTick += (simTime) =>
            {
                DatabaseQueryService.EnqueueQuery(simTime);
            };

            try
            {
                Console.WriteLine("테스트용 데이터베이스 생성 중...\n");
                //CreateTestDatabase.Create(config.DatabasePath, durationSeconds: 10.0);

                Console.WriteLine("서비스 시작...");
                Console.WriteLine($"설정: RetryCount={config.RetryCount}, Interval={config.RetryIntervalMs}ms\n");

                DatabaseQueryService.Start(config);
                SimulationTimer.Start(1.0);  // 1배속

                Console.WriteLine("실행 중... (Enter 키를 누르면 종료)\n");
                Console.ReadLine();

                // 수동 종료
                if (SimulationTimer.IsRunning)
                {
                    Console.WriteLine("\n수동 종료 중...");
                    SimulationTimer.Stop();
                    DatabaseQueryService.Stop();
                }

                Console.WriteLine($"\n=== 최종 결과 ===");
                Console.WriteLine($"조회 성공: {dataCount}회");
                Console.WriteLine($"종료 감지: {endCount}회");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[오류] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\n아무 키나 누르면 종료...");
            Console.ReadKey();
        }
    }
}
