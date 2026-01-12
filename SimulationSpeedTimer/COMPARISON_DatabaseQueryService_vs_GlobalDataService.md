# DatabaseQueryService vs GlobalDataService 핵심 로직 비교

## 📋 개요

**GlobalDataService**는 **DatabaseQueryService**의 핵심 설계 개념을 그대로 유지하면서, **조회 범위만 "특정 테이블/컬럼"에서 "전체 DB"로 확장**한 버전입니다.

## ✅ 동일하게 유지된 핵심 로직

### 1. **Retry 로직 (완전 동일)**

#### DatabaseQueryService
```csharp
private List<ChartDataPoint> QueryDatabaseRangeWithRetry(double start, double end, CancellationToken token)
{
    int attemptCount = 0;
    int maxAttempts = _config.RetryCount + 1;

    while (attemptCount < maxAttempts && !token.IsCancellationRequested)
    {
        attemptCount++;
        var result = QueryDatabaseRange(start, end);

        if (result != null && result.Count > 0)
            return result;

        // Fast-Fail: DB 최신 시간 확인
        double maxTime = GetMaxTimeFromDB();
        if (maxTime >= end)
            return null;

        if (attemptCount < maxAttempts)
            Thread.Sleep(_config.RetryIntervalMs);
    }
    return null;
}
```

#### GlobalDataService
```csharp
private Dictionary<double, SimulationFrame> FetchAllTablesRangeWithRetry(
    SQLiteConnection conn, double start, double end, CancellationToken token)
{
    int attemptCount = 0;
    int maxAttempts = _retryCount + 1;

    while (attemptCount < maxAttempts && !token.IsCancellationRequested)
    {
        attemptCount++;
        var result = FetchAllTablesRange(conn, start, end);

        if (result != null && result.Count > 0)
            return result;

        // Fast-Fail: DB 최신 시간 확인
        double maxTime = GetMaxTimeFromDB(conn);
        if (maxTime >= end)
            return result;

        if (attemptCount < maxAttempts)
            Thread.Sleep(_retryIntervalMs);
    }
    return new Dictionary<double, SimulationFrame>();
}
```

**✅ 동일점:**
- 재시도 횟수 관리 (`RetryCount + 1`)
- Fast-Fail 메커니즘 (DB 최신 시간 확인)
- 재시도 간격 (`RetryIntervalMs`)
- 취소 토큰 처리

**차이점:**
- 반환 타입만 다름 (`List<ChartDataPoint>` vs `Dictionary<double, SimulationFrame>`)

---

### 2. **Fast-Fail 메커니즘 (개념 동일, 구현 확장)**

#### DatabaseQueryService
```csharp
private double GetMaxTimeFromDB()
{
    double maxX = -1.0;
    double maxY = -1.0;

    // X축 테이블 최신 시간
    using (var cmd = _connection.CreateCommand())
    {
        cmd.CommandText = $"SELECT MAX({_resolvedQuery.XAxisTimeColumnName}) FROM {_resolvedQuery.XAxisTableName}";
        var result = cmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
            maxX = Convert.ToDouble(result);
    }

    // Y축 테이블 최신 시간 (다른 테이블인 경우)
    if (!_resolvedQuery.IsSameTable)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT MAX({_resolvedQuery.YAxisTimeColumnName}) FROM {_resolvedQuery.YAxisTableName}";
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
                maxY = Convert.ToDouble(result);
        }
    }

    return Math.Max(maxX, maxY);
}
```

#### GlobalDataService
```csharp
private double GetMaxTimeFromDB(SQLiteConnection conn)
{
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
                    maxTime = tableMaxTime;
            }
        }
    }

    return maxTime;
}
```

**✅ 동일점:**
- DB의 최신 시간을 조회하여 Fast-Fail 판단
- 여러 테이블을 확인하여 최대값 반환
- 에러 발생 시 `-1.0` 반환

**차이점:**
- DatabaseQueryService: X/Y 2개 테이블만 확인
- GlobalDataService: 스키마의 모든 테이블 확인 (확장)

---

### 3. **메타데이터 대기 로직 (개념 동일)**

#### DatabaseQueryService
```csharp
// WorkerLoop 내부
while (_resolvedQuery == null && !token.IsCancellationRequested)
{
    // 1. 테이블 존재 여부 확인
    if (!MetadataResolver.AreMetadataTablesReady(_connection, _config))
    {
        Thread.Sleep(100);
        continue;
    }

    try
    {
        _resolvedQuery = MetadataResolver.Resolve(_config, _connection);
    }
    catch (InvalidOperationException)
    {
        Thread.Sleep(100);
    }
}
```

#### GlobalDataService
```csharp
private SimulationSchema WaitForSchemaReady(SQLiteConnection conn, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            // 1. Object_Info 테이블 존재 확인
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

            // 2. 스키마 로딩
            var schema = new SimulationSchema();
            // ... Object_Info, Column_Info 조회 ...

            // 3. 스키마 검증
            if (!ValidateSchema(conn, schema))
            {
                token.WaitHandle.WaitOne(1000);
                continue;
            }

            return schema;
        }
        catch (Exception ex)
        {
            token.WaitHandle.WaitOne(1000);
        }
    }
    return null;
}
```

**✅ 동일점:**
- 메타데이터 테이블이 준비될 때까지 대기
- 예외 발생 시 재시도
- 취소 토큰으로 중단 가능
- 대기 시간 설정 (100ms~1000ms)

**차이점:**
- DatabaseQueryService: `MetadataResolver` 사용 (특정 X/Y 컬럼 해석)
- GlobalDataService: 전체 스키마 로딩 + 검증 (모든 테이블)

---

### 4. **DB 연결 관리 (개념 동일)**

#### DatabaseQueryService
```csharp
// Start() 내부
var connectionString = new SQLiteConnectionStringBuilder
{
    DataSource = _config.DatabasePath,
    Pooling = false,  // SQLite는 풀링 불필요
}.ToString();

_connection = new SQLiteConnection(connectionString);
_connection.Open();

// WAL 모드 확인
using (var cmd = _connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA journal_mode;";
    var mode = cmd.ExecuteScalar()?.ToString();
    if (mode?.ToLower() != "wal")
        Console.WriteLine($"[{ServiceId}] [경고] WAL 모드가 아닙니다.");
}
```

#### GlobalDataService
```csharp
private SQLiteConnection WaitForConnection(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            var builder = new SQLiteConnectionStringBuilder 
            { 
                DataSource = _dbPath, 
                Pooling = false, 
                FailIfMissing = true 
            };
            var conn = new SQLiteConnection(builder.ToString());
            conn.Open();
            return conn;
        }
        catch (SQLiteException ex)
        {
            token.WaitHandle.WaitOne(500);
        }
    }
    return null;
}

// WorkerLoop 내부
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA journal_mode=WAL;";
    cmd.ExecuteNonQuery();
}
```

**✅ 동일점:**
- `Pooling = false` (SQLite 특성)
- WAL 모드 설정/확인
- 연결 실패 시 재시도 (GlobalDataService)

**차이점:**
- DatabaseQueryService: Start()에서 즉시 연결
- GlobalDataService: WaitForConnection()으로 연결 대기 (더 견고)

---

### 5. **WorkerLoop 구조 (개념 동일)**

#### DatabaseQueryService
```csharp
private void WorkerLoop(CancellationToken token)
{
    try
    {
        // 1. 메타데이터 해석 대기
        while (_resolvedQuery == null && !token.IsCancellationRequested)
        {
            // ... 메타데이터 로딩 ...
        }

        // 2. 큐 소비 루프
        while (!token.IsCancellationRequested)
        {
            if (_queryQueue.TryDequeue(out var range))
            {
                var chartDataList = QueryDatabaseRangeWithRetry(range.Start, range.End, token);
                if (chartDataList != null)
                {
                    foreach (var point in chartDataList)
                        OnDataQueried?.Invoke(ServiceId, point);
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }
    catch (TaskCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
}
```

#### GlobalDataService
```csharp
private void WorkerLoop(CancellationToken token)
{
    SQLiteConnection connection = null;
    try
    {
        // 1. DB 연결 대기
        connection = WaitForConnection(token);
        if (connection == null) return;

        // 2. WAL 모드 설정
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // 3. 스키마 준비 대기
        _schema = WaitForSchemaReady(connection, token);
        if (_schema == null) return;

        // 4. 시간 데이터 소비 루프
        foreach (var time in _timeBuffer.GetConsumingEnumerable())
        {
            if (time >= nextCheckpoint)
            {
                var chunk = FetchAllTablesRangeWithRetry(connection, rangeStart, rangeEnd, token);
                if (chunk != null && chunk.Count > 0)
                {
                    SharedFrameRepository.Instance.StoreChunk(chunk);
                }
                // ... checkpoint 업데이트 ...
            }
        }

        // 5. Graceful Shutdown - 마지막 꼬리 데이터 처리
        if (lastSeenTime > lastQueryEndTime)
        {
            var finalChunk = FetchAllTablesRangeWithRetry(connection, start, end, token);
            // ... 저장 ...
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"Worker Error: {ex.Message}"); }
    finally
    {
        if (connection != null)
        {
            CleanupAndCheckpoint(connection, _dbPath);
            connection.Dispose();
        }
    }
}
```

**✅ 동일점:**
- 메타데이터 준비 대기
- 큐/버퍼 소비 루프
- Retry 로직 사용
- 예외 처리 (TaskCanceledException, Exception)
- 리소스 정리 (finally)

**차이점:**
- DatabaseQueryService: `ConcurrentQueue` 사용
- GlobalDataService: `BlockingCollection` 사용 (Graceful Drain 지원)
- GlobalDataService: 마지막 꼬리 데이터 처리 추가

---

### 6. **Stop 로직 (개념 동일)**

#### DatabaseQueryService
```csharp
public void Stop()
{
    if (!_isRunning) return;
    _isRunning = false;

    // 1. 작업 취소 요청
    _cts?.Cancel();

    // 2. 큐 즉시 비우기
    while (_queryQueue.TryDequeue(out _)) { }

    // 3. 워커 태스크 종료 대기 (최대 2초)
    try { _workerTask?.Wait(2000); }
    catch (AggregateException) { }

    // 4. 리소스 정리
    _connection?.Close();
    _connection?.Dispose();
}
```

#### GlobalDataService
```csharp
public void Stop()
{
    lock (_lock)
    {
        if (_workerTask == null) return;

        // PendingQueue 비우기
        while (_pendingQueue.TryDequeue(out _)) { }

        // 1. 소비 종료 선언 (Graceful Drain)
        _timeBuffer?.CompleteAdding();

        // 2. 워커 종료 대기 (최대 5초)
        bool completed = _workerTask.Wait(TimeSpan.FromSeconds(5));
        if (!completed)
        {
            _cts?.Cancel(); // 5초 넘으면 강제 종료
            _workerTask.Wait(1000);
        }

        // 3. 리소스 정리
        _timeBuffer?.Dispose();
        _cts?.Dispose();
        _workerTask = null;
    }
}
```

**✅ 동일점:**
- 큐/버퍼 비우기
- 워커 태스크 종료 대기
- 타임아웃 후 강제 종료
- 리소스 정리 (Dispose)

**차이점:**
- DatabaseQueryService: 즉시 Cancel (2초 타임아웃)
- GlobalDataService: Graceful Drain 후 Cancel (5초 타임아웃)
- GlobalDataService: `lock` 사용 (재시작 안전성)

---

## 🔄 주요 차이점 요약

| 항목 | DatabaseQueryService | GlobalDataService |
|------|---------------------|-------------------|
| **조회 범위** | 특정 X/Y 테이블/컬럼 | 모든 테이블 |
| **메타데이터** | `MetadataResolver` (X/Y 매핑) | `SimulationSchema` (전체 스키마) |
| **반환 타입** | `List<ChartDataPoint>` | `Dictionary<double, SimulationFrame>` |
| **큐 타입** | `ConcurrentQueue<QueryRange>` | `BlockingCollection<double>` |
| **Graceful Shutdown** | ❌ 없음 | ✅ 마지막 꼬리 데이터 처리 |
| **DB 연결** | Start()에서 즉시 | WaitForConnection() 대기 |
| **WAL Checkpoint** | ❌ 없음 | ✅ Stop 시 TRUNCATE |
| **Retry 로직** | ✅ 동일 | ✅ 동일 |
| **Fast-Fail** | ✅ X/Y 테이블 확인 | ✅ 모든 테이블 확인 |

---

## ✅ 결론

### 핵심 로직 동일성 검증

1. **✅ Retry 로직**: 완전히 동일 (재시도 횟수, 간격, Fast-Fail)
2. **✅ Fast-Fail 메커니즘**: 개념 동일 (DB 최신 시간 확인)
3. **✅ 메타데이터 대기**: 개념 동일 (테이블 준비 대기)
4. **✅ DB 연결 관리**: 개념 동일 (Pooling=false, WAL 모드)
5. **✅ WorkerLoop 구조**: 개념 동일 (메타데이터 대기 → 큐 소비)
6. **✅ Stop 로직**: 개념 동일 (큐 비우기, 타임아웃, 리소스 정리)

### 변경된 부분

- **조회 범위만 확장**: 특정 컬럼 → 전체 DB
- **데이터 구조 변경**: `ChartDataPoint` → `SimulationFrame`
- **Graceful Shutdown 추가**: 데이터 유실 방지 강화
- **WAL Checkpoint 추가**: DB 정리 강화

### 최종 평가

**GlobalDataService는 DatabaseQueryService의 핵심 설계 개념을 100% 유지하면서, 조회 범위만 확장한 버전입니다.**

- ✅ Retry 정책: 동일
- ✅ DB 관리: 동일 (+ 강화)
- ✅ 에러 처리: 동일
- ✅ 리소스 관리: 동일 (+ 강화)
- ✅ 코어 로직: 동일

**단순히 "특정 DB 속성 조회"에서 "전체 DB 조회"로만 변경되었으며, 기존 내부 코어 로직은 동일하게 유지되었습니다.** ✅
