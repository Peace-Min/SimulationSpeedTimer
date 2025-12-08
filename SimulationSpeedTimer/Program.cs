using System;
using System.Collections.Generic;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// SimulationController(정적)를 사용한 최종 테스트 프로그램
    /// </summary>
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 시뮬레이션 컨트롤러(Static) 테스트 ===\n");

            string dbPath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db";

            // 1. 차트 설정 준비
            var configs = new List<(string, DatabaseQueryConfig)>
            {
                ("RadarChart", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourDetectRadar", XAxisAttributeName = "distance",
                    YAxisObjectName = "ourDetectRadar", YAxisAttributeName = "position.x"
                }),
                ("LauncherChart", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourDetectRadar", XAxisAttributeName = "distance",
                    YAxisObjectName = "ourLauncher", YAxisAttributeName = "missile_count"
                }),
                ("MissileChart", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourMissile", XAxisAttributeName = "position.x",
                    YAxisObjectName = "ourMissile", YAxisAttributeName = "position.y"
                })
            };

            // 2. 컨트롤러 초기화 (정적 메서드 호출)
            SimulationController.Initialize(configs);

            // 3. UI 이벤트 연결
            SimulationController.OnDataReceived += (chartId, data) =>
            {
                if (SimulationTimer.CurrentTime.TotalSeconds < 0.05)
                {
                    Console.WriteLine($"[{chartId}] Time: {SimulationTimer.CurrentTime.TotalSeconds:F2}s -> ({data.X:F2}, {data.Y:F2})");
                }
            };

            try
            {
                // 4. 시뮬레이션 시작!
                Console.WriteLine("시뮬레이션 시작 (1배속)...");
                SimulationController.Start(1.0);

                Console.WriteLine("\n실행 중... (Enter 키를 누르면 정지)\n");
                Console.ReadLine();

                // 5. 정지 테스트
                SimulationController.Stop();
                Console.WriteLine("\n정지 됨. (Enter 키를 누르면 재시작)\n");
                Console.ReadLine();

                // 6. 재시작 (이벤트가 유지되는지 확인)
                Console.WriteLine("재시작...");
                SimulationController.Start(1.0);
                Console.WriteLine("\n재시작 됨. (Enter 키를 누르면 종료)\n");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"오류: {ex.Message}");
            }
            finally
            {
                // 7. 종료
                Console.WriteLine("종료 처리 중...");
                SimulationController.Stop();
            }

            Console.WriteLine("\n프로그램 종료.");
        }
    }
}
