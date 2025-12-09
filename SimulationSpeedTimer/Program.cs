using System;
using System.Collections.Generic;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// SimulationController를 사용한 최종 테스트 프로그램
    /// Pause/Resume 기능을 포함한 인터랙티브 테스트
    /// </summary>
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 시뮬레이션 컨트롤러 테스트 (Interactive) ===\n");
            Console.WriteLine("조작 설명:");
            Console.WriteLine("  [S] : 시뮬레이션 시작 / 재개 (Start/Resume)");
            Console.WriteLine("  [P] : 시뮬레이션 일시 정지 (Pause)");
            Console.WriteLine("  [T] : 시뮬레이션 정지 (Stop - 초기화)");
            Console.WriteLine("  [Q] : 프로그램 종료");
            Console.WriteLine("------------------------------------------------\n");

            // DB 경로 설정 (사용자 환경에 맞게 수정 필요)
            // 현재 DB 파일이 없으므로 테스트를 위해 비워둡니다.
            string dbPath = @"c:\Users\minph\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db";

            using (var controller = new SimulationController())
            {
                var configs = new List<(string, DatabaseQueryConfig)>();

                // DB 파일이 존재할 경우에만 설정을 추가하세요.
                if (System.IO.File.Exists(dbPath))
                {
                    configs.Add(("RadarChart", new DatabaseQueryConfig
                    {
                        DatabasePath = dbPath,
                        XAxisObjectName = "ourDetectRadar",
                        XAxisAttributeName = "distance",
                        YAxisObjectName = "ourDetectRadar",
                        YAxisAttributeName = "position.x"
                    }));
                    // 필요한 만큼 추가...
                }
                else
                {
                    Console.WriteLine($"[알림] DB 파일을 찾을 수 없어 DB 쿼리 서비스는 비활성화됩니다.\n경로: {dbPath}\n");
                }

                //controller.Initialize(configs);

                controller.OnDataReceived += (chartId, data) =>
                {
                    // 데이터 수신 시 로그 (너무 빠르면 스킵할 수도 있음)
                    if (SimulationTimer.CurrentTime.TotalMilliseconds % 100 < 20)
                    {
                        Console.WriteLine($"[{chartId}] Data: ({data.X:F2}, {data.Y:F2}) at {SimulationTimer.CurrentTime.TotalSeconds:F2}s");
                    }
                };

                bool appRunning = true;
                while (appRunning)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true).Key;
                        switch (key)
                        {
                            case ConsoleKey.S:
                                Console.WriteLine("\n[User Input] Start/Resume");
                                try
                                {
                                    controller.Start(speed: 1.0);
                                    Console.WriteLine("시뮬레이션이 실행 중입니다...");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error starting: {ex.Message}");
                                }
                                break;

                            case ConsoleKey.P:
                                Console.WriteLine("\n[User Input] Pause");
                                controller.Pause();
                                Console.WriteLine($"일시 정지됨. 현재 시간: {SimulationTimer.CurrentTime.TotalSeconds:F2}s");
                                break;

                            case ConsoleKey.T:
                                Console.WriteLine("\n[User Input] Stop");
                                controller.Stop();
                                Console.WriteLine("정지(초기화)됨.");
                                break;

                            case ConsoleKey.Q:
                                Console.WriteLine("\n[User Input] Quit");
                                appRunning = false;
                                break;
                        }
                    }

                    // 상태 표시 업데이트 (1초마다)
                    if (SimulationTimer.IsRunning && SimulationTimer.CurrentTime.TotalMilliseconds % 1000 < 20)
                    {
                        // 콘솔이 너무 어지럽지 않게... 필요하면 주석 해제
                        // Console.WriteLine($"[Status] Time: {SimulationTimer.CurrentTime:mm\\:ss\\.ff}");
                    }

                    Thread.Sleep(50);
                }

                controller.Stop();
            }

            Console.WriteLine("\n프로그램 종료.");
        }
    }
}
