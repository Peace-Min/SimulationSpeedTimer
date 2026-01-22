# ChartAxisDataProvider 3D 로직 확장 계획

> "데이터 구조가 문제 도메인을 명확히 반영할 때 코드는 자연스럽게 흐른다."

## 1. 핵심 철학 (Why & How)

현재 `ChartAxisDataProvider`는 **2D 투영 모델**로 작동합니다:
- **입력**: `SimulationFrame` (시간 $t$의 상태 $S_t$)
- **매핑**: $f(S_t) \rightarrow (X_{공유}, \{Y_1, Y_2, ..., Y_n\})$
- **암묵적 가정**: 모든 시리즈가 하나의 도메인 축($X$, 보통 시간이나 거리)을 공유합니다.

**3D (X, Y, Z)**를 지원하려면 **3D 궤적 모델(Trajectory Model)**로 전환해야 합니다:
- **입력**: `SimulationFrame` (시간 $t$의 상태 $S_t$)
- **매핑**: $f(S_t) \rightarrow \{(x_1, y_1, z_1), (x_2, y_2, z_2), ..., (x_n, y_n, z_n)\}$
- **새로운 가정**: 각 엔티티(Series)는 시뮬레이션 공간 내에서 독립적인 위치 벡터 $\vec{v} \in \mathbb{R}^3$를 가집니다.

이 계획은 과한 엔지니어링(복잡한 팩토리 패턴이나 과도한 추상화)을 배제하고, 기존 `SeriesItem` 구조를 활용한 단순한 **합성(Composition)**을 통해 고성능 확장을 목표로 합니다.

---

## 2. 데이터 구조 변경

기존 `SeriesItem`을 "데이터 조회 최소 단위"(단일 스칼라 값 조회)로 유지하면서, 3D 시리즈를 위한 **복합 구조**를 도입합니다.

### 수정된 `DatabaseQueryConfig`

3D 시리즈 정의를 단순하게 추가합니다. "3D 시리즈"는 단순히 3개의 스칼라 소스(X, Y, Z)의 튜플입니다.

```csharp
public class SeriesDefinition3D
{
    public string SeriesName { get; set; }
    
    // 스칼라 fetcher들의 합성
    public SeriesItem XSource { get; set; }
    public SeriesItem YSource { get; set; }
    public SeriesItem ZSource { get; set; } // 새로운 차원
}

public class DatabaseQueryConfig
{
    // ... 기존 2D 설정 ...
    public SeriesItem XAxisSeries { get; set; }
    public List<SeriesItem> YAxisSeries { get; set; }

    // [New] 3D 설정
    // 이 리스트가 채워져 있으면 Provider는 해당 항목들에 대해 3D 모드로 작동합니다.
    public List<SeriesDefinition3D> Series3D { get; set; } = new List<SeriesDefinition3D>();
}
```

---

## 3. 내부 로직 루프 (`ProcessFrame`)

파이프라인이 **프레임 중심(Frame-Centric)**에서 **벡터 중심(Vector-Centric)**으로 변화합니다.

### 현재 2D 로직 (O(N) - N 스칼라)
1. $X_{공유}$ 조회.
2. $X_{공유}$ 누락 시 -> 시간(t)으로 대체하거나 Skip.
3. $X_{공유}$ 유효 시 -> `YAxisSeries`의 $Y_i$ 반복.
4. 방출: `(Time, X_공유, {Y_vals})`.

### 제안된 3D 로직 (O(3N) - N 벡터)

설정 존재 여부에 따라 로직이 분기됩니다. 3D 설정이 있다면:

1. **반복**: `SeriesDefinition3D` 항목들($S_i$).
2. **일괄 조회 (Batch Fetch)**:
   - $x = \text{GetValue}(S_i.XSource)$
   - $y = \text{GetValue}(S_i.YSource)$
   - $z = \text{GetValue}(S_i.ZSource)$
3. **검증 및 동기화**:
   - $(x, y, z)$ 중 **하나라도** `null`(데이터 누락)이면:
     - **Strict Mode**: 포인트 전체 스킵 (고스트 현상/아티팩트 방지).
     - **Loose Mode**: 이전 값 사용 (차트가 지저분해지므로 비추천).
     - *결정*: **Skip**. 독립 풀링(Independent Polling) 환경에서 불완전한 데이터는 노이즈입니다.
4. **집계**:
   - 유효한 벡터 저장: `Dictionary<string, (double x, double y, double z)>`.
5. **전송 (Dispatch)**:
   - `OnDataUpdated3D?.Invoke(double time, Dictionary<string, Vector3> data)`

### 업데이트된 코드 흐름 (의사 코드)

```csharp
private void ProcessFrame(SimulationFrame frame)
{
    // 1. 하위 호환성 유지 (기존 2D 로직)
    // ... (기존 X/Y 처리) ...

    // 2. [New] 3D 벡터 처리
    if (_config.Series3D != null && _config.Series3D.Any())
    {
        var buffer3D = new Dictionary<string, (double x, double y, double z)>(
            _config.Series3D.Count // 미리 용량 할당
        );

        foreach (var def in _config.Series3D)
        {
            // 벡터화된 스타일의 조회 (개념적으로)
            double? x = GetValue(frame, def.XSource.ObjectName, def.XSource.AttributeName);
            double? y = GetValue(frame, def.YSource.ObjectName, def.YSource.AttributeName);
            double? z = GetValue(frame, def.ZSource.ObjectName, def.ZSource.AttributeName);

            // "All or Nothing" 무결성 검사
            if (x.HasValue && y.HasValue && z.HasValue)
            {
                buffer3D[def.SeriesName] = (x.Value, y.Value, z.Value);
            }
        }

        // 빈 이벤트는 발생시키지 않거나(Zero-allocation), 필요 시 호출
        if (buffer3D.Count > 0)
        {
            OnDataUpdated3D?.Invoke(frame.Time, buffer3D);
        }
    }
}
```

---

## 4. API / 출력 프로토콜

기존의 혼란스러운 `Action<d, d, Dict>`를 오버로딩하는 대신, 명확한 새 델리게이트 시그니처를 정의합니다.

```csharp
// 높은 처리량, 단순한 시그니처
public Action<double, Dictionary<string, (double X, double Y, double Z)>> OnDataUpdated3D;
```

이를 통해 2D와 3D 소비자를 분리(Decoupling)합니다.
- 2D 차트는 `OnDataUpdated`를 구독.
- 3D 플롯은 `OnDataUpdated3D`를 구독.

## 5. 왜 이것이 "오버엔지니어링이 아닌가"

1.  **데이터 조회를 위한 새 클래스 없음**: `GetValue(frame, table, col)` 헬퍼를 그대로 재사용합니다.
2.  **복잡한 다형성 없음**: `IChartSeries` 같은 인터페이스를 만들지 않습니다. 단순히 리스트 내용만 확인합니다.
3.  **데이터 지향적 (Data-Oriented)**: 루프는 타이트하게 값 추출과 딕셔너리 패킹에만 집중합니다.
4.  **최소 상태 (Minimal State)**: Provider 내부에 버퍼나 히스토리를 유지하지 않습니니다. 들어오는 프레임을 전달하는 무상태 파이프(Stateless Pipe) 역할을 유지합니다.

---

## 요약
로직은 **"하나의 X, 다수의 Y"**에서 **"다수의 독립적 (X, Y, Z) 벡터"**구조로 변경됩니다. 핵심 추출 헬퍼인 `GetValue`는 그대로 "일꾼" 역할을 하며 코드 재사용성을 극대화합니다.
