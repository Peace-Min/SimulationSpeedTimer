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
                
                // 테스트 3: 성능 테스트
                TestSparseDataPerformance(dbPath);

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
                    // var timeSpan = TimeSpan.FromSeconds(timeVal); // Not used in RangeQuery
                    Console.WriteLine($"Time: {timeVal:F2}s 요청 전송");
                    
                    foreach (var service in services)
                    {
                        // service.EnqueueQuery(timeSpan); // Deprecated
                        // Use RangeQuery instead. Assuming interval is small for test.
                        service.EnqueueRangeQuery(timeVal, timeVal + 0.01);
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

        /// <summary>
        /// 데이터 공백 구간(Sparse Data)에서의 성능(Fast-Fail) 테스트
        /// 임시 DB를 생성하여 미래 시점의 데이터를 넣고, 과거 구간 조회 시 즉시 리턴하는지 검증
        /// </summary>
        static void TestSparseDataPerformance(string originalDbPath)
        {
            Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine("테스트 3: 데이터 공백 구간 성능 테스트 (Fast-Fail)");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            string tempDbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(originalDbPath), "test_sparse.db");
            
            // 1. 임시 DB 생성 및 데이터 준비
            // 시나리오: 
            // - 데이터는 1000초에 하나만 존재 (MaxTime = 1000)
            // - 0~10초 구간을 조회하면 데이터는 없지만 MaxTime(1000) > EndTime(10) 이므로
            // - 재시도(Sleep) 없이 즉시 빈 결과를 반환해야 함.
            try
            {
                SQLiteConnection.CreateFile(tempDbPath);
                using (var conn = new SQLiteConnection($"Data Source={tempDbPath}"))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            CREATE TABLE Object_Info (object_name TEXT, table_name TEXT);
                            CREATE TABLE Column_Info (table_name TEXT, attribute_name TEXT, column_name TEXT);
                            CREATE TABLE TestTable (s_time REAL, val REAL);
                            
                            INSERT INTO Object_Info VALUES ('TestObj', 'TestTable');
                            INSERT INTO Column_Info VALUES ('TestTable', 'val', 'val');
                            
                            -- 1000초에 데이터 하나 삽입 (이것이 MaxTime이 됨)
                            INSERT INTO TestTable (s_time, val) VALUES (1000.0, 123.45);
                        ";
                        cmd.ExecuteNonQuery();
                    }
                }

                // 2. 서비스 설정
                var config = new DatabaseQueryConfig
                {
                    DatabasePath = tempDbPath,
                    XAxisObjectName = "TestObj",
                    XAxisAttributeName = "s_time", // X축은 시간 그 자체
                    YAxisObjectName = "TestObj",
                    YAxisAttributeName = "val",
                    RetryCount = 5,
                    RetryIntervalMs = 100
                };

                var service = new DatabaseQueryService("PerfTest", config);
                service.Start();

                // 워커 초기화 대기
                Thread.Sleep(500);

                Console.WriteLine("DB 상태: MaxTime=1000.0s");
                Console.WriteLine("요청: 0~10초 구간 (데이터 없음)");
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                // 0초부터 9초까지 10개의 쿼리 요청
                for (int i = 0; i < 10; i++)
                {
                    service.EnqueueRangeQuery(i, i + 1.0);
                }

                // 큐 처리 대기
                while (service.QueueCount > 0)
                {
                    Thread.Sleep(10);
                }
                Thread.Sleep(100); // 처리 완료 여유분

                sw.Stop();
                Console.WriteLine($"총 소요 시간: {sw.ElapsedMilliseconds}ms");
                
                // 분석
                // 기존 로직: 10쿼리 * 5회 * 100ms = 5,000ms (5초)
                // Fast-Fail: 10쿼리 * (DB조회 < 1ms) ~= 10~50ms (매우 빠름)
                if (sw.ElapsedMilliseconds < 1000)
                {
                    Console.WriteLine("✓ 성능 테스트 통과: Fast-Fail 로직이 정상 작동함.");
                    Console.WriteLine($"  (예상 절감 시간: 약 {5000 - sw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    Console.WriteLine("✗ 성능 테스트 실패: 예상보다 오래 걸렸습니다.");
                }

                service.Stop();
                service.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"테스트 중 오류: {ex.Message}");
            }
            finally
            {
                // 임시 파일 정리
                if (System.IO.File.Exists(tempDbPath))
                {
                    try { System.IO.File.Delete(tempDbPath); } catch { }
                }
            }
        }
    }
}
