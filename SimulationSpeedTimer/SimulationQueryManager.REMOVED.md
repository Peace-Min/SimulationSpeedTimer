# SimulationQueryManager 제거됨

이 파일은 더 이상 사용되지 않습니다.

**제거 이유:**
- 불필요한 중간 레이어 (SimulationController가 직접 서비스 관리)
- 단순히 foreach로 전달만 하는 역할
- 코드 복잡도만 증가시킴

**대체:**
- `SimulationController`가 `List<DatabaseQueryService>`를 직접 관리
