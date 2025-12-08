using System;
using System.Collections.Generic;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// SimulationController를 사용한 최종 테스트 프로그램
    /// 기존 코드는 이처럼 Controller만 알면 됩니다.
    /// </summary>
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 시뮬레이션 컨트롤러 테스트 (최종 구조) ===\n");

            string dbPath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db";

            // 1. 컨트롤러 생성 (using으로 자동 정리)
            using (var controller = new SimulationController())
            {
                // 2. 차트 설정 준비 (기존 코드에서 넘겨줄 데이터)
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

                // 3. 컨트롤러 초기화
                controller.Initialize(configs);

                // 4. UI 이벤트 연결 (데이터 수신 시 할 일)
                controller.OnDataReceived += (chartId, data) =>
                {
                    // 로그 과다 출력 방지
                    if (SimulationTimer.CurrentTime.TotalSeconds < 0.05)
                    {
                        Console.WriteLine($"[{chartId}] Time: {SimulationTimer.CurrentTime.TotalSeconds:F2}s -> ({data.X:F2}, {data.Y:F2})");
                    }
                };

                try
                {
                    // 5. 시뮬레이션 시작!
                    Console.WriteLine("시뮬레이션 시작 (1배속)...");
                    controller.Start(1.0);

                    Console.WriteLine("\n실행 중... (Enter 키를 누르면 일시정지)\n");
                    Console.ReadLine();

                    // 6. 일시정지 테스트
                    controller.Pause();
                    Console.WriteLine("\n일시정지 됨. (Enter 키를 누르면 재개)\n");
                    Console.ReadLine();

                    // 7. 재개
                    controller.Start(1.0);
                    Console.WriteLine("\n재개 됨. (Enter 키를 누르면 종료)\n");
                    Console.ReadLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"오류: {ex.Message}");
                }
                finally
                {
                    // 8. 종료 (using 블록을 벗어나면 Dispose가 호출되어 Stop도 자동 수행됨)
                    Console.WriteLine("종료 처리 중...");
                }
            }

            Console.WriteLine("\n프로그램 종료.");
        }
    }
}
