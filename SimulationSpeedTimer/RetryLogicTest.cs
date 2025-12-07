using System;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 재시도 로직 및 시뮬레이션 종료 감지 테스트
    /// </summary>
    internal class RetryLogicTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 재시도 로직 및 시뮬레이션 종료 감지 테스트 ===\n");

            // Config 설정
            var config = new DatabaseQueryConfig
            {
                TableName = "SimulationData",
                XAxisColumnName = "Temperature",
                YAxisColumnName = "Pressure",
                TimeColumnName = "Time",
                RetryCount = 3,        // 3번 재시도
                RetryIntervalMs = 50   // 50ms 간격
            };

            int successCount = 0;
            int failCount = 0;

            // 이벤트 핸들러 등록
            DatabaseQueryService.OnDataQueried += (chartData) =>
            {
                successCount++;
                Console.WriteLine($"[성공] Time: {SimulationTimer.CurrentTime.TotalSeconds:F2}s, " +
                                $"X: {chartData.X:F2}, Y: {chartData.Y:F2}");
            };

            DatabaseQueryService.OnSimulationEnded += (failedTime, retryCount) =>
            {
                failCount++;
                Console.WriteLine($"\n[시뮬레이션 종료 감지]");
                Console.WriteLine($"  실패 시간: {failedTime.TotalSeconds:F2}초");
                Console.WriteLine($"  재시도 횟수: {retryCount}회");
                Console.WriteLine($"  총 성공: {successCount}회, 실패: {failCount}회");

                // 타이머 정지
                SimulationTimer.Stop();
                DatabaseQueryService.Stop();

                Console.WriteLine("\n서비스가 자동으로 정지되었습니다.");
            };

            SimulationTimer.OnTick += (simTime) =>
            {
                DatabaseQueryService.EnqueueQuery(simTime);
            };

            // 서비스 시작
            Console.WriteLine("서비스 시작...");
            Console.WriteLine($"재시도 설정: {config.RetryCount}회, 간격 {config.RetryIntervalMs}ms\n");

            DatabaseQueryService.Start(config);
            SimulationTimer.Start(1.0);

            // 실행 대기
            Console.WriteLine("실행 중... (Enter 키를 누르면 수동 종료)");
            Console.ReadLine();

            // 수동 종료
            if (SimulationTimer.IsRunning)
            {
                Console.WriteLine("\n수동 종료 중...");
                SimulationTimer.Stop();
                DatabaseQueryService.Stop();
            }

            Console.WriteLine($"\n최종 결과: 성공 {successCount}회, 실패 {failCount}회");
            Console.WriteLine("테스트 완료. 아무 키나 누르면 종료...");
            Console.ReadKey();
        }
    }
}
