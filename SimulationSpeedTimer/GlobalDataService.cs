using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB의 모든 테이블 데이터를 통합 조회하여 Shared Memory(SimulationFrame)에 저장하는 독립 서비스
    /// SimulationController의 개별 쿼리 로직을 대체하는 글로벌 데이터 공급자 역할
    /// </summary>
    public class GlobalDataService : IDisposable
    {
        private static GlobalDataService _instance;
        public static GlobalDataService Instance => _instance ?? (_instance = new GlobalDataService());

        // 현재 활성 세션 (없으면 null)
        private DataSession _currentSession;

        // [테스트용] 데이터 조회 결과 확인을 위한 Hook (Session에서 Bubbling)
        internal event Action<Dictionary<double, SimulationFrame>> _onChunkProcessed;

        private GlobalDataService() { }

        /// <summary>
        /// 서비스 설정 객체
        /// </summary>
        public class GlobalDataServiceConfig
        {
            public string DbPath { get; set; }
            public double QueryInterval { get; set; } = 1.0;
            public int RetryCount { get; set; } = 3;
            public int RetryIntervalMs { get; set; } = 10;

            /// <summary>
            /// [Optional] 각 테이블(논리명)별 기대되는 컬럼 개수 
            /// Key: ObjectName (예: "SAM001"), Value: ColumnCount
            /// </summary>
            public Dictionary<string, int> ExpectedColumnCounts { get; set; } = new Dictionary<string, int>();
        }

        /// <summary>
        /// 서비스 시작 (1 Run = 1 Session Instance)
        /// Start() 호출 시점에는 이미 SimulationContext가 초기화되어 있다고 가정
        /// </summary>
        public void Start(GlobalDataServiceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // 1. 이전 세션 정리 (Non-blocking)
            // UI가 Stop을 먼저 불렀음을 신뢰하지만, 혹시 모르니 null 처리
            var oldSession = _currentSession;
            _currentSession = null;
            oldSession?.Stop(); // Fire and forget

            // 2. Context에서 현재 세션 ID 가져오기
            var sessionId = SimulationContext.Instance.CurrentSessionId;
            if (sessionId == Guid.Empty)
            {
                // [Feedback] Context가 시작되지 않은 상태에서의 서비스 시작은 불허 (엄격한 생명주기 준수)
                throw new InvalidOperationException("[GlobalDataService] Cannot start service: SimulationContext is not active (SessionId is Empty). Please call SimulationContext.Start() first.");
            }

            // 3. 새 세션 생성 및 시작
            var newSession = new DataSession(sessionId, config);

            // 이벤트 연결
            newSession.OnChunkProcessed += (chunk) => _onChunkProcessed?.Invoke(chunk);

            _currentSession = newSession;
            _currentSession.Run();

            Console.WriteLine($"[GlobalDataService] New Session Started: {sessionId} (Path: {config.DbPath})");
        }

        /// <summary>
        /// 외부에서 시간 정보를 입력 (시뮬레이션 시간 수신)
        /// </summary>
        public void EnqueueTime(double time)
        {
            // 현재 세션에게 전달 (세션이 없거나 종료 중이면 자동 소멸)
            _currentSession?.Enqueue(time);
        }

        /// <summary>
        /// 세션 정상 종료 요청 (Graceful Shutdown)
        /// 더 이상 데이터를 받지 않고, 내부 버퍼를 모두 처리한 후 콜백 호출
        /// </summary>
        public void CompleteSession(Action onFlushCompleted = null)
        {
            _currentSession?.MarkComplete(onFlushCompleted);
        }

        public void Stop()
        {
            var session = _currentSession;
            _currentSession = null; // 즉시 연결 해제 (새 데이터 유입 차단)
            session?.Stop();
            Console.WriteLine("[GlobalDataService] Stop Requested (Session detached).");
        }

        public void Dispose()
        {
            Stop();
        }

        // =================================================================================================
        // Inner Class: DataSession (실제 로직 구현체)
        // =================================================================================================
        private class DataSession
        {
            public Guid Id { get; }
            private readonly GlobalDataServiceConfig _config;

            // [상수] 부동소수점 조회의 경계값을 포함하기 위한 마진 (1마이크로초)
            private const double QueryMargin = 0.000001;

            private Task _workerTask;
            private CancellationTokenSource _cts;
            private BlockingCollection<double> _timeBuffer;
            private SimulationSchema _schema;
            private int _yieldCounter = 0;
            // [Optimized Cache] 테이블별 커서 캐싱 (누락 방지용)
            private Dictionary<string, double> _tableCursors = new Dictionary<string, double>();

            // 정상 종료 콜백 (Stop 호출 시 null 처리됨)
            private volatile Action _completionCallback;

            // 이벤트 Hooks
            public event Action<Dictionary<double, SimulationFrame>> OnChunkProcessed;

            public DataSession(Guid id, GlobalDataServiceConfig config)
            {
                Id = id;
                _config = config;
                _timeBuffer = new BlockingCollection<double>(boundedCapacity: 1000);
                _cts = new CancellationTokenSource();
            }

            public void Run()
            {
                _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
            }

            public void Enqueue(double time)
            {
                try
                {
                    if (!_timeBuffer.IsAddingCompleted)
                    {
                        _timeBuffer.TryAdd(time);
                    }
                }
                catch (ObjectDisposedException) { }
            }

            public void MarkComplete(Action callback)
            {
                _completionCallback = callback;
                try
                {
                    _timeBuffer.CompleteAdding();
                }
                catch (ObjectDisposedException) { }
            }

            public void Stop()
            {
                // 강제 중단 시 완료 콜백 무효화 (실행 방지)
                _completionCallback = null;

                try
                {
                    _timeBuffer?.CompleteAdding(); // 추가 중단
                }
                catch (ObjectDisposedException) { }

                try
                {
                    _cts?.Cancel(); // 작업 취소
                }
                catch (ObjectDisposedException) { }

                // 비동기로 종료 대기 (GlobalDataService는 기다리지 않음)
                // "Zero-Wait" 철학에 따라 백그라운드에서 정리되도록 둠.
                // 단, 리소스 정리를 위해 Task가 끝날 때 Dispose를 수행해야 함.
            }

            private void WorkerLoop(CancellationToken token)
            {
                SQLiteConnection connection = null;

                try
                {
                    Console.WriteLine($"[{Id}] WorkerLoop Started.");

                    // 1. DB 연결 및 초기화
                    connection = WaitForConnection(token);
                    if (connection == null) 
                    {
                        Console.WriteLine($"[{Id}] WaitForConnection returned null (Canceled?).");
                        return;
                    }
                    Console.WriteLine($"[{Id}] DB Connected: {_config.DbPath}");

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        cmd.ExecuteNonQuery();
                    }

                    // 2. 스키마 준비
                    _schema = WaitForSchemaReady(connection, token);
                    if (_schema == null) 
                    {
                        Console.WriteLine($"[{Id}] WaitForSchemaReady returned null (Timeout/Canceled?).");
                        return;
                    }
                    Console.WriteLine($"[{Id}] Schema Ready. Tables: {_schema.Tables.Count}. Processing loop start.");

                    // 3. 데이터 소비 루프
                    double nextCheckpoint = _config.QueryInterval;
                    double lastQueryEndTime = 0.0;
                    double lastSeenTime = 0.0;

                    foreach (var time in _timeBuffer.GetConsumingEnumerable())
                    {
                        lastSeenTime = time;

                        if (time >= nextCheckpoint)
                        {
                            // [수정] 이전 처리 종료 시점(lastQueryEndTime)부터 처리를 시작해야 중복/누락이 없음
                            double rangeStart = lastQueryEndTime;
                            double rangeEnd = nextCheckpoint;

                            ProcessRange(connection, rangeStart, rangeEnd, token);

                            // [수정] 처리가 완료된 'rangeEnd'로 갱신 (의미론적 명확성 및 Fast-Forward 버그 해결을 위한 기반)
                            lastQueryEndTime = rangeEnd;
                            // [루프 최적화: Fast-Forward]
                            // 데이터가 밀려서 수신된 시간(time)과 현재 처리 시점(nextCheckpoint)의 격차가 큰 경우(예: 0.2 -> 0.9),
                            // 0.1초씩 루프를 돌며 처리하는 대신, 격차만큼 한 번에 건너뛰어 최신 시점을 즉시 따라잡습니다.
                            // [루프 최적화: Fast-Forward]
                            // 데이터가 밀려서 수신된 시간(time)과 현재 처리 시점(nextCheckpoint)의 격차가 큰 경우,
                            // 루프를 돌지 않고 한 번에 처리(Range Query) 후 인덱스를 점프합니다.
                            double gap = time - nextCheckpoint;
                            if (gap > _config.QueryInterval) // 격차가 1 Interval보다 클 때만 수행
                            {
                                // [수정] Fast-Forward 시, time 지점의 데이터도 포함해서 처리해야 하므로 Margin을 더해줍니다.
                                // 또한 다음 Loop 시작점(lastQueryEndTime)도 이 Margin이 더해진 값이어야 중복 조회가 발생하지 않습니다.
                                double safeEndTime = time + QueryMargin;

                                // 점프할 구간의 데이터를 통째로 처리 (데이터 누락 방지)
                                ProcessRange(connection, nextCheckpoint, safeEndTime, token);

                                // [수정] Fast-Forward로 처리된 구간까지 lastQueryEndTime 갱신 (중복 처리 방지)
                                lastQueryEndTime = safeEndTime;

                                // 인덱스 이동 (time 바로 다음 Interval로 맞춤)
                                // 예: time=100.5, interval=0.5 -> nextCheckpoint를 101.0으로 설정
                                int jumps = (int)Math.Floor((time - nextCheckpoint) / _config.QueryInterval) + 1;
                                nextCheckpoint = Math.Round(nextCheckpoint + jumps * _config.QueryInterval, 1);
                            }
                            else
                            {
                                // [정상 진행] 격차가 크지 않으면 다음 체크포인트로 1단계만 전진
                                nextCheckpoint = Math.Round(nextCheckpoint + _config.QueryInterval, 1);
                            }

                            // [Writer 기아 방지] 주기적으로 Sleep하여 Writer가 Checkpoint를 수행할 틈을 줍니다.
                            // 50번 쿼리(약 5초 데이터)마다 10ms 양보
                            if (++_yieldCounter % 50 == 0)
                            {
                                Thread.Sleep(10);
                            }
                        }

                        if (token.IsCancellationRequested && _timeBuffer.Count == 0) break;
                    }

                    // 4. 잔여 데이터 루프 (Graceful Shutdown)
                    // [Optimized Cursor Scan - Strict User Limit]
                    // 사용자가 Stop을 요청한 시간(lastSeenTime)까지만 정확하게 데이터를 처리합니다.
                    // (DB에 미래 데이터가 있더라도 사용자가 보지 않겠다고 한 것이므로 무시)
                    // 하지만 각 테이블별로 누락된 과거 데이터(예: A테이블 51.5)는 
                    // lastSeenTime 범위 내라면 반드시 찾아서 채워넣습니다.

                    double finalEndTime = lastSeenTime;

                    if (_schema != null && _schema.Tables != null)
                    {
                        var residualChunk = new Dictionary<double, SimulationFrame>();
                        bool hasData = false;

                        foreach (var tableInfo in _schema.Tables)
                        {
                            try
                            {
                                // 해당 테이블의 마지막 Read 커서 (캐시 사용)
                                double startCursor = 0.0;
                                if (_tableCursors.TryGetValue(tableInfo.TableName, out double cursor))
                                {
                                    startCursor = cursor;
                                }
                                
                                // 이미 최종 시간까지(혹은 그 이상) 읽었으면 Skip
                                if (startCursor >= finalEndTime) continue;

                                using (var cmd = connection.CreateCommand())
                                {
                                    // [정밀 쿼리] 내 커서 이후 ~ 사용자 종료 시간까지
                                    cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} WHERE s_time > @start AND s_time <= @end";
                                    cmd.Parameters.AddWithValue("@start", startCursor);
                                    cmd.Parameters.AddWithValue("@end", finalEndTime + QueryMargin);

                                    using (var reader = cmd.ExecuteReader())
                                    {
                                        while (reader.Read())
                                        {
                                            double t = Convert.ToDouble(reader["s_time"]);
                                            
                                            // 사용자 종료 시간을 넘은 데이터는 칼같이 자름 (QueryMargin으로 인해 읽혔을 경우)
                                            if (t > finalEndTime && t > finalEndTime + 0.000001) continue;

                                            if (!residualChunk.TryGetValue(t, out var frame))
                                            {
                                                frame = new SimulationFrame(t);
                                                residualChunk[t] = frame;
                                            }

                                            // 데이터 매핑
                                            string resolvedName = !string.IsNullOrEmpty(tableInfo.ObjectName) ? tableInfo.ObjectName : tableInfo.TableName;
                                            var tableData = frame.GetTable(resolvedName); 
                                            if (tableData == null) 
                                            {
                                                tableData = new SimulationTable(resolvedName);
                                                frame.AddOrUpdateTable(tableData);
                                            }

                                            for (int i = 0; i < reader.FieldCount; i++)
                                            {
                                                string colName = reader.GetName(i);
                                                if (colName == "s_time") continue;

                                                string resolvedColName = colName;
                                                if (tableInfo.ColumnsByPhysicalName.TryGetValue(colName, out var colInfo))
                                                {
                                                    resolvedColName = colInfo.AttributeName;
                                                }
                                                var val = reader.GetValue(i);
                                                if (val != DBNull.Value) tableData.AddColumn(resolvedColName, val);
                                            }
                                            hasData = true;
                                        }
                                    }
                                }
                            }
                            catch { /* 테이블 조회 실패 무시 */ }
                        }

                        if (hasData)
                        {
                            SharedFrameRepository.Instance.StoreChunk(residualChunk, this.Id);
                            OnChunkProcessed?.Invoke(residualChunk);
                            Console.WriteLine($"[{Id}] Finalizing: Synced residual data up to {finalEndTime:F1} ({residualChunk.Count} frames).");
                        }
                    }
                }
                catch (OperationCanceledException) 
                { 
                    Console.WriteLine($"[{Id}] WorkerLoop Canceled (OperationCanceledException).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] WorkerLoop UNHANDLED EXCEPTION: {ex}");
                }
                finally
                {
                    Console.WriteLine($"[{Id}] WorkerLoop Finally Enter. Cleaning up...");
                    // 정상 종료 콜백 실행 (Stop에 의해 null 처리되었다면 실행되지 않음)
                    var callback = _completionCallback;
                    if (callback != null)
                    {
                        try { callback.Invoke(); } catch { /* Callback error ignored */ }
                    }

                    if (connection != null)
                    {
                        // Checkpoint 실행 (WAL 파일 정리 준비)
                        TryCheckpoint(connection);
                        connection.Dispose();
                    }

                    // SQLite 풀 정리 및 파일 삭제
                    SQLiteConnection.ClearAllPools();
                    TryDeleteWalFiles();

                    _timeBuffer?.Dispose();
                    _cts?.Dispose();
                    Console.WriteLine($"[{Id}] Session Disposed.");
                }
            }

            private void TryDeleteWalFiles()
            {
                try
                {
                    string walPath = _config.DbPath + "-wal";
                    string shmPath = _config.DbPath + "-shm";
                    if (System.IO.File.Exists(walPath)) System.IO.File.Delete(walPath);
                    if (System.IO.File.Exists(shmPath)) System.IO.File.Delete(shmPath);
                }
                catch { /* 파일 잠금 등으로 삭제 실패 시 무시 */ }
            }

            private void ProcessRange(SQLiteConnection conn, double start, double end, CancellationToken token)
            {
                var chunk = FetchAllTablesRangeWithRetry(conn, start, end, token);

                if (chunk == null || chunk.Count == 0)
                {
                    chunk = new Dictionary<double, SimulationFrame>();
                    // 데이터가 없더라도 빈 프레임 생성 (타임스탬프 동기화)
                    chunk[end] = new SimulationFrame(end);
                }

                SharedFrameRepository.Instance.StoreChunk(chunk, this.Id);
                OnChunkProcessed?.Invoke(chunk);
            }

            private Dictionary<double, SimulationFrame> FetchAllTablesRangeWithRetry(SQLiteConnection conn, double start, double end, CancellationToken token)
            {
                int attemptCount = 0;
                int maxAttempts = _config.RetryCount + 1;

                while (attemptCount < maxAttempts && !token.IsCancellationRequested)
                {
                    attemptCount++;
                    var result = FetchAllTablesRange(conn, start, end);

                    if (result != null && result.Count > 0)
                    {
                        return result;
                    }

                    // Fast-Fail
                    double maxTime = GetMaxTimeFromDB(conn);
                    if (maxTime >= end) return result;

                    if (attemptCount < maxAttempts)
                    {
                        Thread.Sleep(_config.RetryIntervalMs);
                    }
                }
                return new Dictionary<double, SimulationFrame>();
            }

            private Dictionary<double, SimulationFrame> FetchAllTablesRange(SQLiteConnection conn, double start, double end)
            {
                var chunk = new Dictionary<double, SimulationFrame>();
                if (_schema == null) return chunk;

                foreach (var tableInfo in _schema.Tables)
                {
                    try
                    {
                        if (!tableInfo.Columns.Any()) continue;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} WHERE s_time >= @start AND s_time < @end";
                            cmd.Parameters.AddWithValue("@start", start);
                            cmd.Parameters.AddWithValue("@end", end);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    double t = Convert.ToDouble(reader["s_time"]);

                                    if (!_tableCursors.TryGetValue(tableInfo.TableName, out double cursor) || t > cursor)
                                    {
                                        _tableCursors[tableInfo.TableName] = t;
                                    }

                                    if (!chunk.TryGetValue(t, out var frame))
                                    {
                                        frame = new SimulationFrame(t);
                                        chunk[t] = frame;
                                    }

                                    // [네이밍 변환] Object_Info의 논리적 이름(ObjectName)으로 매핑하여 저장
                                    // 예: Object_Table_0 -> SAM001
                                    string resolvedName = !string.IsNullOrEmpty(tableInfo.ObjectName)
                                        ? tableInfo.ObjectName
                                        : tableInfo.TableName;

                                    var tableData = new SimulationTable(resolvedName);
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        string colName = reader.GetName(i);
                                        if (colName == "s_time") continue;

                                        // [컬럼 네이밍 변환] COL1 -> Velocity
                                        string resolvedColName = colName;
                                        if (tableInfo.ColumnsByPhysicalName.TryGetValue(colName, out var colInfo))
                                        {
                                            resolvedColName = colInfo.AttributeName;
                                        }

                                        var val = reader.GetValue(i);
                                        if (val != DBNull.Value) tableData.AddColumn(resolvedColName, val);
                                    }
                                    frame.AddOrUpdateTable(tableData);
                                }
                            }
                        }
                    }
                    catch { /* 쿼리 오류 무시 */ }
                }
                return chunk;
            }

            private double GetMaxTimeFromDB(SQLiteConnection conn)
            {
                try
                {
                    if (_schema == null || !_schema.Tables.Any()) return -1.0;
                    double maxTime = -1.0;
                    foreach (var tableInfo in _schema.Tables)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT MAX(s_time) FROM {tableInfo.TableName}";
                            var result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                            {
                                double t = Convert.ToDouble(result);
                                if (t > maxTime) maxTime = t;
                            }
                        }
                    }
                    return maxTime;
                }
                catch { return -1.0; }
            }

            private SQLiteConnection WaitForConnection(CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var builder = new SQLiteConnectionStringBuilder { DataSource = _config.DbPath, Pooling = false, FailIfMissing = true };
                        var conn = new SQLiteConnection(builder.ToString());
                        conn.Open();
                        return conn;
                    }
                    catch (SQLiteException) { token.WaitHandle.WaitOne(500); }
                    catch (Exception) { token.WaitHandle.WaitOne(1000); }
                }
                return null;
            }

            private SimulationSchema WaitForSchemaReady(SQLiteConnection conn, CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Object_Info'";
                            var result = cmd.ExecuteScalar();
                            if (result == null || Convert.ToInt32(result) == 0)
                            {
                                token.WaitHandle.WaitOne(500);
                                continue;
                            }
                        }

                        var schema = LoadSchemaFailedSafe(conn);
                        if (schema != null && ValidateSchema(conn, schema))
                        {
                            SharedFrameRepository.Instance.Schema = schema;
                            return schema;
                        }

                        token.WaitHandle.WaitOne(1000);
                    }
                    catch { token.WaitHandle.WaitOne(1000); }
                }
                return null;
            }

            private SimulationSchema LoadSchemaFailedSafe(SQLiteConnection conn)
            {
                try
                {
                    var schema = new SimulationSchema();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT object_name, table_name FROM Object_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read()) schema.AddTable(new SchemaTableInfo(reader["table_name"]?.ToString(), reader["object_name"]?.ToString()));
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT table_name, column_name, attribute_name, data_type FROM Column_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var t = schema.GetTable(reader["table_name"]?.ToString());
                                t?.AddColumn(new SchemaColumnInfo(reader["column_name"]?.ToString(), reader["attribute_name"]?.ToString(), reader["data_type"]?.ToString()));
                            }
                        }
                    }
                    return schema;
                }
                catch { return null; }
            }

            private bool ValidateSchema(SQLiteConnection conn, SimulationSchema schema)
            {
                try
                {
                    // 테이블이 하나도 없는 경우도 아직 로딩 전으로 간주
                    if (schema.Tables == null || !schema.Tables.Any()) return false;

                    foreach (var tableInfo in schema.Tables)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"PRAGMA table_info({tableInfo.TableName})";
                            var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read()) actualColumns.Add(reader["name"]?.ToString());
                            }

                            // 1. 필수 시스템 컬럼 확인 (s_time)
                            if (!actualColumns.Contains("s_time")) return false;

                            // 2. 컬럼 수량 정합성 확인
                            // 물리 테이블의 총 컬럼 수 (s_time 포함)
                            int physicalTotalCount = actualColumns.Count;

                            // 메타데이터에 정의된 데이터 컬럼 수 (s_time 미포함)
                            int metaDataCount = tableInfo.ColumnsByPhysicalName.Count;

                            // [신규 체크] 기대 컬럼 개수가 주어진 경우 (Strict Mode)
                            // Key: 논리적 이름 (ObjectName, 예: "SAM001")
                            string logicalName = tableInfo.ObjectName;

                            if (_config.ExpectedColumnCounts != null &&
                                !string.IsNullOrEmpty(logicalName) &&
                                _config.ExpectedColumnCounts.TryGetValue(logicalName, out int expectedTotalCount))
                            {
                                // Config에는 "s_time을 포함한 전체 물리 컬럼 개수"가 들어온다고 가정 (예: 51)

                                // A. 물리 테이블이 아직 다 안 만들어졌으면 false
                                if (physicalTotalCount != expectedTotalCount) return false;

                                // B. 메타데이터가 아직 다 로드 안 됐으면 false
                                // (메타데이터 50개 + 암묵적 s_time 1개 = 51개여야 함)
                                if ((metaDataCount + 1) != expectedTotalCount) return false;
                            }
                            else
                            {
                                // [기존 Fallback] 기대 개수가 없으면, 물리 테이블과 메타데이터 간의 동기화 여부만 체크
                                // 물리(51) == 메타(50) + 1
                                if (physicalTotalCount != (metaDataCount + 1)) return false;
                            }
                        }
                    }
                    return true;
                }
                catch { return false; }
            }

            private void TryCheckpoint(SQLiteConnection conn)
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        // [수정] TRUNCATE는 Writer와 충돌 가능성이 높으므로, PASSIVE(안전 모드)로 변경
                        // Writer가 이미 정리했거나, 파일이 잠겨있으면 무리하게 시도하지 않음.
                        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch { }
            }
        }
    }
}
