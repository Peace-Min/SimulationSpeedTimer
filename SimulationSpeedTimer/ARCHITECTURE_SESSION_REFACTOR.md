# GlobalDataService 차세대 아키텍처 설계 가이드

## 1. 핵심 철학 (The Core Vision)
- **1 Run = 1 Session Instance**: 시뮬레이션이 실행될 때마다 `GlobalDataService`의 핵심 로직을 담당하는 독립된 인스턴스(Session)를 생성한다.
- **완벽한 상태 격리**: 싱글턴 클래스는 오직 "현재 세션이 무엇인가"라는 포인터만 들고 있는 **Factory/Router** 역할만 수행하며, 어떤 내부 데이터 상태(Pending Queue, LastSeenTime 등)도 전역적으로 유지하지 않는다.
- **SessionId Anchor**: 모든 데이터 흐름의 정합성은 `Guid SessionId` 하나로 통제한다. ID가 다르면 대화하지 않는다.

## 2. 전제 배경 (Background Context)
- **독립적 파일 시스템**: 시뮬레이션 실행 시마다 **새로운 SQLite DB 파일**이 생성된다.
- **Zero Resource Contention**: 이전 실행의 DB 파일과 현재 실행의 DB 파일이 다르므로, 이전 세션의 WAL 체크포인트나 파일 락 종료를 기다릴(Wait) 필요가 전혀 없다.

## 3. 핵심 설계 원칙 (Design Principles)

### A. Zero-Wait Lifecycle (대기 없는 생명주기)
- UI 컨텍스트에서 `Stop()` -> `Start()` 호출 순서가 보장되므로, `Start()` 내부에서 이전 세션의 생존 여부를 검사하거나 정지를 기다리는 방어 코드를 두지 않는다.
- 각 세션은 독립된 인스턴스이므로, 이전 세션의 `Stop` 처리가 지연되더라도 새 세션의 `Start`는 즉시 실행된다.

### B. 데이터 흐름의 완전 폐쇄성
- **No Global Buffer**: 서비스가 시작되지 않은 상태나 종료된 상태에서 들어오는 데이터는 전역 큐에 쌓지 않고 즉시 버린다.
- **Session ID Verification**: `SharedFrameRepository`는 데이터를 저장할 때 호출자가 들고 있는 `SessionId`를 확인하여, 종료 중인 이전 세션의 데이터가 새 세션 영역을 오염시키지 않도록 원천 차단한다.

## 4. 권장 구현 구조 (Implementation Skeleton)

```csharp
public class GlobalDataService 
{
    private DataSession _currentSession; // 현재 활성 세션 인스턴스

    public void Start(string dbPath) 
    {
        // UI가 Stop을 먼저 불렀음을 신뢰함.
        // 그냥 새 세션을 만들고 즉시 시작함.
        _currentSession = new DataSession(Guid.NewGuid(), dbPath);
        _currentSession.Run();
    }

    public void Close()
    {
        // 현재 세션에게 종료 신호를 보냄
        _currentSession?.Stop();
        _currentSession = null;
    }

    public void EnqueueTime(double time) 
    {
        // 현재 세션에게 전달 (세션이 없으면 자동 소멸)
        _currentSession?.Enqueue(time); 
    }
}

// 모든 복잡한 로직(Buffer, SQLite, Schema)은 이 안에 갇힘
private class DataSession 
{
    public Guid Id { get; }
    public void Stop() { /* 자신만의 자원 정리 */ }
}
```

## 5. 세션 동기화 전략 (Synchronization Strategy)
1. **SimulationContext**: 실행 시점에 `Guid` 발급 및 `OnSessionStarted` 이벤트 발행.
2. **SharedFrameRepository**: `StartNewSession(id)` 수신 시 기존 프레임 전량 폐기 후 ID 잠금.
3. **DataSession (Service)**: 생성 시 ID를 부여받고, 모든 데이터 전송 시 이 ID를 서명(Signature)으로 사용.
4. **Validation**: 레포지토리는 서명이 다른 데이터는 즉시 버림으로써 비동기 Cleanup 중인 이전 세션의 간섭을 100% 차단.

## 6. 결론
이 설계는 **"이전 태스크와 다음 태스크는 완전히 남남이다"**라는 사실을 코드로 증명하는 것이다. 싱글턴 내부의 상태를 지우고 `null`을 체크하는 "더러운 관리"를 원천 차단하고, 객체 지향의 본질인 **생성(Creation)과 파괴(Destruction)**를 통해 안정성을 확보한다.
