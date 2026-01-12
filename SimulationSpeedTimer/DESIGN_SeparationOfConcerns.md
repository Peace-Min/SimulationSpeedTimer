# GlobalDataService 설계 원칙: 관심사의 분리

## 🎯 핵심 설계 원칙

**GlobalDataService는 데이터 유무를 판단하지 않고, 무조건 Repository에 저장하고 이벤트를 발생시킵니다.**

**데이터 null 여부 판단 및 사용 여부 결정은 SimulationController의 책임입니다.**

## 📋 책임 분리 (Separation of Concerns)

### GlobalDataService의 책임
1. ✅ DB 조회 (retry 로직 포함)
2. ✅ 조회 결과를 Repository에 저장 (데이터 유무 판단 **안함**)
3. ✅ 이벤트 발생 (무조건)

### SimulationController의 책임
1. ✅ 이벤트 수신
2. ✅ 데이터 null 여부 판단
3. ✅ 비즈니스 로직 처리 (사용 여부 결정)

## 🔄 데이터 흐름

```
시뮬레이션 시간 발생
        ↓
GlobalDataService.EnqueueTime(time)
        ↓
FetchAllTablesRangeWithRetry(start, end)
        ↓
    ┌───┴───┐
    ↓       ↓
retry 성공  retry 실패
    ↓       ↓
chunk(data) chunk(empty)
    ↓       ↓
    └───┬───┘
        ↓
Repository.StoreChunk(chunk) ← 무조건 저장!
        ↓
OnFramesAdded 이벤트 발생 ← 무조건 발생!
        ↓
SimulationController.HandleNewFrames()
        ↓
    ┌───┴───┐
    ↓       ↓
frame.IsEmpty?
    ↓       ↓
   NO      YES
    ↓       ↓
ProcessFrame  ProcessEmptyFrame
(데이터 사용)  (마지막 값 유지 or 종료)
```

## 💻 구현 코드

### GlobalDataService.WorkerLoop

```csharp
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
        if (chunk == null)
        {
            chunk = new Dictionary<double, SimulationFrame>();
        }
        
        // 데이터 저장 및 이벤트 발생 (빈 chunk도 저장)
        SharedFrameRepository.Instance.StoreChunk(chunk);
        _onChunkProcessed?.Invoke(chunk); // 테스트용

        lastQueryEndTime = nextCheckpoint;
        while (time >= nextCheckpoint)
        {
            nextCheckpoint += _queryInterval;
        }
    }
}
```

### SimulationController.HandleNewFrames

```csharp
private void HandleNewFrames(List<SimulationFrame> frames, Guid sessionId)
{
    // 1. 세션 ID 검증
    if (sessionId != _currentSessionId) return;

    // 2. 메타데이터 해석
    if (!_isResolved)
    {
        if (SharedFrameRepository.Instance.Schema != null)
        {
            ResolveMetadata();
            _isResolved = true;
        }
        else return;
    }

    // 3. 데이터 처리 (null 여부 판단)
    foreach (var frame in frames)
    {
        if (frame.IsEmpty)
        {
            // 빈 Frame → 마지막 값 유지 또는 시뮬레이션 종료
            ProcessEmptyFrame(frame);
        }
        else
        {
            // 데이터 있는 Frame → 정상 처리
            ProcessFrame(frame);
        }
    }
}

private void ProcessEmptyFrame(SimulationFrame frame)
{
    // 비즈니스 로직: 마지막 값 유지 또는 시뮬레이션 종료 판단
    foreach (var query in _resolvedQueries)
    {
        string key = $"{query.YTableName}.{query.YColumnName}";
        
        if (_lastKnownValues.TryGetValue(key, out double lastY))
        {
            // 마지막 값 유지
            OnDataUpdated?.Invoke(frame.Time, frame.Time, lastY);
        }
        else
        {
            // 첫 데이터도 없음 → 시뮬레이션 종료로 판단
            Console.WriteLine($"[Controller] Simulation ended at {frame.Time:F2}s");
        }
    }
}
```

## 📊 동작 시나리오

### 시나리오 1: 데이터 있음

```
시간 0.0초:
1. GlobalDataService: DB 조회 성공 → chunk = {0.0: Frame(data)}
2. Repository.StoreChunk(chunk) → 저장
3. OnFramesAdded 이벤트 발생
4. Controller: frame.IsEmpty = false → ProcessFrame() 호출
5. 차트 업데이트
```

### 시나리오 2: 데이터 없음 (retry 실패)

```
시간 0.1초:
1. GlobalDataService: DB 조회 실패 (retry 5회) → chunk = null
2. chunk = new Dictionary<double, SimulationFrame>() → 빈 chunk 생성
3. Repository.StoreChunk(chunk) → 저장 (빈 chunk)
4. OnFramesAdded 이벤트 발생 (빈 frames 리스트)
5. Controller: frames.Count = 0 → 아무 처리 안함 (또는 빈 Frame 처리)
```

### 시나리오 3: 일부 데이터만 있음

```
조회 범위: 0.0 ~ 1.0초
DB 데이터: 0.0, 0.3, 0.7초만 존재

1. GlobalDataService: DB 조회 성공 → chunk = {0.0: Frame, 0.3: Frame, 0.7: Frame}
2. Repository.StoreChunk(chunk) → 저장
3. OnFramesAdded 이벤트 발생
4. Controller: 
   - frame(0.0): IsEmpty = false → ProcessFrame()
   - frame(0.3): IsEmpty = false → ProcessFrame()
   - frame(0.7): IsEmpty = false → ProcessFrame()
5. 차트: 0.0, 0.3, 0.7초 데이터 표시 → 차트 라이브러리가 선형 보간
```

## ✅ 장점

### 1. 단일 책임 원칙 (Single Responsibility Principle)
- GlobalDataService: DB 조회만 담당
- SimulationController: 비즈니스 로직만 담당

### 2. 유연성
- Controller가 데이터 사용 여부를 자유롭게 결정
- 빈 데이터 처리 방식을 Controller에서 변경 가능

### 3. 테스트 용이성
- GlobalDataService: DB 조회 로직만 테스트
- SimulationController: 비즈니스 로직만 테스트

### 4. 확장성
- 새로운 Controller 추가 시 각자 다른 방식으로 빈 데이터 처리 가능
- GlobalDataService는 변경 불필요

## 🔄 backup의 DatabaseQueryService와 비교

### backup (DatabaseQueryService)
```csharp
// 이벤트 종류로 성공/실패 구분
OnDataQueried += (serviceId, data) => { /* 데이터 있음 */ };
OnSimulationEnded += (failedTime, retryCount) => { /* 데이터 없음 */ };
```

### 현재 (GlobalDataService)
```csharp
// Frame 데이터 유무로 성공/실패 구분
OnFramesAdded += (frames, sessionId) => 
{
    foreach (var frame in frames)
    {
        if (frame.IsEmpty)
        {
            // 데이터 없음 (backup의 OnSimulationEnded와 동일)
        }
        else
        {
            // 데이터 있음 (backup의 OnDataQueried와 동일)
        }
    }
};
```

**완전히 동일한 로직, 다른 표현 방식!** ✅

## 📝 결론

**GlobalDataService는 "데이터 제공자"로서 데이터 유무를 판단하지 않고, 무조건 Repository에 저장하고 이벤트를 발생시킵니다.**

**SimulationController는 "데이터 소비자"로서 받은 데이터의 null 여부를 판단하고, 비즈니스 로직에 따라 사용 여부를 결정합니다.**

이는 **관심사의 분리(Separation of Concerns)** 원칙을 따르는 깔끔한 설계입니다! 🎯
