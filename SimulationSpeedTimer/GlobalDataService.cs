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

        // 동기화 및 태스크 관리
        private Task _workerTask;
        private CancellationTokenSource _cts;
        private readonly object _lock = new object();
        
        // 데이터 입력 버퍼 (Work Task용)
        private BlockingCollection<double> _timeBuffer;
        
        // Start() 대기 중 수신된 데이터를 임시 보관하는 큐 (_timeBuffer == null 구간 보호)
        private ConcurrentQueue<double> _pendingQueue = new ConcurrentQueue<double>();

        // 메타데이터 및 스키마
        private SimulationSchema _schema;
        private string _dbPath;
        private double _queryInterval = 1.0;
        
        // Retry 설정 (DatabaseQueryService와 동일한 설계)
        private int _retryCount = 3;
        private int _retryIntervalMs = 10;
        
        // [테스트용] 데이터 조회 결과 확인을 위한 Hook (외부 공개 X)
        internal event Action<Dictionary<double, SimulationFrame>> _onChunkProcessed;

        private GlobalDataService() { }

        /// <summary>
        /// 서비스 시작 (재시작 지원)
        /// </summary>
        public void Start(string dbPath, double queryInterval = 1.0, int retryCount = 3, int retryIntervalMs = 10)
        {
            lock (_lock)
            {
                // 1. 중요: 이전 시뮬레이션 잔여 데이터 제거 (Clear)
                while (_pendingQueue.TryDequeue(out _)) { }
                // SharedFrameRepository 초기화는 이제 SimulationContext가 담당함.
                // SharedFrameRepository.Instance.Clear(); // Repository도 초기화

                // 2. 핵심: 버퍼를 null로 설정하여 이후 EnqueueTime이 PendingQueue를 사용하도록 강제
                //    (이전 세션의 버퍼에 새 데이터가 오염되는 것을 방지)
                _timeBuffer = null;

                // 3. 기존 작업 대기
                if (_workerTask != null && !_workerTask.IsCompleted)
                {
                    Console.WriteLine("[GlobalDataService] Waiting for previous task to finish...");
                    try { _workerTask.Wait(); } catch { }
                }

                _dbPath = dbPath;
                _queryInterval = queryInterval;
                _retryCount = retryCount;
                _retryIntervalMs = retryIntervalMs;
                _cts = new CancellationTokenSource();
                
                // 4. 새 버퍼 생성
                var newBuffer = new BlockingCollection<double>(boundedCapacity: 1000);
                
                // 5. Wait() 중 PendingQueue에 쌓인 데이터를 새 버퍼로 이관
                // (_timeBuffer가 null이었던 구간에 EnqueueTime이 PendingQueue에 넣은 데이터)
                int replayed = 0;
                while (_pendingQueue.TryDequeue(out var pendingTime))
                {
                    newBuffer.TryAdd(pendingTime);
                    replayed++;
                }
                if(replayed > 0) Console.WriteLine($"[GlobalDataService] Replayed {replayed} pending time points.");

                _timeBuffer = newBuffer;

                // 스키마 로딩은 Worker Thread 내부에서 수행
                _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
                Console.WriteLine($"[GlobalDataService] Service Started (Interval: {_queryInterval}s).");
            }
        }

        /// <summary>
        /// 외부에서 시간 정보를 입력 (시뮬레이션 시간 수신)
        /// </summary>
        public void EnqueueTime(double time)
        {
            var buffer = _timeBuffer; // 로컬 복사로 null 체크 시점 고정 (race condition 방지)

            // 버퍼가 없으면 PendingQueue에 임시 보관 (Start() 대기 중)
            if (buffer == null)
            {
                _pendingQueue.Enqueue(time);
                return;
            }

            // 버퍼가 종료 중이면 Drop
            if (buffer.IsAddingCompleted)
                return;

            try 
            { 
                buffer.TryAdd(time); 
            }
            catch (ObjectDisposedException) 
            { 
                // 버퍼가 Dispose된 경우 (경계 조건) - PendingQueue 사용
                _pendingQueue.Enqueue(time);
            }
        }



        /// <summary>
        /// 메인 워커 루프
        /// </summary>
        private void WorkerLoop(CancellationToken token)
        {
            SQLiteConnection connection = null;

            try
            {
                // 1. DB 연결 대기
                connection = WaitForConnection(token);
                if (connection == null) return;

                // 2. WAL 모드
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL;";
                    cmd.ExecuteNonQuery();
                }

                // 3. 스키마 준비 대기
                _schema = WaitForSchemaReady(connection, token);
                if (_schema == null) return;
                
                Console.WriteLine("[GlobalDataService] Ready to process frames.");
                // 4. 시간 데이터 소비 루프 (Graceful Drain 지원)
                double nextCheckpoint = _queryInterval;
                double lastQueryEndTime = 0.0;
                double lastSeenTime = 0.0;
                
                // 핵심: token을 넘기지 않아야 CompleteAdding() 후 버퍼를 끝까지 비움 (Graceful Shutdown)
                foreach (var time in _timeBuffer.GetConsumingEnumerable())
                {
                    lastSeenTime = time;


                    if (time >= nextCheckpoint)
                    {
                        double rangeStart = nextCheckpoint - _queryInterval;
                        double rangeEnd = nextCheckpoint;

                        var chunk = FetchAllTablesRangeWithRetry(connection, rangeStart, rangeEnd, token);
                        
                        // 핵심: 데이터 유무와 관계없이 무조건 저장 및 이벤트 발생
                        // null 여부 판단은 Controller의 책임
                        if (chunk == null || chunk.Count == 0)
                        {
                            chunk = new Dictionary<double, SimulationFrame>();
                            
                            // 최적화: 스키마를 순회하며 껍데기를 만들 필요 없음.
                            // Controller는 GetTable()이 null이면 데이터가 없음을 이미 인지할 수 있음.
                            chunk[time] = new SimulationFrame(time); 
                        }
                        
                        // 데이터 저장 및 이벤트 발생
                        SharedFrameRepository.Instance.StoreChunk(chunk);
                        _onChunkProcessed?.Invoke(chunk); // 테스트용

                        lastQueryEndTime = nextCheckpoint;
                        while (time >= nextCheckpoint)
                        {
                            nextCheckpoint += _queryInterval;
                        }
                    }

                    // 루프 내부에서 취소 확인은 수동으로 (정말 급한 강제종료 대비)
                    if (token.IsCancellationRequested && _timeBuffer.Count == 0) break;
                }

                // 핵심: Graceful Shutdown - Stop 호출 시 마지막 꼬리 데이터 처리
                // (queryInterval 미달분을 추가로 조회하여 데이터 유실 방지)
                if (lastSeenTime > lastQueryEndTime)
                {
                    double start = lastQueryEndTime;
                    double end = lastSeenTime;
                    
                    var finalChunk = FetchAllTablesRangeWithRetry(connection, start, end, token);
                    if (finalChunk != null && finalChunk.Count > 0)
                    {
                        Console.WriteLine($"[GlobalDataService] Final tail chunk processed: {start:F2}s ~ {end:F2}s");
                        SharedFrameRepository.Instance.StoreChunk(finalChunk);
                        _onChunkProcessed?.Invoke(finalChunk); // 테스트용
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[GlobalDataService] Worker Error: {ex.Message}");
            }
            finally
            {
                if (connection != null)
                {
                    CleanupAndCheckpoint(connection, _dbPath);
                    connection.Dispose();
                }
            }
        }


        private SQLiteConnection WaitForConnection(CancellationToken token)
        {
             // (이전 구현과 동일)
             while (!token.IsCancellationRequested)
            {
                try
                {
                    var builder = new SQLiteConnectionStringBuilder { DataSource = _dbPath, Pooling = false, FailIfMissing = true };
                    var conn = new SQLiteConnection(builder.ToString());
                    conn.Open(); 
                    return conn;
                }
                catch (SQLiteException ex) 
                { 
                    Console.WriteLine($"[GlobalDataService] DB not ready yet ({ex.Message}). Retrying in 500ms...");
                    token.WaitHandle.WaitOne(500); 
                }
                catch (Exception ex) 
                { 
                    Console.WriteLine($"[GlobalDataService] Connection error ({ex.Message}). Retrying in 1000ms...");
                    token.WaitHandle.WaitOne(1000); 
                }
            }
            return null;
        }

        private SimulationSchema WaitForSchemaReady(SQLiteConnection conn, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. 필수 테이블(Object_Info) 존재 여부 확인
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

                    // 2. 스키마 로딩 시도
                    var schema = new SimulationSchema();

                    // Object_Info 조회
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT object_name, table_name FROM Object_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string objName = reader["object_name"]?.ToString();
                                string tblName = reader["table_name"]?.ToString();
                                if (!string.IsNullOrEmpty(tblName))
                                {
                                    schema.AddTable(new SchemaTableInfo(tblName, objName));
                                }
                            }
                        }
                    }

                    // Column_Info 조회
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT table_name, column_name, attribute_name, data_type FROM Column_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string tblName = reader["table_name"]?.ToString();
                                string colName = reader["column_name"]?.ToString();
                                string attrName = reader["attribute_name"]?.ToString();
                                string type = reader["data_type"]?.ToString();

                                var table = schema.GetTable(tblName);
                                if (table != null && !string.IsNullOrEmpty(colName))
                                {
                                    table.AddColumn(new SchemaColumnInfo(colName, attrName, type));
                                }
                            }
                        }
                    }

                    // 핵심: 메타데이터와 실제 DB 테이블 구조 일치 여부 검증
                    if (!ValidateSchema(conn, schema))
                    {
                        Console.WriteLine("[GlobalDataService] Schema validation failed. Retrying...");
                        token.WaitHandle.WaitOne(1000);
                        continue;
                    }

                    Console.WriteLine($"[GlobalDataService] Schema Loaded & Validated. Tables: {schema.TotalColumnCount} columns mapped.");
                    
                    // Repository에 스키마 공유 (Controller 등에서 매핑 해석용으로 사용)
                    SharedFrameRepository.Instance.Schema = schema;
                    
                    return schema;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GlobalDataService] Waiting for Schema... ({ex.Message})");
                    token.WaitHandle.WaitOne(1000);
                }
            }
            return null;
        }

        /// <summary>
        /// 메타데이터(Column_Info)와 실제 DB 테이블 구조 일치 여부 검증
        /// </summary>
        private bool ValidateSchema(SQLiteConnection conn, SimulationSchema schema)
        {
            try
            {
                foreach (var tableInfo in schema.Tables)
                {
                    // 실제 테이블의 컬럼 정보 조회
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"PRAGMA table_info({tableInfo.TableName})";
                        var actualColumns = new HashSet<string>();
                        
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string colName = reader["name"]?.ToString();
                                if (!string.IsNullOrEmpty(colName))
                                {
                                    actualColumns.Add(colName);
                                }
                            }
                        }
                        
                        // 메타데이터의 컬럼들이 실제 테이블에 존재하는지 검증
                        foreach (var col in tableInfo.Columns)
                        {
                            if (!actualColumns.Contains(col.ColumnName))
                            {
                                Console.WriteLine($"[GlobalDataService] Validation Error: Column '{col.ColumnName}' not found in table '{tableInfo.TableName}'");
                                return false;
                            }
                        }
                        
                        // s_time 컬럼 존재 여부 확인
                        if (!actualColumns.Contains("s_time"))
                        {
                            Console.WriteLine($"[GlobalDataService] Validation Error: 's_time' column not found in table '{tableInfo.TableName}'");
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GlobalDataService] Schema validation exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 재시도 로직이 포함된 DB 범위 조회 메서드 (DatabaseQueryService와 동일한 설계)
        /// </summary>
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
                    if (attemptCount > 1)
                    {
                        Console.WriteLine($"[GlobalDataService] Data found after {attemptCount} attempts (Range: {start:F2}s ~ {end:F2}s)");
                    }
                    return result;
                }

                // Fast-Fail 로직: DB에 기록된 최신 시간이 현재 요청 구간보다 뒤에 있다면,
                // 이 구간은 데이터가 없는 구간으로 확정하고 재시도 없이 종료
                double maxTime = GetMaxTimeFromDB(conn);
                if (maxTime >= end) // end보다 크거나 같으면 이미 지나간 구간
                {
                    // 데이터가 없는 구간으로 확정
                    return result; // null 또는 빈 딕셔너리 반환
                }

                if (attemptCount < maxAttempts)
                {
                    Thread.Sleep(_retryIntervalMs);
                }
            }

            // 모든 재시도 실패
            Console.WriteLine($"[GlobalDataService] No data found after {maxAttempts} attempts (Range: {start:F2}s ~ {end:F2}s) - Simulation may have ended");
            return new Dictionary<double, SimulationFrame>();
        }

        /// <summary>
        /// DB에 기록된 가장 최신 시간을 조회 (재시도 판단용 - Fast-Fail)
        /// </summary>
        private double GetMaxTimeFromDB(SQLiteConnection conn)
        {
            try
            {
                if (_schema == null || !_schema.Tables.Any())
                    return -1.0;

                double maxTime = -1.0;

                // 모든 테이블의 최대 s_time 확인
                foreach (var tableInfo in _schema.Tables)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT MAX(s_time) FROM {tableInfo.TableName}";
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            double tableMaxTime = Convert.ToDouble(result);
                            if (tableMaxTime > maxTime)
                            {
                                maxTime = tableMaxTime;
                            }
                        }
                    }
                }

                return maxTime;
            }
            catch
            {
                // 오류 발생 시 무시
                return -1.0;
            }
        }
        
        private Dictionary<double, SimulationFrame> FetchAllTablesRange(SQLiteConnection conn, double start, double end)
        {
            var chunk = new Dictionary<double, SimulationFrame>();

            if (_schema == null) return chunk;

            // 스키마에 정의된 모든 테이블 순회
            foreach (var tableInfo in _schema.Tables)
            {
                try
                {
                    if (!tableInfo.Columns.Any()) continue;

                    using (var cmd = conn.CreateCommand())
                    {
                        // 성능 최적화: SELECT * 사용 (스키마 검증은 이미 완료)
                        cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} WHERE s_time >= @start AND s_time < @end";
                        cmd.Parameters.AddWithValue("@start", start);
                        cmd.Parameters.AddWithValue("@end", end);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                double t = Convert.ToDouble(reader["s_time"]);

                                // 해당 시간의 Frame 찾기 또는 생성
                                if (!chunk.TryGetValue(t, out var frame))
                                {
                                    frame = new SimulationFrame(t);
                                    chunk[t] = frame;
                                }

                                // 테이블 데이터 생성 및 값 채우기 (인덱스 기반 접근으로 성능 향상)
                                var tableData = new SimulationTable(tableInfo.TableName);
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    string colName = reader.GetName(i);
                                    if (colName == "s_time") continue; // s_time은 제외
                                    
                                    var val = reader.GetValue(i);
                                    if (val != DBNull.Value)
                                    {
                                        tableData.AddColumn(colName, val);
                                    }
                                }

                                frame.AddOrUpdateTable(tableData);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GlobalDataService] Query Error ({tableInfo.TableName}): {ex.Message}");
                }
            }

            return chunk;
        }

        private void CleanupAndCheckpoint(SQLiteConnection conn, string dbPath)
        {
            // (이전 구현과 동일)
             try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                Console.WriteLine("[GlobalDataService] Checkpoint Executed.");
            }
            catch { }
            conn.Close();
            SQLiteConnection.ClearAllPools();
            try
            {
                string walPath = dbPath + "-wal";
                string shmPath = dbPath + "-shm";
                if (System.IO.File.Exists(walPath)) System.IO.File.Delete(walPath);
                if (System.IO.File.Exists(shmPath)) System.IO.File.Delete(shmPath);
            }
            catch { }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_workerTask == null) return;
                
                // PendingQueue 비우기 (잔여 데이터 제거)
                while (_pendingQueue.TryDequeue(out _)) { }

                Console.WriteLine("[GlobalDataService] Stop Requested.");

                try
                {
                    // 1. 소비 종료 선언 (버퍼에 남은건 끝까지 처리하라고 신호함)
                    _timeBuffer?.CompleteAdding();
                    
                    // 2. 즉시 Cancel 하지 않고 Worker가 스스로 종료되길 기다림
                    // (WorkerLoop에서 CompleteAdding 후 남은 데이터를 처리하고 루프를 빠져나옴)
                }
                catch (ObjectDisposedException) { }

                if (_workerTask != null && !_workerTask.IsCompleted)
                {
                    try
                    {
                        // 최대 5초간 우아한 종료를 기다림
                        bool completed = _workerTask.Wait(TimeSpan.FromSeconds(5)); 
                        if (!completed)
                        {
                            Console.WriteLine($"[GlobalDataService] Graceful stop timed out. Forcing cancellation...");
                            _cts?.Cancel(); // 5초 넘으면 강제 종료
                            _workerTask.Wait(1000); // 강제 종료 대기
                        }
                    }
                    catch (AggregateException) { }
                    catch (Exception ex) { Console.WriteLine($"[GlobalDataService] Error waiting stop: {ex.Message}"); }
                }

                _timeBuffer?.Dispose();
                _cts?.Dispose();
                
                _workerTask = null;
                _timeBuffer = null;
                _cts = null;
                
                Console.WriteLine("[GlobalDataService] Service Stopped.");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
