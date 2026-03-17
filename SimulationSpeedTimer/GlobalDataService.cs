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
        private readonly SemaphoreSlim _sessionGate = new SemaphoreSlim(1, 1);

        internal bool HasActiveSession => _currentSession != null;

        private GlobalDataService() { }

        /// <summary>
        /// 서비스 설정 객체
        /// </summary>
        public class GlobalDataServiceConfig
        {
            public string DbPath { get; set; }
            public double QueryInterval { get; set; } = 1.0;
            public SimulationSchema RequiredSchema { get; set; }
        }

        internal async Task StartSessionAsync(Guid sessionId, GlobalDataServiceConfig config, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (sessionId == Guid.Empty)
            {
                throw new InvalidOperationException("[GlobalDataService] Cannot start service: SimulationContext is not active.");
            }
            if (config.RequiredSchema == null || config.RequiredSchema.Tables == null || !config.RequiredSchema.Tables.Any())
            {
                throw new InvalidOperationException("[GlobalDataService] RequiredSchema must be provided before starting a session.");
            }

            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var oldSession = _currentSession;
                _currentSession = null;
                if (oldSession != null)
                {
                    await oldSession.StopAsync().ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                _currentSession = new DataSession(sessionId, config);
                _currentSession.Run();
            }
            finally
            {
                _sessionGate.Release();
            }
        }

        public void EnqueueTime(double time)
        {
            var session = _currentSession;
            if (session == null) return;

            if (SimulationContext.Instance.CurrentState != SimulationLifecycleState.Running)
            {
                return;
            }

            if (SimulationContext.Instance.CurrentSessionId != session.Id)
            {
                return;
            }

            session.Enqueue(time);
        }

        public void CompleteSession(Action onFlushCompleted = null)
        {
            _currentSession?.MarkComplete(onFlushCompleted);
        }

        internal async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var session = _currentSession;
                _currentSession = null;

                if (session == null)
                {
                    return;
                }

                await session.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionGate.Release();
            }
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        // =================================================================================================
        // Inner Class: DataSession (실제 로직 구현체)
        // =================================================================================================
        private class DataSession
        {
            public Guid Id { get; }
            private readonly GlobalDataServiceConfig _config;

            private const double QueryMargin = 0.000001;

            private CancellationTokenSource _cts;
            private BlockingCollection<double> _timeBuffer;
            private SimulationSchema _schema;
            private int _yieldCounter = 0;

            // [Optimized Cache] 테이블별 커서 캐싱 (누락 방지용)
            // Independent Polling의 핵심: 각 테이블이 어디까지 읽었는지 기억합니다.
            private Dictionary<string, double> _tableCursors = new Dictionary<string, double>();

            private volatile Action _completionCallback;
            private readonly TaskCompletionSource<bool> _completionSource =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Completion => _completionSource.Task;

            public DataSession(Guid id, GlobalDataServiceConfig config)
            {
                Id = id;
                _config = config;
                _timeBuffer = new BlockingCollection<double>(boundedCapacity: 1000);
                _cts = new CancellationTokenSource();
            }

            public void Run()
            {
                Task.Run(() => WorkerLoop(_cts.Token));
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

            public async Task StopAsync()
            {
                Stop();
                await Completion.ConfigureAwait(false);
            }

            private void WorkerLoop(CancellationToken token)
            {
                SQLiteConnection connection = null;

                try
                {
                    connection = WaitForConnection(token);
                    if (connection == null) return;

                    _schema = WaitForSchemaReady(connection, token);
                    if (_schema == null) return;

                    double nextCheckpoint = NormalizeCheckpointTime(_config.QueryInterval);
                    double lastSeenTime = 0.0;

                    foreach (var time in _timeBuffer.GetConsumingEnumerable())
                    {
                        lastSeenTime = time;

                        if (time >= nextCheckpoint)
                        {
                            double rangeEnd = nextCheckpoint;

                            // [Fix] 빈 프레임 강제 주입: 데이터가 없어도 시간축 갱신을 위해 nextCheckpoint 시점에 프레임 생성 유도
                            ProcessRange(connection, rangeEnd, token, forceFrameTime: rangeEnd);

                            // [수정] 처리가 완료된 'rangeEnd'로 갱신

                            // [루프 최적화: Fast-Forward]
                            double gap = time - nextCheckpoint;
                            if (gap > _config.QueryInterval)
                            {
                                double safeEndTime = time + QueryMargin;

                                // 점프할 구간의 데이터를 통째로 처리
                                // [Fix] Fast-Forward 시점(time)에 해당하는 프레임 강제 주입
                                ProcessRange(connection, safeEndTime, token, forceFrameTime: time);


                                int jumps = (int)Math.Floor((time - nextCheckpoint) / _config.QueryInterval) + 1;
                                nextCheckpoint = NormalizeCheckpointTime(nextCheckpoint + jumps * _config.QueryInterval);
                            }
                            else
                            {
                                nextCheckpoint = NormalizeCheckpointTime(nextCheckpoint + _config.QueryInterval);
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
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] WorkerLoop UNHANDLED EXCEPTION: {ex}");
                }
                finally
                {
                    var completionCallback = _completionCallback;
                    _completionCallback = null;

                    try
                    {
                        if (connection != null)
                        {
                            connection.Dispose();
                        }

                        _timeBuffer?.Dispose();
                        _cts?.Dispose();
                    }
                    finally
                    {
                        _completionSource.TrySetResult(true);

                        try { completionCallback?.Invoke(); } catch { }
                    }
                }
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
                    }
                }

                if (chunk != null && chunk.Count > 0)
                {
                    SharedFrameRepository.Instance.StoreChunk(chunk, this.Id);
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




                        // 이미 목표 시간까지 읽었으면 Skip
                        if (startCursor >= targetEnd) continue;

                        using (var cmd = conn.CreateCommand())
                        {
                            // 내 커서 이후 ~ 목표 시간까지 조회
                            cmd.CommandText = BuildRangeQuery(tableInfo);
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

                                        string resolvedColName = colName;
                                        if (tableInfo.ColumnsByPhysicalName.TryGetValue(colName, out var colInfo))
                                        {
                                            resolvedColName = colInfo.AttributeName;
                                        }
                                        var val = reader.GetValue(i);
                                        if (val != DBNull.Value) tableData.AddColumn(resolvedColName, val);
                                    }
                                }
                            }

                            // 읽은 데이터가 있을 경우에만 커서 업데이트
                            if (maxTimeRead > startCursor)
                            {
                                _tableCursors[tableInfo.TableName] = maxTimeRead;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                return chunk;
            }

            private string BuildRangeQuery(SchemaTableInfo tableInfo)
            {
                var selectedColumns = new List<string> { QuoteIdentifier("s_time") };

                foreach (var columnName in tableInfo.ColumnsByPhysicalName.Keys)
                {
                    if (string.Equals(columnName, "s_time", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    selectedColumns.Add(QuoteIdentifier(columnName));
                }

                var projection = string.Join(", ", selectedColumns.Distinct(StringComparer.OrdinalIgnoreCase));
                return $"SELECT {projection} FROM {QuoteIdentifier(tableInfo.TableName)} WHERE {QuoteIdentifier("s_time")} > @start AND {QuoteIdentifier("s_time")} <= @end";
            }

            private static string QuoteIdentifier(string identifier)
            {
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    throw new ArgumentException("Identifier cannot be null or empty.", nameof(identifier));
                }

                return "\"" + identifier.Replace("\"", "\"\"") + "\"";
            }

            private double NormalizeCheckpointTime(double value)
            {
                return Math.Round(value, GetIntervalPrecision(_config.QueryInterval));
            }

            private static int GetIntervalPrecision(double interval)
            {
                interval = Math.Abs(interval);
                if (interval <= 0)
                {
                    return 6;
                }

                for (int precision = 0; precision <= 6; precision++)
                {
                    if (Math.Abs(interval - Math.Round(interval, precision)) < 0.000000001)
                    {
                        return precision;
                    }
                }

                return 6;
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
                        var requiredSchema = _config.RequiredSchema;
                        if (ValidateRequiredSchema(conn, requiredSchema))
                        {
                            SharedFrameRepository.Instance.TrySetSchema(requiredSchema, Id);
                            return requiredSchema;
                        }

                        token.WaitHandle.WaitOne(500);
                    }
                    catch { token.WaitHandle.WaitOne(1000); }
                }
                return null;
            }

            private bool ValidateRequiredSchema(SQLiteConnection conn, SimulationSchema schema)
            {
                try
                {
                    if (schema.Tables == null || !schema.Tables.Any()) return false;

                    foreach (var tableInfo in schema.Tables)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableInfo.TableName)})";
                            var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read()) actualColumns.Add(reader["name"]?.ToString());
                            }

                            if (!actualColumns.Contains("s_time")) return false;

                            foreach (var requiredColumn in tableInfo.ColumnsByPhysicalName.Keys)
                            {
                                if (string.Equals(requiredColumn, "s_time", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                if (!actualColumns.Contains(requiredColumn)) return false;
                            }
                        }
                    }
                    return true;
                }
                catch { return false; }
            }

        }
    }
}
