# 코드 설계에 대한 고찰: 헬퍼 메서드 vs 명시적 흐름

> "코드는 추상화(Abstraction)보다 명시성(Explicitness)이 우선되어야 한다." - Andrej Karpathy (Software 2.0 철학의 연장선)

## 1. 현재의 접근 (Refactored with Helper)

우리는 중복을 줄이기 위해 `GetYOrZValue`라는 헬퍼 메서드를 도입했습니다.
```csharp
// 호출부
double? yVal = GetYOrZValue(frame, ...); // 내부에서 NaN 변환 로직 수행
```
이 방식은 **DRY (Don't Repeat Yourself)** 원칙을 따르지만, **제어 흐름(Control Flow)을 숨기는 단점**이 있습니다.
- "값이 없으면 `NaN`을 반환한다"는 중요한 정책이 함수 이름 뒤에 숨어 있습니다.
- `GetYOrZValue`라는 이름은 다소 모호합니다 (무엇을 가져온다는 것인가?).

## 2. 카파시(Karpathy) 스타일의 비판

Andrej Karpathy와 같은 실용주의 엔지니어들은 종종 **"Premature Abstraction(성급한 추상화)"**를 경계합니다.
- **"Just write the code"**: 로직이 5줄 정도라면, 굳이 함수로 빼서 문맥 이동(Context Switch)을 유발하기보다 그냥 나열하는 것이 가독성에 좋습니다.
- **Data Transformation View**: 코드는 데이터를 어떻게 변환하는지 한눈에 보여야 합니다. "데이터 조회"와 "정책(Policy) 적용"이 섞여 있는 함수는 좋지 않습니다.

## 3. 제안: "정책 우선(Policy-First)" 인라인 로직

헬퍼 메서드를 제거하고, **데이터 조회(Fetching)**와 **유효성 검사(Validation Logic)**를 분리하여 "이야기 흐르듯이" 작성하는 방식을 제안합니다.

### 제안 코드 구조
```csharp
foreach (var config in _configs)
{
    // 1. Fetching (Raw Data) - 있는 그대로 가져옴
    double? xVal = ...;
    double? yVal = GetValue(frame, config.YColumn...);
    double? zVal = config.Is3DMode ? GetValue(frame, config.ZColumn...) : null;

    // 2. Policy Application (Business Logic) - 여기서 결정함
    // "이 데이터가 유효한가?"에 대한 판단을 한 곳에서 명시적으로 수행
    if (config.IsXAxisTime)
    {
        // 정책 A: 시간축이면 관대하게 처리 (데이터 없으면 NaN으로라도 진행)
        if (xVal == null) xVal = frame.Time; 
        if (yVal == null) yVal = double.NaN;
        if (zVal == null && config.Is3DMode) zVal = double.NaN; // 3D인 경우만
    }
    else
    {
        // 정책 B: 데이터축이면 엄격하게 처리 (데이터 없으면 스킵)
        if (xVal == null || yVal == null) continue;
        if (config.Is3DMode && zVal == null) continue;
    }

    // 3. Dispatch
    OnDataUpdated?.Invoke(..., xVal.Value, yVal.Value, zVal);
}
```

## 4. 결론 (Verdict)

이 방식이 더 나은 이유:
1.  **가독성**: 위에서 아래로 읽을 때 흐름이 끊기지 않습니다. "데이터를 가져오고 -> 규칙을 적용하고 -> 보낸다"가 명확합니다.
2.  **유연성**: 만약 Y축은 `NaN`을 허용하고 Z축은 허용하지 않는 등의 복잡한 요구사항이 생겨도, `if` 블록 안에서 쉽게 수정 가능합니다 (헬퍼 메서드는 수정하기 까다로움).
3.  **정직함(Honesty)**: 코드가 "무엇을 하는지" 숨기지 않고 드러냅니다.

따라서 헬퍼 메서드 분리보다는 **명시적 인라인 처리**로 방향을 잡는 것이 "충분한 고민"의 올바른 결론이라고 판단됩니다.
