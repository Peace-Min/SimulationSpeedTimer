# SQLite 연결 설정 가이드

## 📦 System.Data.SQLite 설치

### Visual Studio에서 설치

1. **솔루션 탐색기**에서 프로젝트 우클릭
2. **NuGet 패키지 관리** 선택
3. **찾아보기** 탭에서 `System.Data.SQLite` 검색
4. **설치** 클릭

또는 **패키지 관리자 콘솔**에서:
```powershell
Install-Package System.Data.SQLite
```

---

## 🔧 설치 후 작업

### 1. DatabaseQueryService.cs 수정

파일 상단의 주석 해제:
```csharp
// 변경 전
// TODO: NuGet에서 System.Data.SQLite 설치 필요
// using System.Data.SQLite;

// 변경 후
using System.Data.SQLite;
```

### 2. 연결 필드 타입 변경

```csharp
// 변경 전
private static object _connection;  // SQLiteConnection (NuGet 설치 후 타입 변경)

// 변경 후
private static SQLiteConnection _connection;  // SQLite 연결 재사용
```

### 3. Start 메서드 주석 해제

`Start()` 메서드 내부의 SQLite 연결 코드 주석 해제:

```csharp
// TODO: NuGet에서 System.Data.SQLite 설치 후 아래 주석 해제
/*  <-- 이 부분 제거
// SQLite 연결 생성 (WAL 모드 최적화)
var connectionString = new SQLiteConnectionStringBuilder
{
    DataSource = _config.DatabasePath,
    ReadOnly = true,           // Read 전용
    Pooling = false,           // SQLite는 풀링 불필요
    JournalMode = SQLiteJournalModeEnum.Wal  // WAL 모드 명시
}.ToString();

_connection = new SQLiteConnection(connectionString);
_connection.Open();

// WAL 모드 확인
using (var cmd = _connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA journal_mode;";
    var mode = cmd.ExecuteScalar()?.ToString();
    Console.WriteLine($"[DB] Journal Mode: {mode}");
    
    if (mode?.ToLower() != "wal")
    {
        Console.WriteLine("[경고] WAL 모드가 아닙니다. 성능 저하 가능.");
    }
}
*/  <-- 이 부분 제거
```

### 4. Stop 메서드 주석 해제

`Stop()` 메서드 내부의 연결 닫기 코드 주석 해제:

```csharp
// TODO: NuGet에서 System.Data.SQLite 설치 후 아래 주석 해제
/*  <-- 이 부분 제거
// SQLite 연결 닫기
try
{
    _connection?.Close();
    _connection?.Dispose();
}
catch (Exception ex)
{
    Console.WriteLine($"[DB] 연결 종료 중 오류: {ex.Message}");
}
_connection = null;
*/  <-- 이 부분 제거
```

### 5. QueryDatabase 메서드 주석 해제

`QueryDatabase()` 메서드의 SQLite 코드 주석 해제하고 더미 데이터 제거:

```csharp
private static ChartDataPoint QueryDatabase(TimeSpan simulationTime)
{
    string timeKey = simulationTime.TotalSeconds.ToString("F2");

    // TODO: NuGet에서 System.Data.SQLite 설치 후 아래 주석 해제
    /*  <-- 이 부분 제거
    // SQLite 연결 재사용 방식 (WAL 모드 최적화)
    using (var command = _connection.CreateCommand())
    {
        command.CommandText = 
            $"SELECT {_config.XAxisColumnName}, {_config.YAxisColumnName} " +
            $"FROM {_config.TableName} " +
            $"WHERE {_config.TimeColumnName} = @time";
        
        command.Parameters.AddWithValue("@time", timeKey);
        
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                return new ChartDataPoint
                {
                    X = reader.GetDouble(0),
                    Y = reader.GetDouble(1)
                };
            }
        }
    }
    
    // 데이터 없음 (재시도 로직이 처리)
    return null;
    */  <-- 이 부분 제거

    // 아래 더미 데이터 코드 제거
    // return new ChartDataPoint { ... };
}
```

---

## 🎯 사용 예시

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"C:\Data\simulation.db",  // SQLite DB 파일 경로
    TableName = "SimulationData",
    XAxisColumnName = "Temperature",
    YAxisColumnName = "Pressure",
    TimeColumnName = "Time",
    RetryCount = 5,
    RetryIntervalMs = 20
};

DatabaseQueryService.Start(config);
SimulationTimer.Start(1.0);
```

---

## ✅ WAL 모드 설정 확인

외부 시뮬레이션 프로그램에서 DB를 생성할 때 WAL 모드로 설정해야 합니다:

```sql
PRAGMA journal_mode=WAL;
```

또는 C# 코드에서:
```csharp
using (var connection = new SQLiteConnection($"Data Source={dbPath}"))
{
    connection.Open();
    using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }
}
```

---

## 🔍 WAL 모드 장점 (SQLite 전용)

### 1. 동시 Read/Write 가능
```
Writer (외부 시뮬레이션)
  ↓ Write 중...
  
Reader (DatabaseQueryService)
  ↓ Read 가능! (블로킹 없음) ✅
```

### 2. 연결 재사용의 이점
- 파일 핸들 오버헤드 감소
- WAL 체크포인트 효율성 증가
- 락 경합 없음 (WAL이 처리)

### 3. 성능 비교

```
[연결 재사용 - WAL 모드]
Open: 1회 (50ms)
Query: 0.1ms × 1000 = 100ms
Total: 150ms ✅

[매번 Open/Close - WAL 모드]
Open/Close: 5ms × 1000 = 5000ms
Query: 0.1ms × 1000 = 100ms
Total: 5100ms ⚠️

→ 약 34배 차이!
```

---

## ⚠️ 주의사항

1. **ReadOnly 모드**: Read 전용으로 연결하여 안전성 확보
2. **WAL 모드 필수**: 외부 시뮬레이션에서 WAL 모드로 DB 생성 필요
3. **파일 경로**: DatabasePath에 정확한 .db 파일 경로 지정
4. **컬럼 타입**: X, Y축 컬럼은 숫자 타입(REAL, INTEGER)이어야 함

---

## 🧪 테스트

설치 후 빌드하여 경고가 사라지는지 확인:
```
빌드했습니다.
    경고 0개  ✅
    오류 0개
```

실행 시 콘솔에서 WAL 모드 확인:
```
[DB] Journal Mode: wal  ✅
```
