using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB 조회를 위한 서비스 (인스턴스)
    /// 각 차트마다 하나의 인스턴스를 생성하여 독립적인 연결과 쿼리를 관리합니다.
    /// SQLite WAL 모드에서는 여러 연결이 동시에 읽기(SELECT)를 수행해도 안전합니다.
    /// </summary>
    public class DatabaseQueryService : IDisposable
    {
        private ConcurrentQueue<TimeSpan> _queryQueue = new ConcurrentQueue<TimeSpan>();
        private Task _workerTask;
        private CancellationTokenSource _cts;
        private bool _isRunning = false;
        private DatabaseQueryConfig _config;
        private SQLiteConnection _connection;
        private ResolvedQueryInfo _resolvedQuery; // 메타데이터 해석 결과 캐싱
        
        /// <summary>
        /// 서비스 식별자 (예: "Chart1", "MissileTracker")
        /// </summary>
        public string ServiceId { get; private set; }

        /// <summary>
        /// DB 조회 결과를 전달하는 이벤트
        /// 매개변수: (ServiceId, ChartDataPoint)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataQueried;

        /// <summary>
        /// 서비스 실행 상태
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// 현재 큐에 대기 중인 조회 요청 수
        /// </summary>
        public int QueueCount => _queryQueue.Count;

        /// <summary>
        /// 생성자
        /// </summary>
        /// <param name="serviceId">서비스 식별자</param>
        /// <param name="config">DB 조회 설정</param>
        public DatabaseQueryService(string serviceId, DatabaseQueryConfig config)
        {
            ServiceId = serviceId;
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// DB 조회 서비스 시작
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            // 설정 유효성 검증
            if (string.IsNullOrWhiteSpace(_config.DatabasePath))
                throw new ArgumentException("DatabasePath is required", nameof(_config));
            if (string.IsNullOrWhiteSpace(_config.XAxisObjectName))
                throw new ArgumentException("XAxisObjectName is required", nameof(_config));
            if (string.IsNullOrWhiteSpace(_config.XAxisAttributeName))
                throw new ArgumentException("XAxisAttributeName is required", nameof(_config));
            if (string.IsNullOrWhiteSpace(_config.YAxisObjectName))
                throw new ArgumentException("YAxisObjectName is required", nameof(_config));
            if (string.IsNullOrWhiteSpace(_config.YAxisAttributeName))
                throw new ArgumentException("YAxisAttributeName is required", nameof(_config));

            // SQLite 연결 생성 (WAL 모드 최적화)
            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = _config.DatabasePath,
                Pooling = false,           // SQLite는 풀링 불필요
            }.ToString();

            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            // WAL 모드 확인
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                var mode = cmd.ExecuteScalar()?.ToString();
                
                if (mode?.ToLower() != "wal")
                {
                    Console.WriteLine($"[{ServiceId}] [경고] WAL 모드가 아닙니다. 성능 저하 가능.");
                }
            }

            // 메타데이터 해석 (Object_Info, Column_Info에서 실제 테이블명/컬럼명 조회)
            _resolvedQuery = MetadataResolver.Resolve(_config, _connection);
            // Console.WriteLine($"[{ServiceId}] 메타데이터 해석 완료: {_resolvedQuery.XAxisTableName}.{_resolvedQuery.XAxisColumnName} vs {_resolvedQuery.YAxisTableName}.{_resolvedQuery.YAxisColumnName}");

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
        }

        /// <summary>
        /// DB 조회 서비스 정지 및 리소스 정리
        /// </summary>
        public void Stop()
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
                Console.WriteLine($"[{ServiceId}] 연결 종료 중 오류: {ex.Message}");
            }
            _connection = null;

            // 큐 비우기
            while (_queryQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// IDisposable 구현
        /// </summary>
        public void Dispose()
        {
            Stop();
            // 이벤트 핸들러 제거
            OnDataQueried = null;
        }

        /// <summary>
        /// 조회 요청을 큐에 추가
        /// 타이머 Tick 이벤트에서 호출됩니다.
        /// </summary>
        /// <param name="simulationTime">조회할 시뮬레이션 시간 (DB 기본키)</param>
        public void EnqueueQuery(TimeSpan simulationTime)
        {
            _queryQueue.Enqueue(simulationTime);
        }

        /// <summary>
        /// 백그라운드 워커 루프
        /// 큐에서 시간 정보를 디큐하여 DB 조회를 수행합니다.
        /// </summary>
        private void WorkerLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_queryQueue.TryDequeue(out TimeSpan simTime))
                    {
                        // DB 조회 수행 (재시도 포함)
                        var chartData = QueryDatabaseWithRetry(simTime, token);

                        // 조회 결과 이벤트 발생 (데이터가 있을 때만)
                        if (chartData != null)
                        {
                            OnDataQueried?.Invoke(ServiceId, chartData);
                        }
                        // 데이터가 없으면(null) 그냥 무시하고 다음 시간 처리
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
                Console.WriteLine($"Error in DatabaseQueryService[{ServiceId}]: {ex.Message}");
            }
        }

        /// <summary>
        /// 재시도 로직이 포함된 DB 조회 메서드
        /// 데이터가 없으면 설정된 횟수만큼 재시도합니다.
        /// </summary>
        /// <param name="simulationTime">조회할 시간</param>
        /// <param name="token">취소 토큰</param>
        /// <returns>조회된 차트 데이터 포인트 (실패 시 null)</returns>
        private ChartDataPoint QueryDatabaseWithRetry(TimeSpan simulationTime, CancellationToken token)
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
                        // Console.WriteLine($"[{ServiceId}] Success after {attemptCount} attempts for time {simulationTime.TotalSeconds:F2}s");
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
            // Console.WriteLine($"[{ServiceId}] Failed after {attemptCount} attempts for time {simulationTime.TotalSeconds:F2}s");
            return null;
        }

        /// <summary>
        /// 실제 DB 조회를 수행하는 메서드
        /// </summary>
        /// <param name="simulationTime">조회할 시간 (기본키)</param>
        /// <returns>조회된 차트 데이터 포인트</returns>
        private ChartDataPoint QueryDatabase(TimeSpan simulationTime)
        {
            // TimeSpan을 시간 값으로 변환 (s_time 컬럼과 매칭)
            double timeValue = simulationTime.TotalSeconds;

            if (_resolvedQuery.IsSameTable)
            {
                // 같은 테이블: 단일 쿼리로 X, Y 동시 조회
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {_resolvedQuery.XAxisColumnName}, {_resolvedQuery.YAxisColumnName} " +
                        $"FROM {_resolvedQuery.XAxisTableName} " +
                        $"WHERE {_resolvedQuery.XAxisTimeColumnName} = @time";

                    command.Parameters.AddWithValue("@time", timeValue);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new ChartDataPoint
                            {
                                X = Convert.ToDouble(reader.GetValue(0)),
                                Y = Convert.ToDouble(reader.GetValue(1))
                            };
                        }
                    }
                }
            }
            else
            {
                // 다른 테이블: 2개 쿼리로 X, Y 각각 조회
                double? xValue = null;
                double? yValue = null;

                // X축 조회
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {_resolvedQuery.XAxisColumnName} " +
                        $"FROM {_resolvedQuery.XAxisTableName} " +
                        $"WHERE {_resolvedQuery.XAxisTimeColumnName} = @time";

                    command.Parameters.AddWithValue("@time", timeValue);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            xValue = Convert.ToDouble(reader.GetValue(0));
                        }
                    }
                }

                // Y축 조회
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {_resolvedQuery.YAxisColumnName} " +
                        $"FROM {_resolvedQuery.YAxisTableName} " +
                        $"WHERE {_resolvedQuery.YAxisTimeColumnName} = @time";

                    command.Parameters.AddWithValue("@time", timeValue);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            yValue = Convert.ToDouble(reader.GetValue(0));
                        }
                    }
                }

                // 둘 다 조회 성공한 경우에만 반환
                if (xValue.HasValue && yValue.HasValue)
                {
                    return new ChartDataPoint
                    {
                        X = xValue.Value,
                        Y = yValue.Value
                    };
                }
            }

            // 데이터 없음 (재시도 로직이 처리)
            return null;
        }
    }
}
