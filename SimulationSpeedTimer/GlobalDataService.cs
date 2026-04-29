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
            // [Refactored] Retry 관련 설정 (사용하지 않음, 호환성 유지)
            public int RetryCount { get; set; } = 0;
            public int RetryIntervalMs { get; set; } = 10;

            public Dictionary<string, List<string>> ExpectedColumns { get; set; } = new Dictionary<string, List<string>>();
        }

        public void Start(GlobalDataServiceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var oldSession = _currentSession;
            _currentSession = null;
            oldSession?.Stop();

            var sessionId = SimulationContext.Instance.CurrentSessionId;
            if (sessionId == Guid.Empty)
            {
                throw new InvalidOperationException("[GlobalDataService] Cannot start service: SimulationContext is not active.");
            }

            var newSession = new DataSession(sessionId, config);
            newSession.OnChunkProcessed += (chunk) => _onChunkProcessed?.Invoke(chunk);

            _currentSession = newSession;
            _currentSession.Run();

            Console.WriteLine($"[GlobalDataService] New Session Started: {sessionId} (Path: {config.DbPath})");
        }

        public void EnqueueTime(double time)
        {
            _currentSession?.Enqueue(time);
        }

        public void CompleteSession(Action onFlushCompleted = null)
        {
            _currentSession?.MarkComplete(onFlushCompleted);
        }

        public void Stop()
        {
            var session = _currentSession;
            _currentSession = null;
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

            private const double QueryMargin = 0.000001;

            private Task _workerTask;
            private CancellationTokenSource _cts;
            private BlockingCollection<double> _timeBuffer;
            private SimulationSchema _schema;
            private int _yieldCounter = 0;

            // [Optimized Cache] 테이블별 커서 캐싱 (누락 방지용)
            // Independent Polling의 핵심: 각 테이블이 어디까지 읽었는지 기억합니다.
            private Dictionary<string, double> _tableCursors = new Dictionary<string, double>();

            private volatile Action _completionCallback;
            private readonly IComparisonExportService _comparisonExportService = new ComparisonExportService();

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
                _completionCallback = null;
                try { _timeBuffer?.CompleteAdding(); } catch { }
                try { _cts?.Cancel(); } catch { }
            }

            private void WorkerLoop(CancellationToken token)
            {
                SQLiteConnection connection = null;

                try
                {
                    Console.WriteLine($"[{Id}] WorkerLoop Started.");

                    connection = WaitForConnection(token);
                    if (connection == null) return;
                    Console.WriteLine($"[{Id}] DB Connected: {_config.DbPath}");

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        cmd.ExecuteNonQuery();
                    }

                    _schema = WaitForSchemaReady(connection, token);
                    if (_schema == null) return;
                    Console.WriteLine($"[{Id}] Schema Ready. Tables: {_schema.Tables.Count()}. Processing loop start.");

                    double nextCheckpoint = _config.QueryInterval;
                    double lastQueryEndTime = 0.0;
                    double lastSeenTime = 0.0;

                    foreach (var time in _timeBuffer.GetConsumingEnumerable())
                    {
                        lastSeenTime = time;

                        if (time >= nextCheckpoint)
                        {
                            // [수정] 이전 처리 종료 시점(lastQueryEndTime)부터 처리를 시작해야 중복/누락이 없음
                            // Independent Polling에서는 start 인자가 불필요하므로 제거합니다.
                            double rangeEnd = nextCheckpoint;

                            // [Fix] 빈 프레임 강제 주입: 데이터가 없어도 시간축 갱신을 위해 nextCheckpoint 시점에 프레임 생성 유도
                            ProcessRange(connection, rangeEnd, token, forceFrameTime: rangeEnd);

                            // [수정] 처리가 완료된 'rangeEnd'로 갱신
                            lastQueryEndTime = rangeEnd;

                            // [루프 최적화: Fast-Forward]
                            double gap = time - nextCheckpoint;
                            if (gap > _config.QueryInterval)
                            {
                                double safeEndTime = time + QueryMargin;

                                // 점프할 구간의 데이터를 통째로 처리
                                // [Fix] Fast-Forward 시점(time)에 해당하는 프레임 강제 주입
                                ProcessRange(connection, safeEndTime, token, forceFrameTime: time);

                                lastQueryEndTime = safeEndTime;

                                int jumps = (int)Math.Floor((time - nextCheckpoint) / _config.QueryInterval) + 1;
                                nextCheckpoint = Math.Round(nextCheckpoint + jumps * _config.QueryInterval, 1);
                            }
                            else
                            {
                                nextCheckpoint = Math.Round(nextCheckpoint + _config.QueryInterval, 1);
                            }

                            if (++_yieldCounter % 50 == 0) Thread.Sleep(10);
                        }

                        if (token.IsCancellationRequested && _timeBuffer.Count == 0) break;
                    }

                    // 4. 잔여 데이터 루프 (Graceful Shutdown)
                    // 사용자가 Stop을 요청한 시간(lastSeenTime)까지만 정확하게 데이터를 처리합니다.
                    double finalEndTime = lastSeenTime;

                    if (_schema != null && _schema.Tables != null)
                    {
                        // [Refactored] 중복 로직 제거 -> ProcessRange 재사용
                        // 종료 시점까지 남은 데이터를 모두 긁어옵니다. (CancellationToken은 무시하거나 None 사용)
                        // 사용자 보장: "Write는 다 끝났다" -> 따라서 Retry 없이 1회 조회로 족함.
                        ProcessRange(connection, finalEndTime, CancellationToken.None);
                        Console.WriteLine($"[{Id}] Finalizing: Synced residual data up to {finalEndTime:F1}.");
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[{Id}] WorkerLoop Canceled.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] WorkerLoop UNHANDLED EXCEPTION: {ex}");
                }
                finally
                {
                    Console.WriteLine($"[{Id}] WorkerLoop Finally Enter. Cleaning up...");
                    try { _completionCallback?.Invoke(); } catch { }

                    if (connection != null)
                    {
                        TryCheckpoint(connection);
                        connection.Dispose();
                    }

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
                catch { }
            }

            // [Fix] forceFrameTime 파라미터 추가
            private void ProcessRange(SQLiteConnection conn, double end, CancellationToken token, double? forceFrameTime = null)
            {
                // [Independent Polling]
                // 각 테이블별로 읽을 수 있는 만큼만 독립적으로 읽어서 병합합니다.
                var chunk = FetchIndependentTables(conn, end);

                // [Fix] 강제 프레임 주입 (데이터가 하나도 없는 구간이라도 시간축 진행을 위해 필요)
                if (forceFrameTime.HasValue)
                {
                    double targetTime = forceFrameTime.Value;
                    // 부동소수점 오차 고려: 딕셔너리에 정확히 없으면 추가
                    if (!chunk.TryGetValue(targetTime, out _))
                    {
                        // 데이터가 없는 경우, 빈 SimulationFrame을 생성하여 주입
                        // ChartAxisDataProvider는 이를 받아 NaN으로 처리하여 시간축을 진행시킴
                        chunk[targetTime] = new SimulationFrame(targetTime);
                        // Console.WriteLine($"[GlobalDataService] Injected Dummy Frame at {targetTime:F1}");
                    }
                }

                if (chunk != null && chunk.Count > 0)
                {
                    SharedFrameRepository.Instance.StoreChunk(chunk, this.Id);
                    OnChunkProcessed?.Invoke(chunk);
                }
            }

            // [New Core Logic] 독립적 테이블 폴링 (No Global Wait)
            private Dictionary<double, SimulationFrame> FetchIndependentTables(SQLiteConnection conn, double targetEnd)
            {
                var chunk = new Dictionary<double, SimulationFrame>();
                if (_schema == null) return chunk;

                foreach (var tableInfo in _schema.Tables)
                {
                    try
                    {
                        // 해당 테이블의 마지막 Read 커서 가져오기
                        double startCursor = -1.0;
                        if (_tableCursors.TryGetValue(tableInfo.TableName, out double cursor))
                        {
                            startCursor = cursor;
                        }
                        Console.WriteLine($"[DEBUG] Fetching {tableInfo.TableName} Start: {startCursor,5:F1} Target: {targetEnd,5:F1}");

                        // DEBUG: Check actual count

                        using (var debugCmd = conn.CreateCommand())
                        {
                            debugCmd.CommandText = $"SELECT count(*) FROM {tableInfo.TableName}";
                            object cnt = debugCmd.ExecuteScalar();
                            Console.WriteLine($"[DEBUG] {tableInfo.TableName} Total Count: {cnt}");
                        }


                        // 이미 목표 시간까지 읽었으면 Skip
                        if (startCursor >= targetEnd) continue;

                        using (var cmd = conn.CreateCommand())
                        {
                            // 내 커서 이후 ~ 목표 시간까지 조회
                            cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} WHERE s_time > @start AND s_time <= @end";
                            cmd.Parameters.AddWithValue("@start", startCursor);
                            cmd.Parameters.AddWithValue("@end", targetEnd);

                            double maxTimeRead = startCursor;

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    double t = Convert.ToDouble(reader["s_time"]);
                                    if (t > maxTimeRead) maxTimeRead = t;

                                    if (!chunk.TryGetValue(t, out var frame))
                                    {
                                        frame = new SimulationFrame(t);
                                        chunk[t] = frame;
                                    }

                                    // 데이터 매핑
                                    string resolvedName = !string.IsNullOrEmpty(tableInfo.ObjectName)
                                        ? tableInfo.ObjectName
                                        : tableInfo.TableName;

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

                                        // [Filtering Logic] 스키마에 정의된(필터링되어 남은) 컬럼에 대해서만 데이터 매핑 수행
                                        if (tableInfo.ColumnsByPhysicalName.TryGetValue(colName, out var colInfo))
                                        {
                                            var val = reader.GetValue(i);
                                            if (val != DBNull.Value)
                                                tableData.AddColumn(colInfo.AttributeName, val);
                                        }
                                    }
                                }
                            }

                            // 읽은 데이터가 있을 경우에만 커서 업데이트
                            if (maxTimeRead > startCursor)
                            {
                                _tableCursors[tableInfo.TableName] = maxTimeRead;
                            }
                            // Console.WriteLine($"[DEBUG] {tableInfo.TableName} read up to {maxTimeRead}. Rows: {chunk.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] FetchIndependentTables Failed for {tableInfo.TableName}: {ex.Message}");
                    }
                }

                return chunk;
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
                int retryCnt = 0;
                int stableCount = 0;
                int? lastSchemaVersion = null;
                SimulationSchema lastLoadedSchema = null;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (retryCnt > 10)
                        {
                            TryWriteSchemaValidationReport(lastLoadedSchema, false);
                            return null;
                        }

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
                        if (schema != null)
                        {
                            lastLoadedSchema = schema;
                        }

                        if (schema != null && ValidateSchema(schema))
                        {
                            // 3. 검증 성공 시! -> 불필요한 컬럼 가지치기(필터링) 수행
                            FilterSchemaByConfig(schema);
                            if (!ValidateFilteredTablesReady(conn, schema))
                            {
                                retryCnt++;
                                stableCount = 0;
                                lastSchemaVersion = null;
                                token.WaitHandle.WaitOne(1000);
                                continue;
                            }

                            // 4. 깔끔하게 정리된 스키마를 저장 후 반환
                            var currentSchemaVersion = GetSchemaVersion(conn);
                            if (!currentSchemaVersion.HasValue)
                            {
                                retryCnt++;
                                stableCount = 0;
                                lastSchemaVersion = null;
                                token.WaitHandle.WaitOne(1000);
                                continue;
                            }

                            if (lastSchemaVersion.HasValue && lastSchemaVersion.Value == currentSchemaVersion.Value)
                            {
                                stableCount++;
                            }
                            else
                            {
                                stableCount = 1;
                                lastSchemaVersion = currentSchemaVersion.Value;
                            }

                            if (stableCount < 2)
                            {
                                token.WaitHandle.WaitOne(300);
                                continue;
                            }

                            TryWriteSchemaValidationReport(schema, true);
                            SharedFrameRepository.Instance.Schema = schema;
                            return schema;
                        }

                        retryCnt++;
                        stableCount = 0;
                        lastSchemaVersion = null;
                        token.WaitHandle.WaitOne(1000);
                    }
                    catch
                    {
                        retryCnt++;
                        stableCount = 0;
                        lastSchemaVersion = null;
                        token.WaitHandle.WaitOne(1000);
                    }
                }
                return null;
            }

            private void TryWriteSchemaValidationReport(SimulationSchema schema, bool isSuccess)
            {
                try
                {
                    var sourceDataset = SchemaValidationComparisonFactory.CreateSourceDataset(schema);
                    var targetDataset = SchemaValidationComparisonFactory.CreateTargetDataset(_config.ExpectedColumns);
                    var options = SchemaValidationComparisonFactory.CreateOptions(_config.DbPath);

                    if (!isSuccess)
                    {
                        options.Title = options.Title + "_실패";
                    }

                    _comparisonExportService.TryExportHtml(sourceDataset, targetDataset, options, out _);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ComparisonExport] Failed to build comparison export request: {ex.Message}");
                }
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

            private bool ValidateSchema(SimulationSchema schema)
            {
                try
                {
                    // 0. 테이블이 하나도 없는 경우도 아직 로딩 전으로 간주
                    if ((schema.Tables == null) || (!schema.Tables.Any())) { return false; }

                    // 1. 읽어온 테이블의 갯수가 Config에 설정된 테이블에 갯수와 다른 경우.
                    // (Config가 설정되어 있을 때만 개수 비교 수행)
                    if (_config.ExpectedColumns != null && _config.ExpectedColumns.Count > 0)
                    {
                        if (schema.Tables.Count() != _config.ExpectedColumns.Count) return false;

                        foreach (var tableInfo in schema.Tables)
                        {
                            if (!_config.ExpectedColumns.TryGetValue(tableInfo.ObjectName, out var targetColumnList))
                            {
                                return false; // Config에 없는 테이블이 스키마에 있음
                            }

                            // 2. 읽어온 테이블의 컬럼 목록에서 Config에 설정된 컬럼이 전부 포함되지 않은 경우.
                            // 2. 읽어온 테이블의 컬럼 목록에서 Config에 설정된 컬럼이 전부 포함되지 않은 경우.
                            var loadedAttributes = new HashSet<string>(tableInfo.Columns.Select(c => c.AttributeName));
                            if (!loadedAttributes.IsSupersetOf(targetColumnList)) { return false; }
                        }
                    }

                    return true;
                }
                catch { return false; }
            }

            private void FilterSchemaByConfig(SimulationSchema schema)
            {
                if (_config.ExpectedColumns == null || _config.ExpectedColumns.Count == 0) return;

                // 스키마 컬렉션이 루프 도중 변경(삭제)될 수 있으므로 ToList()를 통해 복제된 리스트로 순회합니다.
                var currentTables = schema.Tables.ToList();

                foreach (var table in currentTables)
                {
                    if (_config.ExpectedColumns.TryGetValue(table.ObjectName, out var targetColumns))
                    {
                        // 1. 대소문자를 명확히 구분하는 HashSet 생성
                        var allowedAttributes = new HashSet<string>(targetColumns);

                        // 2. Config에 대소문자까지 완벽히 일치하게 적힌 컬럼 + 필수 컬럼("s_time")만 추출
                        var filteredColumns = table.Columns
                            .Where(c => allowedAttributes.Contains(c.AttributeName) ||
                                        c.ColumnName == "s_time")
                            .ToList();

                        // 3. 필터링된 컬럼으로 기존 스키마 덮어쓰기
                        table.SetFilteredColumns(filteredColumns);
                    }
                    else
                    {
                        // Config에 명시되지 않은 테이블의 경우, 스키마 목록에서 자체를 제거하여
                        // 불필요한 메타데이터 유지 및 데이터 로드를 원천적으로 차단합니다.
                        schema.RemoveTable(table.TableName);
                    }
                }
            }

            private bool ValidateFilteredTablesReady(SQLiteConnection conn, SimulationSchema schema)
            {
                if (conn == null || schema == null || schema.Tables == null)
                {
                    return false;
                }

                try
                {
                    foreach (var table in schema.Tables)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT count(*) FROM {table.TableName}";
                            cmd.ExecuteScalar();
                        }
                    }

                    return true;
                }
                catch (SQLiteException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            }

            private int? GetSchemaVersion(SQLiteConnection conn)
            {
                if (conn == null)
                {
                    return null;
                }

                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA schema_version;";
                        var result = cmd.ExecuteScalar();
                        if (result == null)
                        {
                            return null;
                        }

                        return Convert.ToInt32(result);
                    }
                }
                catch
                {
                    return null;
                }
            }

            private void TryCheckpoint(SQLiteConnection conn)
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch { }
            }
        }
    }
}
