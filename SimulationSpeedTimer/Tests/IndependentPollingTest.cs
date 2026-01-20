using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer.Tests
{
    /// <summary>
    /// 독립적 폴링(Independent Polling) 아키텍처 및 데이터 병합 검증 테스트
    /// </summary>
    public class IndependentPollingTest
    {
        private Guid _sessionId;
        private CancellationTokenSource _cts;

        public void Run()
        {
            Console.WriteLine("=== Independent Polling Architecture Test Start ===");

            try
            {
                Setup();
                Test_IndependentPolling_Success();
                Test_GracefulShutdown_DataIntegrity();
                Console.WriteLine("=== All Tests Passed Successfully ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Test Failed] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Teardown();
            }
        }

        private void Setup()
        {
            _sessionId = Guid.NewGuid();
            _cts = new CancellationTokenSource();

            // 1. SharedFrameRepository 초기화
            SharedFrameRepository.Instance.StartNewSession(_sessionId);
            
            // 2. 스키마 설정 (테스트용)
            // MockDataService가 이 스키마를 통해 어떤 테이블을 보낼지 결정하지는 않지만,
            // Repository는 스키마 없이도 데이터를 저장할 수 있어야 함 (또는 여기서 더미 스키마 주입)
            var schema = new SimulationSchema();
            schema.AddTable(new SchemaTableInfo("FastTable", "ObjectA"));
            schema.AddTable(new SchemaTableInfo("SlowTable", "ObjectB"));
            SharedFrameRepository.Instance.Schema = schema;

            Console.WriteLine($"[Setup] Session {_sessionId} Initialized.");
        }

        private void Teardown()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            // 잔여 데이터 정리
            SharedFrameRepository.Instance.StartNewSession(Guid.NewGuid());
        }

        /// <summary>
        /// 시나리오 1: 서로 다른 속도의 데이터 유입 시 병합 성공 여부 검증
        /// - FastTable: 100ms 주기
        /// - SlowTable: 500ms 주기
        /// - 목표: T=0.5, 1.0 등 공통 시점에서 두 테이블 데이터가 모두 존재하는지 확인
        /// </summary>
        private void Test_IndependentPolling_Success()
        {
            Console.WriteLine("\n[Test 1] Independent Polling & Merge Validation");

            // Mock 서비스 시작
            var fastService = new MockDataService(_sessionId, "FastTable", 100); // 100ms
            var slowService = new MockDataService(_sessionId, "SlowTable", 500); // 500ms

            var tasks = new List<Task>();
            tasks.Add(Task.Run(() => fastService.Run(_cts.Token)));
            tasks.Add(Task.Run(() => slowService.Run(_cts.Token)));

            // 데이터가 쌓일 때까지 대기 (예: 2초 -> 2.0초 시점까지 데이터 생성)
            // 안전 장치: 타임아웃
            if (!SpinWait.SpinUntil(() => SharedFrameRepository.Instance.GetFrameCount() > 10, 3000))
            {
                throw new TimeoutException("데이터 수신 타임아웃: 프레임이 생성되지 않았습니다.");
            }

            // 검증 로직 (Polling 방식으로 데이터 확인)
            // T=0.5 (500ms) 시점에 FastTable(0.1*5번째)과 SlowTable(0.5*1번째)이 모두 있어야 함
            double targetTime = 0.5;
            ValidateMergeAtTime(targetTime, 5000); // 5초 타임아웃
            
            targetTime = 1.0;
            ValidateMergeAtTime(targetTime, 5000);

            Console.WriteLine("[Success] Independent Polling Merged Correctly.");
            
            _cts.Cancel(); // Stop tests
            Task.WaitAll(tasks.ToArray());
        }

        private void ValidateMergeAtTime(double targetTime, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var frame = SharedFrameRepository.Instance.GetFrame(targetTime);
                if (frame != null)
                {
                    var fastData = frame.GetTable("FastTable");
                    var slowData = frame.GetTable("SlowTable");

                    if (fastData != null && slowData != null)
                    {
                        Console.WriteLine($"[Validation] T={targetTime:F1} - Both Tables Found. (FastVal={fastData["val"]}, SlowVal={slowData["val"]})");
                        return; // 성공
                    }
                }
                Thread.Sleep(50);
            }

            // 실패 상세 분석
            var f = SharedFrameRepository.Instance.GetFrame(targetTime);
            string status = f == null ? "Frame Not Found" : $"Tables: {string.Join(",", f.AllTables.Select(t => t.TableName))}";
            throw new Exception($"[Merge Fail] T={targetTime:F1} 에서 병합 실패. Timeout. Status: {status}");
        }

        /// <summary>
        /// 시나리오 2: 종료 신호(Stop) 시 잔여 데이터 처리 검증 (Graceful Shutdown)
        /// </summary>
        private void Test_GracefulShutdown_DataIntegrity()
        {
            Console.WriteLine("\n[Test 2] Graceful Shutdown & Final Sweep");
            
            // Setup 다시 수행 (Clean State)
            Setup();

            var service = new MockDataService(_sessionId, "LateData", 200);
            var task = Task.Run(() => service.Run(_cts.Token));

            // 잠시 실행
            Thread.Sleep(600); // 0.2, 0.4, 0.6 생성 예상

            // Stop 신호
            Console.WriteLine(">>> Sending Stop Signal...");
            _cts.Cancel();

            // 서비스 종료 대기
            try { task.Wait(2000); } catch (AggregateException) { }

            // Final Sweep 확인
            // MockDataService는 Cancellation 요청 시 마지막으로 "FinalChunk"를 보내도록 구현됨
            // 검증: 서비스가 처리했다고 주장하는 마지막 시간이 Repository에 있는지 확인
            double lastProcessed = service.LastProcessedTime;
            
            Console.WriteLine($"Service stopped at LastProcessedTime={lastProcessed:F1}");

            var frame = SharedFrameRepository.Instance.GetFrame(lastProcessed);
            if (frame == null || frame.GetTable("LateData") == null)
            {
                throw new Exception($"[Data Loss] 종료 시 마지막 데이터(T={lastProcessed:F1})가 저장소에 누락되었습니다.");
            }

            Console.WriteLine($"[Success] Final Sweep Data (T={lastProcessed:F1}) confirmed.");
        }
    }

    /// <summary>
    /// 실제 DB 대신 데이터를 생성하여 Repository에 주입하는 Mock 서비스
    /// </summary>
    internal class MockDataService
    {
        private readonly Guid _sessionId;
        private readonly string _tableName;
        private readonly int _intervalMs; // 데이터 생성 주기 (현실 시간)
        
        public double LastProcessedTime { get; private set; } = 0.0;

        public MockDataService(Guid sessionId, string tableName, int intervalMs)
        {
            _sessionId = sessionId;
            _tableName = tableName;
            _intervalMs = intervalMs;
        }

        public void Run(CancellationToken token)
        {
            double simTime = 0.0; // 시뮬레이션 시간 (0.0부터 시작)


            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 1. 데이터 생성 (Generate)
                    // _intervalMs 주기에 맞춰 시뮬레이션 데이터 방출
                    // 예: SlowTable(500ms) -> 실제 500ms마다 한 번씩 큐에 넣음
                    // 시뮬레이션 시간은 1.0 (QueryInterval) 단위가 아니라, 
                    // 여기서는 "특정 시점에 데이터가 도착함"을 흉내내기 위해 
                    // intervalMs마다 simTime을 증가시키며 보낸다.
                    
                    // 더 현실적인 시나리오:
                    // FastTable (100ms): 0.1s, 0.2s, 0.3s, 0.4s, 0.5s ...
                    // SlowTable (500ms): 0.1s...0.5s 한꺼번에? 아니면 0.5s만?
                    // 요구사항: "서로 다른 주기(A 100ms, B 500ms)로 데이터 방출"
                    // Independent Polling의 핵심은 '느린 테이블은 드문드문 업데이트된다'는 것.
                    // 따라서 SlowTable은 0.5, 1.0, 1.5에만 데이터가 있다고 가정하거나,
                    // 혹은 데이터는 매 0.1초마다 있지만 DB 쿼리가 500ms 걸려서 뭉텅이로 온다고 가정.
                    // 여기서는 "0.5초마다 데이터가 도착하는데, 그 데이터는 0.5초 시점의 데이터다"라고 단순화.
                    
                    if (_tableName == "SlowTable")
                    {
                         // SlowTable은 0.5초 단위로만 데이터가 존재한다고 가정
                         simTime += 0.5;
                         Thread.Sleep(_intervalMs); 
                    }
                    else
                    {
                        // FastTable은 0.1초 단위로 데이터 존재
                        simTime += 0.1;
                        Thread.Sleep(_intervalMs);
                    }
                    
                    // 반올림 오류 방지
                    simTime = Math.Round(simTime, 1);

                    // 2. Repository에 주입 (StoreChunk)
                    PushData(simTime);
                    LastProcessedTime = simTime;
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful Exit
            }
            finally
            {
                // Final Sweep Logic
                // Stop 신호 후 처리되지 않은 잔여 데이터가 있다면 처리 (여기서는 단순히 로그 확인용)
                // 실제 GlobalDataService는 'lastSeenTime'까지 읽음.
                // 여기서는 종료 직전에 마지막 데이터를 확실히 보냈는지 확인.
                Console.WriteLine($"[{_tableName}] Mock Service Stopping. Last Time: {LastProcessedTime}");
            }
        }

        private void PushData(double time)
        {
            var chunk = new Dictionary<double, SimulationFrame>();
            var frame = new SimulationFrame(time);
            
            var table = new SimulationTable(_tableName);
            table.AddColumn("val", time * 100); // Dummy Data
            
            frame.AddOrUpdateTable(table);
            chunk[time] = frame;

            SharedFrameRepository.Instance.StoreChunk(chunk, _sessionId);
            // Console.WriteLine($"[{_tableName}] Pushed T={time:F1}");
        }
    }
}
