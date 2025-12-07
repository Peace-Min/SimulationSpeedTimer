# DatabaseQueryService 사용 가이드

## 📋 개요

`DatabaseQueryService`는 시뮬레이션 타이머와 연동하여 DB 조회를 백그라운드에서 처리하는 정적 서비스입니다.

## 🎯 주요 특징

- **비동기 DB 조회**: 타이머 Tick에서 시간 정보를 큐에 넣으면 백그라운드 워커가 처리
- **동적 쿼리**: 사용자가 선택한 테이블명과 X/Y축 컬럼으로 쿼리 생성
- **시간 키 변환**: TimeSpan을 0.01초 단위 문자열로 변환하여 WHERE 조건에 사용
- **차트 데이터 반환**: 조회 결과를 `ChartDataPoint` (X, Y 값)로 반환

## 📦 필요한 클래스

### 1. DatabaseQueryConfig
```csharp
public class DatabaseQueryConfig
{
    public string TableName { get; set; }           // 조회할 테이블명
    public string XAxisColumnName { get; set; }     // X축 컬럼명
    public string YAxisColumnName { get; set; }     // Y축 컬럼명
    public string TimeColumnName { get; set; }      // 시간 컬럼명 (기본: "Time")
}
```

### 2. ChartDataPoint
```csharp
public class ChartDataPoint
{
    public double X { get; set; }  // X축 값
    public double Y { get; set; }  // Y축 값
}
```

## 🚀 사용 방법

### 1단계: Config 설정

```csharp
var dbConfig = new DatabaseQueryConfig
{
    TableName = "SimulationData",      // DB 테이블명
    XAxisColumnName = "Temperature",   // 사용자가 선택한 X축 컬럼
    YAxisColumnName = "Pressure",      // 사용자가 선택한 Y축 컬럼
    TimeColumnName = "Time"            // 기본키 컬럼 (WHERE 조건용)
};
```

### 2단계: 이벤트 핸들러 등록

```csharp
// DB 조회 결과를 받는 이벤트 핸들러
DatabaseQueryService.OnDataQueried += (chartData) =>
{
    Console.WriteLine($"X: {chartData.X}, Y: {chartData.Y}");
    
    // 차트 서비스로 데이터 전송 (메신저 사용 예시)
    // Messenger.Send(chartData);
};

// 타이머 Tick에서 조회 요청 큐에 추가
SimulationTimer.OnTick += (simTime) =>
{
    DatabaseQueryService.EnqueueQuery(simTime);
};
```

### 3단계: 서비스 시작

```csharp
DatabaseQueryService.Start(dbConfig);
SimulationTimer.Start(1.0);  // 1배속으로 시작
```

### 4단계: 서비스 정지

```csharp
SimulationTimer.Stop();
DatabaseQueryService.Stop();
```

## 🔍 내부 동작 원리

### TimeSpan → 문자열 변환

```csharp
TimeSpan simTime = TimeSpan.FromSeconds(1.23);
string timeKey = simTime.TotalSeconds.ToString("F2");  // "1.23"
```

### 동적 쿼리 생성

```csharp
// Config 정보를 사용하여 쿼리 생성
string query = $"SELECT {config.XAxisColumnName}, {config.YAxisColumnName} " +
               $"FROM {config.TableName} " +
               $"WHERE {config.TimeColumnName} = @time";

// 예시: SELECT Temperature, Pressure FROM SimulationData WHERE Time = '1.23'
```

### 실제 DB 연결 구현 예시

`DatabaseQueryService.cs`의 `QueryDatabase` 메서드를 수정하세요:

```csharp
private static ChartDataPoint QueryDatabase(TimeSpan simulationTime)
{
    // TimeSpan을 0.01초 단위 문자열로 변환
    string timeKey = simulationTime.TotalSeconds.ToString("F2");

    // 동적 쿼리 생성
    string query = $"SELECT {_config.XAxisColumnName}, {_config.YAxisColumnName} " +
                  $"FROM {_config.TableName} " +
                  $"WHERE {_config.TimeColumnName} = @time";

    // Dapper 사용 예시
    using (var connection = new SqlConnection(connectionString))
    {
        var result = connection.QueryFirstOrDefault<dynamic>(query, new { time = timeKey });
        
        if (result != null)
        {
            return new ChartDataPoint
            {
                X = Convert.ToDouble(result[_config.XAxisColumnName]),
                Y = Convert.ToDouble(result[_config.YAxisColumnName])
            };
        }
    }
    
    return null;
}
```

## 📊 전체 사용 예시

```csharp
using System;

class Program
{
    static void Main()
    {
        // 1. Config 설정
        var dbConfig = new DatabaseQueryConfig
        {
            TableName = "SimulationData",
            XAxisColumnName = "Temperature",
            YAxisColumnName = "Pressure",
            TimeColumnName = "Time"
        };

        // 2. 이벤트 핸들러 등록
        DatabaseQueryService.OnDataQueried += (chartData) =>
        {
            Console.WriteLine($"Time: {SimulationTimer.CurrentTime}, " +
                            $"X: {chartData.X}, Y: {chartData.Y}");
        };

        SimulationTimer.OnTick += (simTime) =>
        {
            DatabaseQueryService.EnqueueQuery(simTime);
        };

        // 3. 서비스 시작
        DatabaseQueryService.Start(dbConfig);
        SimulationTimer.Start(1.0);

        Console.WriteLine("서비스 실행 중... (Enter 키를 누르면 종료)");
        Console.ReadLine();

        // 4. 서비스 정지
        SimulationTimer.Stop();
        DatabaseQueryService.Stop();
        
        Console.WriteLine("서비스 종료됨");
    }
}
```

## ⚙️ 고급 사용법

### 런타임에 X/Y축 변경

```csharp
// 서비스 정지
DatabaseQueryService.Stop();

// 새로운 Config로 재시작
var newConfig = new DatabaseQueryConfig
{
    TableName = "SimulationData",
    XAxisColumnName = "Velocity",    // X축 변경
    YAxisColumnName = "Acceleration" // Y축 변경
};

DatabaseQueryService.Start(newConfig);
```

### 큐 상태 모니터링

```csharp
Console.WriteLine($"대기 중인 조회 요청: {DatabaseQueryService.QueueCount}");
Console.WriteLine($"서비스 실행 상태: {DatabaseQueryService.IsRunning}");
```

## 🛡️ 주의사항

1. **Config 유효성**: `TableName`, `XAxisColumnName`, `YAxisColumnName`은 필수입니다.
2. **시간 형식**: DB의 시간 컬럼은 "0.01", "0.02", "1.23" 형식의 문자열이어야 합니다.
3. **리소스 정리**: 애플리케이션 종료 시 반드시 `Stop()`을 호출하세요.
4. **이벤트 핸들러**: `Stop()` 호출 시 모든 이벤트 핸들러가 제거됩니다.

## 📝 DB 테이블 예시

```sql
CREATE TABLE SimulationData (
    Time VARCHAR(10) PRIMARY KEY,  -- "0.01", "0.02", "1.23" 형식
    Temperature FLOAT,
    Pressure FLOAT,
    Velocity FLOAT,
    Acceleration FLOAT
);

-- 데이터 예시
INSERT INTO SimulationData VALUES ('0.01', 25.3, 101.2, 10.5, 2.1);
INSERT INTO SimulationData VALUES ('0.02', 25.5, 101.3, 10.7, 2.2);
```
