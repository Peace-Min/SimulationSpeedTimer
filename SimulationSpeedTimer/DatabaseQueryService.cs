using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB 조회를 위한 정적 서비스
    /// 타이머 Tick에서 시간 정보를 큐에 인큐하면, 백그라운드 워커가 디큐하여 DB 조회를 수행합니다.
    /// SQLite WAL 모드 최적화: 연결 재사용 방식
    /// </summary>
    public static class DatabaseQueryService
    {
        private static ConcurrentQueue<TimeSpan> _queryQueue = new ConcurrentQueue<TimeSpan>();
        private static Task _workerTask;
        private static CancellationTokenSource _cts;
        private static bool _isRunning = false;
        private static DatabaseQueryConfig _config;
        private static SqliteConnection _connection;

        /// <summary>
        /// DB 조회 결과를 전달하는 이벤트
        /// 매개변수: ChartDataPoint (X, Y 값)
        /// </summary>
        public static event Action<ChartDataPoint> OnDataQueried;

        /// <summary>
        /// 시뮬레이션 종료 감지 이벤트
        /// 재시도 횟수를 초과하여 데이터를 찾지 못한 경우 발생
        /// 매개변수: (실패한 시간, 재시도 횟수)
        /// </summary>
        public static event Action<TimeSpan, int> OnSimulationEnded;

        /// <summary>
        /// 서비스 실행 상태
        /// </summary>
        public static bool IsRunning => _isRunning;

        /// <summary>
        /// 현재 큐에 대기 중인 조회 요청 수
        /// </summary>
        public static int QueueCount => _queryQueue.Count;

        /// <summary>
        /// DB 조회 서비스 시작
        /// </summary>
        /// <param name="config">DB 조회 설정 정보</param>
        public static void Start(DatabaseQueryConfig config)
        {
            if (_isRunning) return;

            _config = config ?? throw new ArgumentNullException(nameof(config));

            // 설정 유효성 검証
            if (string.IsNullOrWhiteSpace(_config.DatabasePath))
                throw new ArgumentException("DatabasePath is required", nameof(config));
            if (string.IsNullOrWhiteSpace(_config.TableName))
                throw new ArgumentException("TableName is required", nameof(config));
            if (string.IsNullOrWhiteSpace(_config.XAxisColumnName))
                throw new ArgumentException("XAxisColumnName is required", nameof(config));
            if (string.IsNullOrWhiteSpace(_config.YAxisColumnName))
                throw new ArgumentException("YAxisColumnName is required", nameof(config));

            // SQLite 연결 생성 (WAL 모드 최적화)
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _config.DatabasePath,
                //Read = true,           // Read 전용
                Pooling = false,           // SQLite는 풀링 불필요
                //JournalMode = SQLiteJournalModeEnum.Wal  // WAL 모드 명시
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            _connection.Open();

            // WAL 모드 확인
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                var mode = cmd.ExecuteScalar()?.ToString();
                Console.WriteLine($"[DB] Journal Mode: {mode}");

                if (mode?.ToLower() != "wal")
                {
                    Console.WriteLine("[경고] WAL 모드가 아닙니다. 성능 저하 가능.");
                }
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
        }

        /// <summary>
        /// DB 조회 서비스 정지
        /// </summary>
        public static void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            // 취소 요청
            _cts?.Cancel();

            // 워커 태스크 종료 대기
            try
            {
                _workerTask?.Wait(1000); // 1초 타임아웃
            }
            catch (AggregateException) { }

            // Task 디스포즈
            try
            {
                _workerTask?.Dispose();
            }
            catch { }
            _workerTask = null;

            // CancellationTokenSource 디스포즈
            _cts?.Dispose();
            _cts = null;

            // SQLite 연결 닫기
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] 연결 종료 중 오류: {ex.Message}");
            }
            _connection = null;

            // 큐 비우기
            while (_queryQueue.TryDequeue(out _)) { }

            // 이벤트 핸들러 모두 제거 (메모리 누수 방지)
            OnDataQueried = null;
            OnSimulationEnded = null;
        }

        /// <summary>
        /// 조회 요청을 큐에 추가
        /// 타이머 Tick 이벤트에서 호출됩니다.
        /// </summary>
        /// <param name="simulationTime">조회할 시뮬레이션 시간 (DB 기본키)</param>
        public static void EnqueueQuery(TimeSpan simulationTime)
        {
            _queryQueue.Enqueue(simulationTime);
        }

        /// <summary>
        /// 백그라운드 워커 루프
        /// 큐에서 시간 정보를 디큐하여 DB 조회를 수행합니다.
        /// </summary>
        private static void WorkerLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_queryQueue.TryDequeue(out TimeSpan simTime))
                    {
                        // DB 조회 수행 (재시도 포함)
                        var chartData = QueryDatabaseWithRetry(simTime, token);

                        // 조회 결과 이벤트 발생
                        if (chartData != null)
                        {
                            OnDataQueried?.Invoke(chartData);
                        }
                        else
                        {
                            // 재시도 실패 -> 시뮬레이션 종료로 판단
                            OnSimulationEnded?.Invoke(simTime, _config.RetryCount);

                            // 서비스 자동 정지 (선택사항)
                            // Stop();
                            // break;
                        }
                    }
                    else
                    {
                        // 큐가 비어있으면 잠시 대기 (CPU 사용률 절약)
                        Thread.Sleep(1);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // 정상 종료
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in DatabaseQueryService: {ex.Message}");
            }
        }

        /// <summary>
        /// 재시도 로직이 포함된 DB 조회 메서드
        /// 데이터가 없으면 설정된 횟수만큼 재시도합니다.
        /// </summary>
        /// <param name="simulationTime">조회할 시간</param>
        /// <param name="token">취소 토큰</param>
        /// <returns>조회된 차트 데이터 포인트 (실패 시 null)</returns>
        private static ChartDataPoint QueryDatabaseWithRetry(TimeSpan simulationTime, CancellationToken token)
        {
            int attemptCount = 0;
            int maxAttempts = _config.RetryCount + 1; // 첫 시도 + 재시도 횟수

            while (attemptCount < maxAttempts && !token.IsCancellationRequested)
            {
                attemptCount++;

                // DB 조회 시도
                var result = QueryDatabase(simulationTime);

                if (result != null)
                {
                    // 성공
                    if (attemptCount > 1)
                    {
                        // 재시도 후 성공한 경우 로그 출력 (선택사항)
                        Console.WriteLine($"[DB Query] Success after {attemptCount} attempts for time {simulationTime.TotalSeconds:F2}s");
                    }
                    return result;
                }

                // 마지막 시도가 아니면 대기 후 재시도
                if (attemptCount < maxAttempts)
                {
                    Thread.Sleep(_config.RetryIntervalMs);
                }
            }

            // 모든 재시도 실패
            Console.WriteLine($"[DB Query] Failed after {attemptCount} attempts for time {simulationTime.TotalSeconds:F2}s - Simulation may have ended");
            return null;
        }

        /// <summary>
        /// 실제 DB 조회를 수행하는 메서드
        /// </summary>
        /// <param name="simulationTime">조회할 시간 (기본키)</param>
        /// <returns>조회된 차트 데이터 포인트</returns>
        private static ChartDataPoint QueryDatabase(TimeSpan simulationTime)
        {
            // TimeSpan을 0.01초 단위 문자열로 변환 (예: "0.01", "0.02", "1.23")
            string timeKey = simulationTime.TotalSeconds.ToString("F2");

            // SQLite 연결 재사용 방식 (WAL 모드 최적화)
            using (var command = _connection.CreateCommand())
            {
                command.CommandText =
                    $"SELECT {_config.XAxisColumnName}, {_config.YAxisColumnName} " +
                    $"FROM {_config.TableName} " +
                    $"WHERE {_config.TimeColumnName} = @time";

                command.Parameters.AddWithValue("@time", timeKey);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new ChartDataPoint
                        {
                            X = reader.GetDouble(0),
                            Y = reader.GetDouble(1)
                        };
                    }
                }
            }

            // 데이터 없음 (재시도 로직이 처리)
            return null;
        }

        /// <summary>
        /// TimeSpan을 0.01초 단위 문자열로 변환
        /// </summary>
        /// <param name="time">변환할 시간</param>
        /// <returns>0.01초 단위 문자열 (예: "0.01", "1.23")</returns>
        private static string ConvertToTimeKey(TimeSpan time)
        {
            return time.TotalSeconds.ToString("F2");
        }
    }
}
