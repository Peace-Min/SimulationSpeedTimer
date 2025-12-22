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
        private ConcurrentQueue<(double Start, double End)> _queryQueue = new ConcurrentQueue<(double Start, double End)>();
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

            // 메타데이터 해석은 Worker thread에서 수행 (테이블 생성 대기)

            // Console.WriteLine($"[{ServiceId}] 메타데이터 해석 완료: {_resolvedQuery.XAxisTableName}.{_resolvedQuery.XAxisColumnName} vs {_resolvedQuery.YAxisTableName}.{_resolvedQuery.YAxisColumnName}");

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
        }

        /// <summary>
        /// DB 조회 서비스 정지 및 리소스 정리
        /// </summary>
        /// <summary>
        /// DB 조회 서비스 정지 및 리소스 정리
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            // 1. 작업 취소 요청
            _cts?.Cancel();

            // 2. 큐 즉시 비우기 (이전 시뮬레이션 데이터 잔여물 제거)
            // Stop 시점의 남은 작업은 의미 없으므로 모두 버림
            while (_queryQueue.TryDequeue(out _)) { }

            // 3. 워커 태스크 종료 대기 (최대 2초)
            try
            {
                _workerTask?.Wait(2000);
            }
            catch (AggregateException) { }

            // 4. 리소스 정리
            try { _workerTask?.Dispose(); } catch { }
            _workerTask = null;

            _cts?.Dispose();
            _cts = null;

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
        /// 지정된 범위의 데이터를 조회하도록 큐에 요청을 추가
        /// </summary>
        public void EnqueueRangeQuery(double start, double end)
        {
            _queryQueue.Enqueue((start, end));
        }

        /// <summary>
        /// 백그라운드 워커 루프
        /// 큐에서 시간 정보를 디큐하여 DB 조회를 수행합니다.
        /// </summary>
        private void WorkerLoop(CancellationToken token)
        {
            try
            {
                // 메타데이터 해석 (테이블이 생성될 때까지 대기)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_resolvedQuery == null && !token.IsCancellationRequested)
                {
                    // 1. 테이블 존재 여부 확인 (예외 발생 방지)
                    if (!MetadataResolver.AreMetadataTablesReady(_connection, _config))
                    {
                        Thread.Sleep(100); // 테이블 생성 대기
                        continue;
                    }

                    try
                    {
                        _resolvedQuery = MetadataResolver.Resolve(_config, _connection);
                    }
                    catch (InvalidOperationException)
                    {
                        // 테이블은 있으나 해당 객체/컬럼 정보가 아직 없는 경우
                        Thread.Sleep(100);
                    }
                    catch (Exception ex)
                    {
                        // 기타 오류
                        Console.WriteLine($"[{ServiceId}] Metadata Resolve Error: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }
                sw.Stop();
                Console.WriteLine($"[{ServiceId}] Metadata Resolve Time: {sw.ElapsedMilliseconds}ms");

                while (!token.IsCancellationRequested)
                {
                    if (_queryQueue.TryDequeue(out var range))
                    {
                        // DB 조회 수행 (재시도 포함)
                        var chartDataList = QueryDatabaseRangeWithRetry(range.Start, range.End, token);

                        // 조회 결과 이벤트 발생 (데이터가 있을 때만)
                        if (chartDataList != null)
                        {
                            // TODO: 리스트 전체를 넘기거나 개별 포인트로 넘겨야 함. 
                            // 기존 인터페이스 유지를 위해 개별 포인트로 순차 발생 
                            // (추후 성능을 위해 List<ChartDataPoint> 전달로 변경 권장)
                            foreach (var point in chartDataList)
                            {
                                OnDataQueried?.Invoke(ServiceId, point);
                            }
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
                Console.WriteLine($"Error in DatabaseQueryService[{ServiceId}]: {ex.Message}");
            }
        }

        /// <summary>
        /// 재시도 로직이 포함된 DB 범위 조회 메서드
        /// </summary>
        private System.Collections.Generic.List<ChartDataPoint> QueryDatabaseRangeWithRetry(double start, double end, CancellationToken token)
        {
            int attemptCount = 0;
            int maxAttempts = _config.RetryCount + 1;

            while (attemptCount < maxAttempts && !token.IsCancellationRequested)
            {
                attemptCount++;

                var result = QueryDatabaseRange(start, end);

                if (result != null && result.Count > 0)
                {
                    return result;
                }

                // 데이터가 없을 때: 재시도 여부 결정 (Fast-Fail 로직)
                // DB에 기록된 최신 시간이 현재 요청 구간보다 뒤에 있다면,
                // 이 구간은 데이터가 없는 구간으로 확정하고 재시도 없이 종료
                double maxTime = GetMaxTimeFromDB();
                if (maxTime >= end) // end보다 크거나 같으면 이미 지나간 구간
                {
                    return null;
                }

                if (attemptCount < maxAttempts)
                {
                    Thread.Sleep(_config.RetryIntervalMs);
                }
            }
            return null;
        }

        /// <summary>
        /// DB에 기록된 가장 최신 시간을 조회 (재시도 판단용)
        /// </summary>
        private double GetMaxTimeFromDB()
        {
            try
            {
                double maxX = -1.0;
                double maxY = -1.0;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = $"SELECT MAX({_resolvedQuery.XAxisTimeColumnName}) FROM {_resolvedQuery.XAxisTableName}";
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        maxX = Convert.ToDouble(result);
                    }
                }

                // 같은 테이블이면 X만 확인해도 충분
                if (_resolvedQuery.IsSameTable)
                {
                    return maxX;
                }

                // 다른 테이블이면 Y축 테이블도 확인
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = $"SELECT MAX({_resolvedQuery.YAxisTimeColumnName}) FROM {_resolvedQuery.YAxisTableName}";
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        maxY = Convert.ToDouble(result);
                    }
                }

                // 둘 중 더 많이 진행된 시간을 반환 (Fast-Fail을 위해)
                // 예: X는 10초, Y는 15초까지 기록됨 -> 현재 12초 조회 중 -> Y가 이미 지났으므로 12초는 데이터 없는 구간임
                return Math.Max(maxX, maxY);
            }
            catch
            {
                // 오류 발생 시 무시
            }
            return -1.0;
        }

        /// <summary>
        /// 실제 DB 조회를 수행하는 메서드
        /// </summary>
        /// <param name="simulationTime">조회할 시간 (기본키)</param>
        /// <returns>조회된 차트 데이터 포인트</returns>
        private System.Collections.Generic.List<ChartDataPoint> QueryDatabaseRange(double start, double end)
        {
            var results = new System.Collections.Generic.List<ChartDataPoint>();

            if (_resolvedQuery.IsSameTable)
            {
                using (var command = _connection.CreateCommand())
                {
                    // WHERE start <= time < end
                    command.CommandText =
                        $"SELECT {_resolvedQuery.XAxisColumnName}, {_resolvedQuery.YAxisColumnName} " +
                        $"FROM {_resolvedQuery.XAxisTableName} " +
                        $"WHERE {_resolvedQuery.XAxisTimeColumnName} >= @start AND {_resolvedQuery.XAxisTimeColumnName} < @end";

                    command.Parameters.AddWithValue("@start", start);
                    command.Parameters.AddWithValue("@end", end);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new ChartDataPoint
                            {
                                X = Convert.ToDouble(reader.GetValue(0)),
                                Y = Convert.ToDouble(reader.GetValue(1))
                            });
                        }
                    }
                }
            }
            else
            {
                // 다른 테이블일 경우 복잡함. JOIN을 쓰거나 각각 가져와서 병합해야 함.
                // 여기서는 시간(s_time)이 동일하다고 가정하고 각각 조회 후 인덱스로 매칭하거나,
                // s_time까지 같이 조회해서 메모리에서 조인해야 안전함.
                // 일단 간단하게 각각 조회해서 순서대로 매칭 (위험할 수 있음, s_time 정렬 보장 필요)

                // 간단 구현: X, Y 각각 조회하되 ORDER BY s_time
                var xPoints = new System.Collections.Generic.List<double>();
                var yPoints = new System.Collections.Generic.List<double>();

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {_resolvedQuery.XAxisColumnName} " +
                        $"FROM {_resolvedQuery.XAxisTableName} " +
                        $"WHERE {_resolvedQuery.XAxisTimeColumnName} >= @start AND {_resolvedQuery.XAxisTimeColumnName} < @end " +
                        $"ORDER BY {_resolvedQuery.XAxisTimeColumnName}";

                    command.Parameters.AddWithValue("@start", start);
                    command.Parameters.AddWithValue("@end", end);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read()) xPoints.Add(Convert.ToDouble(reader.GetValue(0)));
                    }
                }

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {_resolvedQuery.YAxisColumnName} " +
                        $"FROM {_resolvedQuery.YAxisTableName} " +
                        $"WHERE {_resolvedQuery.YAxisTimeColumnName} >= @start AND {_resolvedQuery.YAxisTimeColumnName} < @end " +
                        $"ORDER BY {_resolvedQuery.YAxisTimeColumnName}";

                    command.Parameters.AddWithValue("@start", start);
                    command.Parameters.AddWithValue("@end", end);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read()) yPoints.Add(Convert.ToDouble(reader.GetValue(0)));
                    }
                }

                // 개수가 다르면 문제지만, 일단 min 개수만큼 매칭
                int count = Math.Min(xPoints.Count, yPoints.Count);
                for (int i = 0; i < count; i++)
                {
                    results.Add(new ChartDataPoint { X = xPoints[i], Y = yPoints[i] });
                }
            }

            return results.Count > 0 ? results : null;
        }
    }
}
