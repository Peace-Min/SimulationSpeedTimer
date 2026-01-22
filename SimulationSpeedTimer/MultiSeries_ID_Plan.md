# 리팩토링 계획: ID 기반 다중 시리즈 지원

## 목표
`ChartAxisDataProvider`가 `Dictionary<string, List<DatabaseQueryConfig>>` 구조를 사용하여 다중 차트(수신자) 및 차트당 다중 시리즈를 지원하도록 개선합니다.

## 변경 제안

### 1. `DatabaseQueryConfig.cs`
*   **변경 없음**: 시리즈 식별을 위한 별도 속성을 추가하지 않고, **등록 순서(Index)**를 사용합니다.

### 2. `ChartAxisDataProvider.cs`

*   **데이터 구조 변경**: `Dictionary<string, List<DatabaseQueryConfig>>` (Key: `receiverId`)
*   **이벤트 시그니처 변경**:
    *   `Action<string, int, double, double, double, double?>`
    *   파라미터 순서: `(receiverId, seriesIndex, time, x, y, z)`
    *   수신부는 `seriesIndex`를 사용하여 자신의 시리즈 리스트(`config[i]`)와 매핑합니다.
*   **로직 업데이트 (`ProcessFrame`)**:
    *   등록된 모든 `receiverId`(Key)를 순회합니다.
    *   각 리스트의 **인덱스(i)**를 돌며 `OnDataUpdated(receiverId, i, ...)`를 호출합니다.

#### [MODIFY] [ChartAxisDataProvider.cs](file:///c:/Users/minph/OneDrive/%EB%B0%94%ED%83%95%20%ED%99%94%EB%A9%B4/%EC%83%88%20%ED%8F%B4%EB%8D%94/as/SimulationSpeedTimer/ChartAxisDataProvider.cs)

## 검증 계획

### 자동화 검증
1.  **컴파일 확인**: 변경된 이벤트 시그니처(`receiverId` 추가)로 인한 컴파일 에러 유무 확인.
2.  **로직 검증**:
    - 동일한 `receiverId`로 2개의 Config를 추가했을 때, 콜백이 2번 발생하는지 확인.
    - 서로 다른 `receiverId`를 가진 Config가 서로 간섭하지 않는지 확인.

### 수동 검증
- Dictionary 키 관리(중복 키 추가, 없는 키 삭제 등)가 안전하게 처리되는지 코드 리뷰.
