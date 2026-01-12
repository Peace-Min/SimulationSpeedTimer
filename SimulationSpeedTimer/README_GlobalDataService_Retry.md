# GlobalDataService Retry 로직 가이드

## 🔄 개요

`GlobalDataService`는 **전체 DB 조회** 기능을 담당하며, `DatabaseQueryService`와 **동일한 retry 로직 및 설계 개념**을 적용합니다.

실시간 DB 조회 환경에서는 **write가 아직 완료되지 않아** 데이터가 없을 수 있으므로, 자동 재시도 로직을 제공합니다.

## ⚙️ 설정 방법

### GlobalDataService.Start()에 retry 설정 전달

```csharp
GlobalDataService.Instance.Start(
    dbPath: "simulation.db",
    queryInterval: 1.0,      // 조회 간격 (초)
    retryCount: 5,           // 재시도 횟수 (기본값: 3)
    retryIntervalMs: 20      // 재시도 간격 (기본값: 10ms)
);
```

## 🎯 동작 원리

### 1. 첫 시도
```
시간 0.0~1.0초 범위 데이터 조회 시도
↓
데이터 없음 (아직 write 안됨)
↓
Fast-Fail 확인
```

### 2. Fast-Fail 메커니즘
```
DB의 최신 s_time 조회
↓
최신 시간 >= 요청 구간 끝?
  YES → 데이터 없는 구간으로 확정 (재시도 안함)
  NO  → 재시도 (write 대기 중)
```

### 3. 재시도
```
20ms 후 다시 조회
↓
데이터 있음 → 성공! ✓
```

### 4. 모든 재시도 실패
```
5번 재시도 모두 실패
↓
시뮬레이션 종료로 판단
↓
로그 출력: "No data found after 6 attempts - Simulation may have ended"
```

## 📡 핵심 메서드

### FetchAllTablesRangeWithRetry
```csharp
private Dictionary<double, SimulationFrame> FetchAllTablesRangeWithRetry(
    SQLiteConnection conn, 
    double start, 
    double end, 
    CancellationToken token)
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
                Console.WriteLine($"Data found after {attemptCount} attempts");
            return result;
        }

        // Fast-Fail: DB 최신 시간 확인
        double maxTime = GetMaxTimeFromDB(conn);
        if (maxTime >= end) // 이미 지나간 구간
        {
            return result; // 재시도 없이 종료
        }

        if (attemptCount < maxAttempts)
        {
            Thread.Sleep(_retryIntervalMs);
        }
    }

    Console.WriteLine($"No data found after {maxAttempts} attempts - Simulation may have ended");
    return new Dictionary<double, SimulationFrame>();
}
```

### GetMaxTimeFromDB
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
                {
                    maxTime = tableMaxTime;
                }
            }
        }
    }

    return maxTime;
}
```

## 💡 사용 예시

```csharp
// 1. 서비스 시작 (retry 설정 포함)
GlobalDataService.Instance.Start(
    dbPath: "simulation.db",
    queryInterval: 1.0,
    retryCount: 5,        // 5번 재시도
    retryIntervalMs: 20   // 20ms 간격
);

// 2. 시간 데이터 입력
SimulationTimer.OnTick += (simTime) =>
{
    GlobalDataService.Instance.EnqueueTime(simTime);
};

// 3. 데이터 소비 (SimulationController가 자동 처리)
SimulationController.Instance.OnDataUpdated += (time, x, y) =>
{
    Console.WriteLine($"Time: {time:F2}s, X: {x}, Y: {y}");
};

// 4. 서비스 시작
SimulationContext.Instance.Start();
SimulationTimer.Start(1.0);
```

## 🔍 재시도 시나리오 예시

### 시나리오 1: 정상 조회 (재시도 불필요)
```
시간 0.0~1.0초 조회
→ 데이터 존재 ✓
→ 즉시 반환
```

### 시나리오 2: 1번 재시도 후 성공
```
시간 1.0~2.0초 조회
→ 데이터 없음 ✗
→ DB 최신 시간: 1.5초 (< 2.0초) → 재시도 필요
→ 20ms 대기
→ 재시도 (2번째 시도)
→ 데이터 존재 ✓
→ 성공 (로그: "Data found after 2 attempts")
```

### 시나리오 3: Fast-Fail (데이터 없는 구간)
```
시간 10.0~11.0초 조회
→ 데이터 없음 ✗
→ DB 최신 시간: 15.0초 (>= 11.0초) → 이미 지나간 구간
→ 재시도 없이 즉시 종료
```

### 시나리오 4: 시뮬레이션 종료
```
시간 20.0~21.0초 조회
→ 데이터 없음 ✗
→ DB 최신 시간: 19.5초 (< 21.0초) → 재시도 필요
→ 20ms 대기 → 재시도 (2번째) → 실패
→ 20ms 대기 → 재시도 (3번째) → 실패
→ 20ms 대기 → 재시도 (4번째) → 실패
→ 20ms 대기 → 재시도 (5번째) → 실패
→ 20ms 대기 → 재시도 (6번째) → 실패
→ "No data found after 6 attempts - Simulation may have ended"
```

## ⚠️ 주의사항

### 1. 재시도 횟수 설정
```csharp
// 너무 적으면: 정상 데이터도 놓칠 수 있음
retryCount: 1  // ⚠️ 위험

// 적절한 값: 3~10
retryCount: 5  // ✓ 권장

// 너무 많으면: 종료 감지가 늦어짐
retryCount: 100  // ⚠️ 비효율적
```

### 2. 재시도 간격 설정
```csharp
// DB write 주기를 고려하여 설정
// 예: DB가 10ms마다 write → retryIntervalMs = 10~20

retryIntervalMs: 10   // 빠른 감지
retryIntervalMs: 50   // 안정적 감지
```

### 3. DatabaseQueryService와의 차이점

| 항목 | DatabaseQueryService | GlobalDataService |
|------|---------------------|-------------------|
| 조회 범위 | 특정 테이블/컬럼 | 모든 테이블 |
| Retry 로직 | ✅ 동일 | ✅ 동일 |
| Fast-Fail | ✅ X/Y축 테이블 각각 확인 | ✅ 모든 테이블 확인 |
| 설정 방식 | `DatabaseQueryConfig` | `Start()` 파라미터 |

## 📈 성능 고려사항

### 재시도로 인한 지연 시간
```
retryCount = 5
retryIntervalMs = 20

최악의 경우 지연: 5 × 20ms = 100ms
```

### 권장 설정
```csharp
// 10ms 시뮬레이션 주기 기준
GlobalDataService.Instance.Start(
    dbPath: "simulation.db",
    queryInterval: 1.0,
    retryCount: 3,          // 3번 재시도
    retryIntervalMs: 10     // 10ms 간격
    // 최대 지연: 30ms (시뮬레이션 3틱 분량)
);
```

## 🧪 테스트 방법

### Retry 로직 테스트
```csharp
// GlobalDataServiceTest.cs에서 확인
GlobalDataService.Instance.Start(dbPath, 0.5, retryCount: 5, retryIntervalMs: 20);

// 시간 데이터 입력
for (int k = 0; k < 5; k++)
{
    GlobalDataService.Instance.EnqueueTime(k * 0.1);
}

// 결과: 데이터가 없으면 재시도 후 성공 또는 시뮬레이션 종료 감지
```

## 📚 관련 문서

- `README_RetryLogic.md`: DatabaseQueryService의 retry 로직 (동일한 설계)
- `README_DatabaseQueryService.md`: 개별 쿼리 서비스 가이드
- `ARCHITECTURE.md`: 전체 아키텍처 설명
