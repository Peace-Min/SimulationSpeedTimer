using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace SimulationSpeedTimer
{
    public struct STime
    {
        public uint Sec;
        public uint Usec;
    }

    /// <summary>
    /// 시뮬레이션 전체를 관장하는 컨트롤러 (Singleton)
    /// </summary>
    public class SimulationController : IDisposable
    {
        private static SimulationController _instance;
        public static SimulationController Instance => _instance ?? (_instance = new SimulationController());

        // 설정 보관용 (메인 스레드와 워커 스레드 간 공유되므로 ConcurrentDictionary 혹은 lock 필요. 
        // 여기서는 Start 시점에 복사해서 넘기는 방식 사용)
        private Dictionary<string, DatabaseQueryConfig> _configRegistry = new Dictionary<string, DatabaseQueryConfig>();
        private readonly object _configLock = new object();

        // 현재 실행 중인 메인 시뮬레이션 태스크
        private Task _simulationTask;
        private CancellationTokenSource _cts;

        // 시간 데이터 버퍼 (Producer-Consumer 패턴)
        private BlockingCollection<STime> _timeBuffer;

        /// <summary>
        /// Singleton 생성자
        /// </summary>
        private SimulationController()
        {
            // 앱 종료 시 자동으로 리소스 정리 (WAL Checkpoint 등) 보장
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Dispose();
        }
        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataReceived;

        public void AddService(string id, DatabaseQueryConfig config)
        {
            lock (_configLock)
            {
                _configRegistry[id] = config;
                Console.WriteLine($"[Controller] Service Config Registered: {id}");
            }
        }

        public void RemoveService(string id)
        {
            lock (_configLock)
            {
                _configRegistry.Remove(id);
                Console.WriteLine($"[Controller] Service Config Removed: {id}");
            }
        }



        /// <summary>
        /// 시뮬레이션 시작 (비동기 체이닝)
        /// 이전 작업이 있다면 종료될 때까지 기다렸다가(Queueing) 실행됨
        /// </summary>
        public void Start(double queryInterval = 1.0, double speed = 1.0)
        {
            // 1. 기존 작업 중단 요청
            _cts?.Cancel();

            // 2. 새로운 실행 컨텍스트 준비
            var newCts = new CancellationTokenSource();
            var prevTask = _simulationTask;

            // 3. 현재 설정 스냅샷 생성 (스레드 안전)
            Dictionary<string, DatabaseQueryConfig> configSnapshot;
            lock (_configLock)
            {
                if (_configRegistry.Count == 0) return;
                configSnapshot = new Dictionary<string, DatabaseQueryConfig>(_configRegistry);
            }

            _timeBuffer = new BlockingCollection<STime>(boundedCapacity: 1000);
            _cts = newCts;

            // 4. 태스크 체이닝: 이전 태스크 완료 -> 새 태스크 시작
            _simulationTask = Task.Run(async () =>
            {
                // 이전 태스크가 완전히 정리될 때까지 대기
                if (prevTask != null)
                {
                    try
                    {
                        await prevTask;
                    }
                    catch (Exception) { /* 무시 (Cancellation 등) */ }
                }

                // 여기서부터는 안전하게 단독 실행 보장됨
                RunSimulationLoop(configSnapshot, queryInterval, newCts.Token);
            });
        }

        /// <summary>
        /// 시뮬레이션 메인 루프 (워커 태스크)
        /// </summary>
        private void RunSimulationLoop(Dictionary<string, DatabaseQueryConfig> configs, double queryInterval, CancellationToken token)
        {
            // 큐 대기 중에 이미 취소되었다면 실행하지 않고 종료 (Fast Exit)
            if (token.IsCancellationRequested) return;

            var services = new Dictionary<string, DatabaseQueryService>();
            double nextCheckpoint = queryInterval;

            try
            {
                // 1. 서비스 생성 및 시작
                foreach (var kvp in configs)
                {
                    var service = new DatabaseQueryService(kvp.Key, kvp.Value);
                    // 이벤트 릴레이
                    service.OnDataQueried += (id, data) => OnDataReceived?.Invoke(id, data);
                    service.Start();
                    services[kvp.Key] = service;
                }

                Console.WriteLine($"[Controller] Simulation Started with {services.Count} services.");

                // 2. 시간 수신 루프 (BlockingCollection 소비)
                // ConsumeEnumerable은 컬렉션이 CompleteAdding 되거나 취소될 때까지 블로킹하며 대기
                foreach (var sTime in _timeBuffer.GetConsumingEnumerable(token))
                {
                    double currentTotalSeconds = sTime.Sec + (sTime.Usec / 1_000_000.0);

                    // 쿼리 간격 체크
                    while (currentTotalSeconds >= nextCheckpoint)
                    {
                        double rangeStart = nextCheckpoint - queryInterval;
                        double rangeEnd = nextCheckpoint;

                        foreach (var service in services.Values)
                        {
                            service.EnqueueRangeQuery(rangeStart, rangeEnd);
                        }
                        
                        nextCheckpoint += queryInterval;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 신호
                Console.WriteLine("[Controller] Simulation Loop Canceled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Controller] Error in Loop: {ex.Message}");
            }
            finally
            {
                // 3. 정리 작업 (서비스 종료 -> WAL 체크포인트 -> 리소스 해제)
                CleanupServices(services, configs);
            }
        }

        /// <summary>
        /// 서비스 정리 및 WAL 체크포인트 (단일 진입점)
        /// </summary>
        private void CleanupServices(Dictionary<string, DatabaseQueryService> services, Dictionary<string, DatabaseQueryConfig> configs)
        {
            // 1. 모든 서비스 Stop (읽기 작업 종료 대기)
            Parallel.ForEach(services.Values, service =>
            {
                try { service.Stop(); } catch { }
            });

            // 2. WAL 체크포인트 (DB 파일당 1회)
            // 첫 번째 유효한 DB 경로 추출
            string targetDbPath = null;
            foreach(var cfg in configs.Values)
            {
                if(!string.IsNullOrEmpty(cfg.DatabasePath))
                {
                    targetDbPath = cfg.DatabasePath;
                    break;
                }
            }

            if (targetDbPath != null)
            {
                PerformCheckpoint(targetDbPath);
            }

            // 3. Dispose
            foreach (var service in services.Values)
            {
                try { service.Dispose(); } catch { }
            }
            services.Clear();
            
            Console.WriteLine("[Controller] Cleanup Finished.");
        }

        private void PerformCheckpoint(string dbPath)
        {
            try
            {
                var builder = new System.Data.SQLite.SQLiteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Pooling = false
                };

                using (var conn = new System.Data.SQLite.SQLiteConnection(builder.ToString()))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        cmd.ExecuteNonQuery();
                        // Console.WriteLine($"[Controller] Checkpoint Executed: {dbPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Controller] Checkpoint Failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            Console.WriteLine("[Controller] Stop Requested.");
            // 단순히 취소 토큰만 발동 -> 태스크가 루프 깨고 finally 블록으로 가서 정리함
            _cts?.Cancel();
            _timeBuffer?.CompleteAdding(); // 버퍼 닫기
        }



        public void OnTimeReceived(STime sTime)
        {
            // 외부 스레드에서 들어오는 시간을 버퍼에 넣기만 함 (Non-blocking 권장)
            if (_timeBuffer != null && !_timeBuffer.IsAddingCompleted && !_cts.IsCancellationRequested)
            {
                _timeBuffer.TryAdd(sTime);
            }
        }

        public void Dispose()
        {
            Stop();
            try { _simulationTask?.Wait(3000); } catch { }
            _timeBuffer?.Dispose();
            _cts?.Dispose();
        }
    }
}
