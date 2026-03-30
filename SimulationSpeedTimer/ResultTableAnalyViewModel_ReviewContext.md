# ResultTableAnalyViewModel Review Context

## 목적
- 이 문서는 `ResultTableAnalyViewModel.cs` 단일 파일만 보고도 로직 검토가 가능하도록, 필요한 의존 개념과 현재 구현 흐름을 요약한 문서다.
- 대상 파일: [ResultTableAnalyViewModel.cs](C:/Users/CEO/source/repos/SimulationSpeedTimer/SimulationSpeedTimer/ResultTableAnalyViewModel.cs)

## 역할
- 실시간으로 수신되는 `SimulationFrame` 데이터를 테이블별 Grid 전시 데이터로 변환한다.
- UI는 한 번에 하나의 `SelectedTableName`만 본다.
- 선택된 테이블만 `_pendingBuffer`에서 dequeue하여 화면 컬렉션(`Items`)에 반영한다.
- 일부 객체는 서브컴포넌트를 가지며, 서브컴포넌트 데이터는 부모 테이블 row에 병합 전시한다.

## 외부 의존 개념

### SimulationFrame
- 파일: [SimulationFrame.cs](C:/Users/CEO/source/repos/SimulationSpeedTimer/SimulationSpeedTimer/SimulationFrame.cs)
- `Time`: 해당 frame의 시간 키
- `AllTables`: 이 시간대에 수신된 모든 `SimulationTable`
- `GetTable(string tableName)`: 대소문자 무시 조회

### SimulationTable
- 파일: [SimulationFrame.cs](C:/Users/CEO/source/repos/SimulationSpeedTimer/SimulationSpeedTimer/SimulationFrame.cs)
- `TableName`: 수신 테이블명
- `ColumnNames`: 수신된 컬럼명 목록
- `this[string columnName]`: 컬럼값 조회, 없으면 `null`

### SharedFrameRepository
- 파일: [SharedFrameRepository.cs](C:/Users/CEO/source/repos/SimulationSpeedTimer/SimulationSpeedTimer/SharedFrameRepository.cs)
- `OnFramesAdded(List<SimulationFrame>, Guid sessionId)` 이벤트를 발행한다.
- 이벤트 payload는 저장소 전체 snapshot이 아니라 batch-local delta frame 목록이다.

### 시나리오 설정 데이터
- `ResultTableAnalyViewModel_OnScenarioSetupCompleted`에서 화면 전시용 `TableConfig`와 `SubcomponentLink`를 만든다.
- 부모 테이블 config는 `node.Key.playerObjectName` 기준으로 생성된다.
- 서브컴포넌트 링크는 `subComponent.Key.Path -> subComponent.Key.ParentNode.Name`으로 생성된다.

## ViewModel 내부 캐시 의미
- `_pendingBuffer`
  - key: 화면 테이블명
  - value: 아직 UI에 반영되지 않은 `ExpandoObject` row 큐
- `_bandCache`
  - key: 화면 테이블명
  - value: Grid band/column 정의
- `_rowCache`
  - key: 화면 테이블명
  - value: Grid `ItemsSource`
- `_subcomponentParentMap`
  - key: 서브컴포넌트 테이블명
  - value: 부모 테이블명
- `_configuredFieldCache`
  - key: 화면 테이블명
  - value: 해당 테이블에 표시 가능한 컬럼명 집합
- `_rowIndexCache`
  - key: 부모 테이블명
  - value: `Time -> ExpandoObject`
  - 병합 대상 부모 테이블만 생성된다.

## 현재 구현 흐름

### 1. UpdateTableConfig
- 모든 `tableConfigs`에 대해 `_bandCache`, `_rowCache`, `_configuredFieldCache`를 만든다.
- `subcomponentLinks`에 포함된 부모 테이블에 대해서만 `_rowIndexCache[parent]`를 만든다.
- 즉 일반 객체는 `_rowIndexCache`가 없고, 병합 대상 부모만 `_rowIndexCache`가 있다.

### 2. HandleFramesAdded
- session id가 현재 session과 다르면 무시한다.
- `frame.AllTables`를 순회한다.
- `tableData.TableName`이 `_subcomponentParentMap`에 있으면 부모 테이블명으로 바꾼다.
- 아니면 원래 `tableData.TableName`을 그대로 사용한다.
- 최종 `tableName` 기준으로 `CreateExpandoRow(frame, tableData)`를 만들고 `_pendingBuffer[tableName]`에 enqueue한다.

### 3. CreateExpandoRow
- 항상 `Time` 컬럼을 채운다.
- 현재 `tableName`이 `_rowIndexCache`에 있으면 병합 대상 부모로 간주한다.
- 병합 대상 부모면 `_configuredFieldCache[tableName]`의 모든 컬럼을 먼저 `"-"`로 채운다.
- 이후 현재 `tableData.ColumnNames`를 순회하며 실제 값을 덮어쓴다.
- 일반 객체는 `_rowIndexCache`가 없으므로 baseline처럼 실제 수신 컬럼만 row에 들어간다.

### 4. OnUIRefreshTimerTick
- `SelectedTableName`을 tick 시작 시점에 로컬 변수로 캡처한다.
- 해당 테이블 큐만 dequeue한다.
- 선택된 테이블이 `_rowIndexCache`에 있으면 병합 대상 부모로 보고 `Time` 기준 merge를 수행한다.
- `_rowIndexCache`가 없으면 baseline처럼 `AddRange`한다.

## 검토 시 반드시 확인할 논리 포인트

### 1. 가장 큰 허점: placeholder가 기존 실제 값을 다시 지울 수 있음
- 병합 대상 row는 생성 시 모든 configured field를 `"-"`로 채운다.
- 이후 merge 단계는 `Time`을 제외한 모든 키를 기존 row에 그대로 덮어쓴다.
- 그래서 나중에 들어온 row가 실제 값이 없는 컬럼을 `"-"`로 들고 있으면, 이전에 채워진 실제 값이 `"-"`로 덮여 사라질 수 있다.

예시:
- `HG` at `t=1.0`: `Hit=10`, `MissDistance=5`, `Value0="-"`, `Value1="-"`
- `HG/Sub` at `t=1.0`: `Hit="-"`, `MissDistance="-"`, `Value0=1`, `Value1=2`
- merge 후 현재 구현은 `Hit`와 `MissDistance`도 `"-"`로 덮을 수 있다.

즉 현재 코드는 "없는 값 표시용 placeholder"와 "실제 업데이트 payload"를 분리하지 않았기 때문에, 병합 대상 부모에서 데이터 소실이 생길 수 있다.

### 2. 일반 객체는 baseline 경로를 타는지 확인 필요
- 일반 객체는 `_rowIndexCache`가 없어야 한다.
- 일반 객체가 `_rowIndexCache`에 잘못 들어가면 병합 대상처럼 처리되어 의도치 않게 merge/placeholder 경로를 타게 된다.

### 3. 부모/서브컴포넌트 naming space 일치 여부
- 부모 config key는 `playerObjectName`
- 링크의 parent는 `ParentNode.Name`
- 수신 테이블명은 `SimulationTable.TableName`
- 이 세 값이 다르면 병합이 아니라 queue key 자체가 달라져 전시 누락이 발생할 수 있다.

### 4. 선택하지 않은 테이블의 누적 표시
- 설계상 선택되지 않은 테이블은 dequeue하지 않는다.
- 따라서 선택을 바꾼 뒤 나중에 돌아왔을 때, `_pendingBuffer[selectedTableName]`의 누적분이 그대로 남아 있어야 정상이다.
- 이 케이스에서 누락이 생기면 보통 아래 둘 중 하나다.
  - enqueue key가 실제 선택 이름과 다름
  - dequeue는 되었지만 잘못된 row merge로 값이 소실됨

## 현재 코드만 보고 가능한 핵심 질문
- 병합 대상 row에서 placeholder `"-"`를 merge payload로 그대로 써도 되는가?
- 일반 객체가 절대 `_rowIndexCache`에 들어가지 않는가?
- `playerObjectName`, `ParentNode.Name`, `SimulationTable.TableName`이 항상 같은 naming rule을 쓰는가?
- 특정 테이블 미전시 현상이 queue 누락인지, merge overwrite인지 로그 없이 구분 가능한가?

## 현재 구현의 가장 가능성 높은 결함 요약
- 일반 객체 미전시보다 병합 대상 부모에서의 값 소실 가능성이 더 큼
- 원인: placeholder를 row 초기값이자 merge payload로 동시에 사용함
- 즉 "미수신 값"과 "갱신하지 말아야 할 값"이 구분되지 않는다
