# 재시도 로직 및 시뮬레이션 종료 감지 가이드

## 🔄 재시도 로직 개요

실시간 DB 조회 환경에서는 **write가 아직 완료되지 않아** 데이터가 없을 수 있습니다.
`DatabaseQueryService`는 이를 처리하기 위해 **자동 재시도 로직**을 제공합니다.

## ⚙️ 설정 방법

### DatabaseQueryConfig에 재시도 설정 추가

```csharp
var config = new DatabaseQueryConfig
{
    TableName = "SimulationData",
    XAxisColumnName = "Temperature",
    YAxisColumnName = "Pressure",
    TimeColumnName = "Time",
    
    // 재시도 설정
    RetryCount = 5,           // 재시도 횟수 (기본값: 3)
    RetryIntervalMs = 20      // 재시도 간격 (기본값: 10ms)
};
```

## 🎯 동작 원리

### 1. 첫 시도
```
시간 0.05초 데이터 조회 시도
↓
데이터 없음 (아직 write 안됨)
↓
재시도 대기 (20ms)
```

### 2. 재시도
```
20ms 후 다시 조회
↓
데이터 있음 → 성공! ✓
```

### 3. 모든 재시도 실패
```
5번 재시도 모두 실패
↓
시뮬레이션 종료로 판단
↓
OnSimulationEnded 이벤트 발생
```

## 📡 시뮬레이션 종료 감지 이벤트

### OnSimulationEnded 이벤트 사용

```csharp
DatabaseQueryService.OnSimulationEnded += (failedTime, retryCount) =>
{
    Console.WriteLine($"[시뮬레이션 종료 감지]");
    Console.WriteLine($"  실패한 시간: {failedTime.TotalSeconds:F2}초");
    Console.WriteLine($"  재시도 횟수: {retryCount}회");
    
    // 시뮬레이션 타이머 정지
    SimulationTimer.Stop();
    
    // UI 업데이트
    MessageBox.Show("시뮬레이션이 종료되었습니다.");
    
    // 서비스 정리
    DatabaseQueryService.Stop();
};
```

## 💡 전체 사용 예시

```csharp
using System;

class Program
{
    static void Main()
    {
        // 1. Config 설정 (재시도 포함)
        var config = new DatabaseQueryConfig
        {
            TableName = "SimulationData",
            XAxisColumnName = "Temperature",
            YAxisColumnName = "Pressure",
            TimeColumnName = "Time",
            RetryCount = 5,        // 5번 재시도
            RetryIntervalMs = 20   // 20ms 간격
        };

        // 2. 이벤트 핸들러 등록
        
        // 조회 성공 시
        DatabaseQueryService.OnDataQueried += (chartData) =>
        {
            Console.WriteLine($"[데이터 조회 성공] X: {chartData.X}, Y: {chartData.Y}");
            // 차트 업데이트
            UpdateChart(chartData);
        };

        // 시뮬레이션 종료 감지 시
        DatabaseQueryService.OnSimulationEnded += (failedTime, retryCount) =>
        {
            Console.WriteLine($"\n[시뮬레이션 종료]");
            Console.WriteLine($"  마지막 조회 시도 시간: {failedTime.TotalSeconds:F2}초");
            Console.WriteLine($"  재시도 횟수: {retryCount}회");
            Console.WriteLine($"  판단: 외부 시뮬레이션이 종료되었습니다.");
            
            // 타이머 정지
            SimulationTimer.Stop();
            DatabaseQueryService.Stop();
        };

        // 타이머 Tick에서 조회 요청
        SimulationTimer.OnTick += (simTime) =>
        {
            DatabaseQueryService.EnqueueQuery(simTime);
        };

        // 3. 서비스 시작
        DatabaseQueryService.Start(config);
        SimulationTimer.Start(1.0);

        Console.WriteLine("서비스 실행 중...");
        Console.WriteLine("외부 시뮬레이션이 종료되면 자동으로 감지됩니다.");
        Console.ReadLine();
    }

    static void UpdateChart(ChartDataPoint data)
    {
        // 차트 업데이트 로직
    }
}
```

## 🔍 재시도 로직 상세

### QueryDatabaseWithRetry 내부 동작

```csharp
// 의사 코드
for (int attempt = 1; attempt <= RetryCount + 1; attempt++)
{
    var result = QueryDatabase(time);
    
    if (result != null)
    {
        // 성공!
        if (attempt > 1)
            Console.WriteLine($"재시도 {attempt}번째에 성공");
        return result;
    }
    
    if (attempt < maxAttempts)
    {
        Thread.Sleep(RetryIntervalMs);  // 대기 후 재시도
    }
}

// 모든 재시도 실패
Console.WriteLine($"{RetryCount + 1}번 시도 후 실패 - 시뮬레이션 종료로 판단");
return null;
```

## 📊 재시도 시나리오 예시

### 시나리오 1: 정상 조회 (재시도 불필요)
```
시간 0.01초 조회
→ 데이터 존재 ✓
→ 즉시 반환
```

### 시나리오 2: 1번 재시도 후 성공
```
시간 0.05초 조회
→ 데이터 없음 ✗
→ 20ms 대기
→ 재시도 (2번째 시도)
→ 데이터 존재 ✓
→ 성공 (로그: "Success after 2 attempts")
```

### 시나리오 3: 시뮬레이션 종료
```
시간 10.50초 조회
→ 데이터 없음 ✗
→ 20ms 대기 → 재시도 (2번째) → 실패
→ 20ms 대기 → 재시도 (3번째) → 실패
→ 20ms 대기 → 재시도 (4번째) → 실패
→ 20ms 대기 → 재시도 (5번째) → 실패
→ 20ms 대기 → 재시도 (6번째) → 실패
→ OnSimulationEnded 이벤트 발생
→ "Failed after 6 attempts - Simulation may have ended"
```

## ⚠️ 주의사항

### 1. 재시도 횟수 설정
```csharp
// 너무 적으면: 정상 데이터도 놓칠 수 있음
RetryCount = 1  // ⚠️ 위험

// 적절한 값: 3~10
RetryCount = 5  // ✓ 권장

// 너무 많으면: 종료 감지가 늦어짐
RetryCount = 100  // ⚠️ 비효율적
```

### 2. 재시도 간격 설정
```csharp
// DB write 주기를 고려하여 설정
// 예: DB가 10ms마다 write → RetryIntervalMs = 10~20

RetryIntervalMs = 10   // 빠른 감지
RetryIntervalMs = 50   // 안정적 감지
```

### 3. 실제 DB 구현 시 주의
```csharp
private static ChartDataPoint QueryDatabase(TimeSpan simulationTime)
{
    string timeKey = simulationTime.TotalSeconds.ToString("F2");
    
    using (var connection = new SqlConnection(connectionString))
    {
        var result = connection.QueryFirstOrDefault<dynamic>(query, new { time = timeKey });
        
        if (result != null)
        {
            return new ChartDataPoint
            {
                X = Convert.ToDouble(result[XAxisColumnName]),
                Y = Convert.ToDouble(result[YAxisColumnName])
            };
        }
        
        // ⚠️ 중요: 데이터가 없으면 반드시 null 반환!
        return null;  // 재시도 로직이 이를 감지
    }
}
```

## 🎛️ 고급 설정

### 시뮬레이션 종료 시 자동 정지

WorkerLoop에서 주석 처리된 부분을 활성화:

```csharp
else
{
    // 재시도 실패 -> 시뮬레이션 종료로 판단
    OnSimulationEnded?.Invoke(simTime, _config.RetryCount);
    
    // 서비스 자동 정지 (활성화)
    Stop();
    break;
}
```

### 재시도 성공 로그 비활성화

```csharp
// QueryDatabaseWithRetry에서 로그 출력 부분 제거
if (attemptCount > 1)
{
    // Console.WriteLine($"Success after {attemptCount} attempts...");
}
```

## 📈 성능 고려사항

### 재시도로 인한 지연 시간
```
RetryCount = 5
RetryIntervalMs = 20

최악의 경우 지연: 5 × 20ms = 100ms
```

### 권장 설정
```csharp
// 10ms 시뮬레이션 주기 기준
var config = new DatabaseQueryConfig
{
    RetryCount = 3,          // 3번 재시도
    RetryIntervalMs = 10     // 10ms 간격
    // 최대 지연: 30ms (시뮬레이션 3틱 분량)
};
```

## 🧪 테스트 방법

### 재시도 로직 테스트

```csharp
// QueryDatabase를 수정하여 의도적으로 null 반환
private static ChartDataPoint QueryDatabase(TimeSpan simulationTime)
{
    // 0.5초 이후 데이터는 없는 것으로 시뮬레이션
    if (simulationTime.TotalSeconds > 0.5)
        return null;
    
    return new ChartDataPoint { X = ..., Y = ... };
}

// 결과: 0.5초 이후 재시도 후 OnSimulationEnded 발생
```
