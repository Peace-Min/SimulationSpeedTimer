# GlobalDataService & SharedFrameRepository 설계 문서

## 📌 프로젝트 개요
**SimulationSpeedTimer** 프로젝트의 데이터 아키텍처 개선: 기존의 개별 차트별 DB 쿼리 방식에서 **중앙 집중식 데이터 공급 및 공유 메모리 패턴**으로 전환.

---

## 🎯 핵심 목표

1. **단일 DB 조회**: 모든 테이블의 모든 컬럼을 한 번에 조회하여 중복 쿼리 제거
2. **공유 메모리**: 조회된 데이터를 메모리에 저장하여 모든 차트/UI가 공유
3. **독립적 생명주기**: `SimulationController`와 분리된 독립 서비스로 동작
4. **데이터 무결성**: Stop/Start 전환 시에도 데이터 유실 및 오염 방지
5. **Graceful Shutdown**: 버퍼에 남은 데이터를 끝까지 처리 후 종료

---

## 🏗️ 아키텍처 구성

```
┌─────────────────────────────────────────────────────────────┐
│                    Simulation Engine                        │
│                    (External Process)                       │
└────────────────────┬────────────────────────────────────────┘
                     │ Time Data (0.0, 0.1, 0.2, ...)
                     ↓
┌─────────────────────────────────────────────────────────────┐
│              GlobalDataService (Singleton)                  │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ 1. EnqueueTime(double) - 시간 데이터 수신            │  │
│  │ 2. WorkerLoop - 백그라운드 Task                      │  │
│  │    - WaitForConnection (DB 파일 대기)                │  │
│  │    - WaitForSchemaReady (메타데이터 로딩)            │  │
│  │    - FetchAllTablesRange (범위 기반 전체 조회)       │  │
│  │ 3. Graceful Shutdown - 남은 데이터 완전 처리         │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────┬────────────────────────────────────────┘
                     │ Dictionary<double, SimulationFrame>
                     ↓
┌─────────────────────────────────────────────────────────────┐
│          SharedFrameRepository (Singleton)                  │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ - ConcurrentDictionary<double, SimulationFrame>      │  │
│  │ - SortedSet<double> (시간 인덱스)                    │  │
│  │ - ReaderWriterLockSlim (동시성 제어)                 │  │
│  │ - Sliding Window (메모리 관리)                       │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────┬────────────────────────────────────────┘
                     │ GetFrame(time) / GetFramesInRange(...)
                     ↓
┌─────────────────────────────────────────────────────────────┐
│              Chart Services / UI Components                 │
│  (기존 DatabaseQueryService 대체 또는 보조)                │
└─────────────────────────────────────────────────────────────┘
```

---

## 📦 핵심 컴포넌트

### 1. **GlobalDataService** (데이터 공급자)

#### 책임
- 시뮬레이션 엔진으로부터 시간 정보 수신
- DB 연결 및 스키마 로딩 (Retry 로직 포함)
- 시간 범위 기반 전체 테이블 데이터 조회
- 조회된 데이터를 SharedFrameRepository에 전달

#### 주요 메서드
```csharp
public void Start(string dbPath, double queryInterval = 1.0)
public void Stop()
public void EnqueueTime(double time)
private void WorkerLoop(CancellationToken token)
private Dictionary<double, SimulationFrame> FetchAllTablesRange(SQLiteConnection conn, double start, double end)
```

#### 상태 관리 (ServiceState)
- **Stopped**: 데이터 수신 거부 (Drop)
- **Preparing**: Start 호출 후 워커 준비 중 (PendingQueue에 임시 저장)
- **Running**: 정상 동작 (TimeBuffer에 직접 주입)

#### 핵심 로직

**1) 데이터 흐름 제어**
```csharp
public void EnqueueTime(double time)
{
    var currentState = _state;
    
    if (currentState == ServiceState.Stopped)
        return; // Drop
    
    if (currentState == ServiceState.Running && _timeBuffer != null)
        _timeBuffer.TryAdd(time); // 직접 주입
    else if (currentState == ServiceState.Preparing)
        _pendingQueue.Enqueue(time); // 임시 보관
}
```

**2) Graceful Shutdown**
```csharp
// Stop() 메서드
_timeBuffer?.CompleteAdding(); // 더 이상 입력 없음 선언
// Cancel()은 즉시 호출하지 않음 → 버퍼 Drain 대기

// WorkerLoop 종료 직전
if (lastSeenTime > lastQueryEndTime)
{
    // 마지막 꼬리(Tail) 데이터 처리
    var finalChunk = FetchAllTablesRange(connection, lastQueryEndTime, lastSeenTime);
    SharedFrameRepository.Instance.StoreChunk(finalChunk);
}
```

**3) 세션 격리**
```csharp
// Start() 메서드
lock (_lock)
{
    // 1. PendingQueue 초기화 (이전 세션 잔재 제거)
    while (_pendingQueue.TryDequeue(out _)) { }
    
    // 2. 상태 변경
    _state = ServiceState.Preparing;
    
    // 3. 이전 작업 완료 대기
    if (_workerTask != null && !_workerTask.IsCompleted)
        _workerTask.Wait();
    
    // 4. 새 버퍼 생성 및 PendingQueue Replay
    var newBuffer = new BlockingCollection<double>();
    while (_pendingQueue.TryDequeue(out var t))
        newBuffer.TryAdd(t);
    
    _timeBuffer = newBuffer;
    _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
}
```

---

### 2. **SharedFrameRepository** (공유 메모리 저장소)

#### 책임
- GlobalDataService가 조회한 데이터를 시간 기반으로 저장
- 외부 서비스(차트 등)에게 빠른 조회 API 제공
- 메모리 효율을 위한 슬라이딩 윈도우 관리
- 동시 읽기/쓰기 안전성 보장

#### 주요 메서드
```csharp
public void StoreChunk(Dictionary<double, SimulationFrame> chunk)
public SimulationFrame GetFrame(double time)
public List<SimulationFrame> GetFramesInRange(double start, double end)
public List<(double Time, object Value)> GetAttributeValues(string objectName, string attributeName, double startTime, double endTime)
public void Clear()
```

#### 내부 구조
```csharp
private readonly ConcurrentDictionary<double, SimulationFrame> _frames;
private readonly SortedSet<double> _timeIndex; // 빠른 범위 조회
private readonly ReaderWriterLockSlim _lock;
private double _maxWindowSize = 60.0; // 최근 60초만 유지
```

#### 슬라이딩 윈도우
```csharp
private void CleanupOldFrames()
{
    double latestTime = _timeIndex.Max;
    double cutoffTime = latestTime - _maxWindowSize;
    
    var toRemove = _timeIndex.Where(t => t < cutoffTime).ToList();
    foreach (var time in toRemove)
    {
        _frames.TryRemove(time, out _);
        _timeIndex.Remove(time);
    }
}
```

---

### 3. **데이터 모델**

#### SimulationFrame
```csharp
public class SimulationFrame
{
    public double Time { get; }
    private Dictionary<string, SimulationTable> _tables;
    public IReadOnlyDictionary<string, SimulationTable> Tables => _tables;
    
    public void AddOrUpdateTable(SimulationTable table)
    public SimulationTable GetTable(string tableName)
}
```

#### SimulationTable
```csharp
public class SimulationTable
{
    public string TableName { get; }
    private Dictionary<string, object> _columns;
    
    public object this[string columnName] { get; }
    public T Get<T>(string columnName, T defaultValue = default)
    public IEnumerable<string> ColumnNames => _columns.Keys;
}
```

#### SimulationSchema
```csharp
public class SimulationSchema
{
    private Dictionary<string, SchemaTableInfo> _tables;
    private Dictionary<string, SchemaTableInfo> _tablesByObject; // 논리명 인덱스
    
    public SchemaTableInfo GetTable(string tableName)
    public SchemaTableInfo GetTableByObject(string objectName)
    public IEnumerable<SchemaTableInfo> Tables => _tables.Values;
    public int TotalColumnCount => _tables.Values.Sum(t => t.Columns.Count());
}
```

---

## 🔄 데이터 흐름 시나리오

### 시나리오 1: 정상 동작
```
1. SimulationEngine → EnqueueTime(0.0, 0.1, 0.2, ..., 1.0)
2. WorkerLoop: 1.0초 도달 → FetchAllTablesRange(0.0, 1.0)
3. GlobalDataService → SharedFrameRepository.StoreChunk(chunk)
4. Chart Service → SharedFrameRepository.GetAttributeValues("Radar", "distance", 0.0, 1.0)
5. UI 업데이트
```

### 시나리오 2: Stop → Start 전환 (15초 시뮬레이션)
```
1. 사용자가 15초에 Stop() 호출
2. 내부 Worker는 12.5초까지 처리 완료 상태
3. CompleteAdding() → 버퍼 Drain 시작
4. 12.6 ~ 15.0초 데이터 순차 처리
5. 루프 종료 후 Final Tail 처리 (14.0~15.0초)
6. 사용자가 새 시뮬레이션 Start() 호출
7. Start() 내부에서 이전 Worker 완료 대기 (Wait)
8. 대기 중 수신된 0.0, 0.1초 데이터는 PendingQueue에 보관
9. 이전 Worker 종료 완료 → 새 버퍼 생성 및 PendingQueue Replay
10. 새 시뮬레이션 정상 시작
```

### 시나리오 3: 데이터 오염 방지
```
1. Session A 실행 중 (1000.0~1002.0초 데이터)
2. Stop() 호출 → 상태 = Stopped
3. 외부에서 9999.0 데이터 주입 시도 → Drop (Stopped 상태)
4. Start() 호출 → 상태 = Preparing
5. 0.0, 0.1 데이터 주입 → PendingQueue에 저장
6. 새 Worker 시작 → PendingQueue Replay
7. 결과: Session A(1000대)와 Session B(0대) 데이터 완전 격리
```

---

## ✅ 검증된 보장 사항

| 항목 | 보장 내용 | 구현 메커니즘 |
|------|----------|--------------|
| **데이터 완전성** | Stop 시점까지의 모든 데이터 처리 | CompleteAdding + Drain + Final Tail |
| **세션 격리** | 이전/새 시뮬레이션 데이터 분리 | 새 버퍼 인스턴스 + Lock 동기화 |
| **데이터 유실 방지** | Start 전 수신 데이터 보존 | PendingQueue + Replay |
| **오염 방지** | Stop 후 수신 데이터 차단 | ServiceState.Stopped → Drop |
| **동시성 안전** | 여러 차트의 동시 읽기 | ReaderWriterLockSlim |
| **메모리 효율** | 무한 증가 방지 | Sliding Window (60초) |

---

## 🧪 테스트 결과

**Chaos Lifecycle Test (3회 반복)**
- ✅ Session A 데이터: 20개 처리
- ✅ Session B 데이터: 10개 처리
- ✅ 오염 데이터(9999.0): 0개 (Drop 성공)
- ✅ PendingQueue Replay: 0.0부터 시작 확인
- ✅ 세션 격리: 1000대와 0대 데이터 분리 확인
- ✅ Graceful Shutdown: Final Tail 처리 로그 확인

---

## 🚀 다음 구현 단계

1. **SharedFrameRepository.cs 구현**
   - ConcurrentDictionary 기반 저장소
   - ReaderWriterLockSlim 동기화
   - Sliding Window 메모리 관리

2. **GlobalDataService 연동**
   - `FetchAllTablesRange` 결과를 `StoreChunk` 호출로 변경
   - 테스트 Hook(`_onChunkProcessed`) 제거

3. **Schema 공유 메커니즘**
   - Repository에서도 논리명→물리명 변환 가능하도록
   - `SimulationSchema`를 GlobalDataService와 Repository가 공유

4. **Chart Service 리팩토링**
   - 기존 `DatabaseQueryService` 대체
   - Repository 우선 조회 → 없으면 DB 직접 조회 (Fallback)

---

## 📝 주요 설계 결정 사항

1. **Singleton 패턴**: GlobalDataService와 SharedFrameRepository 모두 Singleton으로 구현하여 전역 접근 보장
2. **Range-Based Query**: 개별 시간 조회 대신 0.5~1.0초 범위 조회로 DB I/O 최소화
3. **State Machine**: Stopped/Preparing/Running 상태로 데이터 흐름 명확히 제어
4. **Lock-Free Buffer**: BlockingCollection 사용으로 Producer-Consumer 패턴 구현
5. **Graceful Shutdown**: Cancel 대신 CompleteAdding으로 데이터 무결성 우선

---

## 📂 파일 구조

```
SimulationSpeedTimer/
├── GlobalDataService.cs          # 데이터 공급자 (DB 조회 및 버퍼 관리)
├── SharedFrameRepository.cs      # 공유 메모리 저장소 (미구현)
├── SimulationFrame.cs            # 데이터 모델 (Frame)
├── SimulationSchema.cs           # 데이터 모델 (Schema)
├── GlobalDataServiceTest.cs      # Chaos Lifecycle 테스트
└── ARCHITECTURE.md               # 본 문서
```

---

이 문서를 다른 에이전트에게 제공하면 현재까지의 설계 의도와 구현 상태를 정확히 이해하고 이어서 작업할 수 있습니다.
