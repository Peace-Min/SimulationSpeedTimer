using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// MetadataResolver와 DatabaseQueryService 테스트 프로그램
    /// </summary>
    internal class MetadataResolverTest
    {
        static void Main(string[] args)
        {
            // 콘솔 인코딩 설정 (한글 깨짐 방지)
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("=== MetadataResolver 및 다중 쿼리 테스트 ===\n");

            string dbPath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db";

            try
            {
                // 테스트 1: 메타데이터 해석 (기존 테스트)
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine("테스트 1: 메타데이터 해석 검증");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                TestMetadataResolver(dbPath);

                // 테스트 2: 다중 서비스 인스턴스 동시 실행 테스트
                Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine("테스트 2: 다중 서비스 인스턴스 동시 실행 (WAL 모드 검증)");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                TestMultiInstanceQuery(dbPath);

                Console.WriteLine("\n✓ 모든 테스트 완료!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ 오류 발생: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\n아무 키나 누르면 종료...");
            Console.ReadKey();
        }

        /// <summary>
        /// 메타데이터 해석 테스트
        /// </summary>
        static void TestMetadataResolver(string dbPath)
        {
            var config = new DatabaseQueryConfig
            {
                DatabasePath = dbPath,
                XAxisObjectName = "ourDetectRadar",
                XAxisAttributeName = "distance",
                YAxisObjectName = "ourLauncher",
                YAxisAttributeName = "missile_count"
            };

            using (var connection = new SQLiteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                var resolved = MetadataResolver.Resolve(config, connection);

                Console.WriteLine($"입력: {config.XAxisObjectName}.{config.XAxisAttributeName} vs {config.YAxisObjectName}.{config.YAxisAttributeName}");
                Console.WriteLine($"해석: {resolved.XAxisTableName}.{resolved.XAxisColumnName} vs {resolved.YAxisTableName}.{resolved.YAxisColumnName}");
                Console.WriteLine($"같은 테이블: {resolved.IsSameTable}");
            }
        }

        /// <summary>
        /// 다중 서비스 인스턴스 동시 실행 테스트
        /// </summary>
        static void TestMultiInstanceQuery(string dbPath)
        {
            // 3개의 서로 다른 차트 설정 준비
            var configs = new List<(string Id, DatabaseQueryConfig Config)>
            {
                ("Chart1", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourDetectRadar",
                    XAxisAttributeName = "distance",
                    YAxisObjectName = "ourDetectRadar",
                    YAxisAttributeName = "position.x"
                }),
                ("Chart2", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourDetectRadar",
                    XAxisAttributeName = "distance",
                    YAxisObjectName = "ourLauncher",
                    YAxisAttributeName = "missile_count"
                }),
                ("Chart3", new DatabaseQueryConfig
                {
                    DatabasePath = dbPath,
                    XAxisObjectName = "ourMissile",
                    XAxisAttributeName = "position.x",
                    YAxisObjectName = "ourMissile",
                    YAxisAttributeName = "position.y"
                })
            };

            var services = new List<DatabaseQueryService>();
            int dataReceivedCount = 0;
            object lockObj = new object();

            try
            {
                // 1. 서비스 생성 및 시작
                foreach (var item in configs)
                {
                    var service = new DatabaseQueryService(item.Id, item.Config);
                    
                    service.OnDataQueried += (id, data) =>
                    {
                        lock (lockObj)
                        {
                            dataReceivedCount++;
                            Console.WriteLine($"[{id}] 데이터 수신: X={data.X:F2}, Y={data.Y:F2}");
                        }
                    };

                    service.Start();
                    services.Add(service);
                    Console.WriteLine($"[{item.Id}] 서비스 시작됨");
                }

                // 2. 동시에 쿼리 요청 (0.0초, 0.01초, 0.1초)
                double[] testTimes = { 0.0, 0.01, 0.1 };
                
                Console.WriteLine("\n--- 쿼리 요청 시작 ---");
                foreach (var timeVal in testTimes)
                {
                    var timeSpan = TimeSpan.FromSeconds(timeVal);
                    Console.WriteLine($"Time: {timeVal:F2}s 요청 전송");
                    
                    foreach (var service in services)
                    {
                        service.EnqueueQuery(timeSpan);
                    }
                    
                    // 잠시 대기 (실제 시뮬레이션처럼)
                    Thread.Sleep(100);
                }

                // 3. 결과 대기
                Console.WriteLine("\n--- 결과 대기 중 (2초) ---");
                Thread.Sleep(2000);

                Console.WriteLine($"\n총 수신된 데이터 수: {dataReceivedCount}");
            }
            finally
            {
                // 4. 정리
                foreach (var service in services)
                {
                    service.Stop();
                    service.Dispose();
                }
                Console.WriteLine("모든 서비스 종료됨");
            }
        }
    }
}
