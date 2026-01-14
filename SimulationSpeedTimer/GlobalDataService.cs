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
        /// 서비스 시작 (1 Run = 1 Session Instance)
        /// Start() 호출 시점에는 이미 SimulationContext가 초기화되어 있다고 가정
        /// </summary>
        public void Start(string dbPath, double queryInterval = 1.0, int retryCount = 3, int retryIntervalMs = 10)
        {
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
            var newSession = new DataSession(sessionId, dbPath, queryInterval, retryCount, retryIntervalMs);
            
            // 이벤트 연결
            newSession.OnChunkProcessed += (chunk) => _onChunkProcessed?.Invoke(chunk);

            _currentSession = newSession;
            _currentSession.Run();

            Console.WriteLine($"[GlobalDataService] New Session Started: {sessionId} (Path: {dbPath})");
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
            private readonly string _dbPath;
            private readonly double _queryInterval;
            private readonly int _retryCount;
            private readonly int _retryIntervalMs;

            private Task _workerTask;
            private CancellationTokenSource _cts;
            private BlockingCollection<double> _timeBuffer;
            private SimulationSchema _schema;

            // 정상 종료 콜백 (Stop 호출 시 null 처리됨)
            private volatile Action _completionCallback;

            // 이벤트 Hooks
            public event Action<Dictionary<double, SimulationFrame>> OnChunkProcessed;

            public DataSession(Guid id, string dbPath, double queryInterval, int retryCount, int retryIntervalMs)
            {
                Id = id;
                _dbPath = dbPath;
                _queryInterval = queryInterval;
                _retryCount = retryCount;
                _retryIntervalMs = retryIntervalMs;
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
                    // 1. DB 연결 및 초기화
                    connection = WaitForConnection(token);
                    if (connection == null) return;

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        cmd.ExecuteNonQuery();
                    }

                    // 2. 스키마 준비
                    _schema = WaitForSchemaReady(connection, token);
                    if (_schema == null) return;

                    Console.WriteLine($"[{Id}] Ready to process frames.");

                    // 3. 데이터 소비 루프
                    double nextCheckpoint = _queryInterval;
                    double lastQueryEndTime = 0.0;
                    double lastSeenTime = 0.0;

                    foreach (var time in _timeBuffer.GetConsumingEnumerable())
                    {
                        lastSeenTime = time;

                        if (time >= nextCheckpoint)
                        {
                            double rangeStart = nextCheckpoint - _queryInterval;
                            double rangeEnd = nextCheckpoint;

                            ProcessRange(connection, rangeStart, rangeEnd, token);

                            lastQueryEndTime = nextCheckpoint;
                            // [최적화] 반복문 대신 계산식을 통한 즉시 시간 이동 (무한 루프 방지)
                            double gap = time - nextCheckpoint;
                            if (gap >= 0)
                            {
                                int jumps = (int)Math.Floor(gap / _queryInterval) + 1;
                                nextCheckpoint += jumps * _queryInterval;
                            }
                        }

                        if (token.IsCancellationRequested && _timeBuffer.Count == 0) break;
                    }

                    // 4. 잔여 데이터 루프 (Graceful Shutdown)
                    if (lastSeenTime > lastQueryEndTime)
                    {
                        ProcessRange(connection, lastQueryEndTime, lastSeenTime, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] Worker Error: {ex.Message}");
                }
                finally
                {
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
                    string walPath = _dbPath + "-wal";
                    string shmPath = _dbPath + "-shm";
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
                int maxAttempts = _retryCount + 1;

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
                        Thread.Sleep(_retryIntervalMs);
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
                                    if (!chunk.TryGetValue(t, out var frame))
                                    {
                                        frame = new SimulationFrame(t);
                                        chunk[t] = frame;
                                    }

                                    var tableData = new SimulationTable(tableInfo.TableName);
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        string colName = reader.GetName(i);
                                        if (colName == "s_time") continue;
                                        var val = reader.GetValue(i);
                                        if (val != DBNull.Value) tableData.AddColumn(colName, val);
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
                        var builder = new SQLiteConnectionStringBuilder { DataSource = _dbPath, Pooling = false, FailIfMissing = true };
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
                        if(schema != null && ValidateSchema(conn, schema))
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
                 try {
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
                             while(reader.Read()) {
                                 var t = schema.GetTable(reader["table_name"]?.ToString());
                                 t?.AddColumn(new SchemaColumnInfo(reader["column_name"]?.ToString(), reader["attribute_name"]?.ToString(), reader["data_type"]?.ToString()));
                             }
                         }
                     }
                     return schema;
                 } catch { return null; }
            }

            private bool ValidateSchema(SQLiteConnection conn, SimulationSchema schema)
            {
                 try
                {
                    foreach (var tableInfo in schema.Tables)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"PRAGMA table_info({tableInfo.TableName})";
                            var actualColumns = new HashSet<string>();
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read()) actualColumns.Add(reader["name"]?.ToString());
                            }
                            if (!actualColumns.Contains("s_time")) return false;
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
                     using(var cmd = conn.CreateCommand()) {
                         cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                         cmd.ExecuteNonQuery();
                     }
                 } catch {}
            }
        }
    }
}
