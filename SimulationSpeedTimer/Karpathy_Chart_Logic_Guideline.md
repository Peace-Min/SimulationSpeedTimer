# Karpathy-Style 가이드라인: 차트 생성 비즈니스 로직

> "매직(Magic)을 피하라. 코드는 마법이 아니라 기계적인 절차여야 한다."

사용자께서 질문하신 **"2D/3D 모드에 따라 차트 시리즈를 할당하는 로직"**을 구현할 때, 안드레 카파시(Andrej Karpathy) 관점의 핵심 가치는 **명시성(Explicitness)**과 **단순성(Simplicity)**입니다.

## 1. 피해야 할 패턴: "과도한 추상화"

우리는 흔히 "유연성"을 핑계로 복잡한 팩토리 패턴을 사용하려 합니다.

### 🚫 Bad: Factory Pattern (숨겨진 로직)
```csharp
// "ChartSeriesFactory가 알아서 해주겠지" -> 블랙박스
var series = ChartSeriesFactory.Create(config); 
chart.Series.Add(series);
```
**문제점**:
1.  `Create` 메서드 안을 들어가 봐야 2D인지 3D인지 알 수 있습니다.
2.  2D와 3D는 초기화 방식이 완전히 다를 수 있는데(예: 3D는 조명 설정 필요), 팩토리에 억지로 구겨 넣으면 인터페이스가 지저분해집니다.

---

## 2. 권장 패턴: "Flat & Explicit Setup"

카파시 스타일은 **"그냥 if문 써라(Just use an if statement)"**입니다.
초기화 로직을 숨기지 말고, **가장 상위 레벨(Top-Level)에서 분기**를 명확히 보여주는 것이 좋습니다.

### ✅ Good: Explicit Logic (명시적 분기)

```csharp
public void SetupChart(DatabaseQueryConfig config)
{
    // 1. 공통 설정 (Common)
    var chart = new ChartView();
    chart.Title = "Simulation Result";

    // 2. 분기 처리 (Explicit Branching)
    // 읽는 사람이 여기서 "아, 3D와 2D가 갈라지는구나"를 바로 알 수 있음
    if (config.Is3DMode)
    {
        // [3D Path]
        // 3D 전용 초기화 로직이 인라인으로 보여야 함
        var series3D = new LineSeries3D
        {
            XBinding = "X",
            YBinding = "Y",
            ZBinding = "Z", // 3D 전용 속성
            CameraConfig = new CameraConfig { ... } // 2D엔 없는 설정
        };
        chart.Series.Add(series3D);
        
        // 3D 전용 축 설정 등...
        chart.AxisX = new Axis3D();
    }
    else
    {
        // [2D Path]
        var series2D = new LineSeries2D
        {
            XBinding = "X",
            YBinding = "Y",
            // ZBinding 없음
            StrokeThickness = 2 // 2D 전용 스타일
        };
        chart.Series.Add(series2D);

        // 2D 전용 축 설정
        chart.AxisX = new DateTimeAxis();
    }
}
```

## 3. 핵심 원칙 요약

1.  **Dumb is Better**: 코드가 똑똑해 보이려(Smart) 하지 말고, 멍청할 정도로 단순하게(Dumb) 작성하십시오. `if (3D)`면 3D를 만들고, `else`면 2D를 만듭니다.
2.  **No Interface Forcing**: 2D와 3D가 90% 다르다면, 억지로 `IChartSeries` 같은 공통 인터페이스로 묶어서 `series.SetZ(null)` 같은 이상한 짓을 하지 마십시오. 그냥 따로 처리하십시오.
3.  **Inline properties access**: 설정 객체 (`config`)의 속성을 `if` 조건문에서 직접 접근하십시오. 로직의 흐름이 데이터(`config`)의 상태에 따라 어떻게 변하는지 적나라하게 드러내야 합니다.

## 4. Q&A: 변수의 스코프 (미리 선언 vs 분기 내 선언)

**Q: "공통으로 사용되는 X, Y 정보들은 분기 전에 미리 만들어두는 게 좋나요?"**

### ❌ Bad: Premature Sharing (설익은 공유)
```csharp
// "어차피 둘 다 X, Y 쓰니까 미리 뽑아놓자?" -> 비추천
var xBind = config.X.Name;
var yBind = config.Y.Name;

if (is3D) {
    // 3D 전용 로직... (여기서 xBind가 쓰이는지 확인하려면 위를 봐야 함)
} else {
    // 2D 전용 로직...
}
```
**이유**:
1.  **결합도 증가**: 만약 나중에 2D 차트만 "X축 형식이 바뀐다면", 위의 공통 변수를 수정하다가 3D까지 깨뜨릴 수 있습니다.
2.  **문맥 오염**: 3D 블록을 떼어서 다른 함수로 옮기고 싶을 때(Refactoring), 외부 변수(`xBind`) 의존성 때문에 `Cut & Paste`가 안 됩니다.

### ✅ Good: Code Locality (코드 지역성)
```csharp
if (is3D) {
    // [3D Context] 필요한 모든 것을 여기서 정의
    var xBind = config.X.Name; 
    var yBind = config.Y.Name;
    var zBind = config.Z.Name;
    
    Setup3D(xBind, yBind, zBind);
} else {
    // [2D Context] 필요한 모든 것을 여기서 정의
    var xBind = config.X.Name;
    var yBind = config.Y.Name;
    
    Setup2D(xBind, yBind);
}
```
**이유**:
- **복사하기 쉬움 (Copy-Paste Friendly)**: 이 블록이 독립적(Self-Contained)이므로, 나중에 `Init3DChart()` 함수로 추출하기가 매우 쉽습니다.
- **Side-Effect 없음**: 2D 로직을 수정해도 3D에는 절대 영향을 주지 않는다는 확신(Confidence)을 줍니다.

**Rule of Thumb (경험 법칙)**:
- 단순히 문자열 가져오기나 가벼운 할당이라면, **중복(Repeat)**하십시오. **명시성과 독립성이 DRY보다 중요합니다.**
- (단, 무거운 DB 연결이나 메모리 할당처럼 비용이 큰 작업은 미리 해서 재사용하십시오.)

## 5. Q&A: 가독성을 위한 단순 메서드 분리 (Single-Use Helper)

**Q: "if/else 안의 코드가 길어서 `Setup3DOnly()` 처럼 메서드로 빼는 건 어떤가요? 재사용은 안 하지만 가독성 때문에요."**

### ❌ Bad: Context Switching (문맥 끊기)
```csharp
if (is3D) {
    Setup3DOnly(config); // <--- 이걸 보려고 F12를 눌러서 파일 아래로 점프해야 함
}
```
**안드레 카파시의 관점**: **"싫어합니다."**
- **Linearity (선형성) 파괴**: 코드는 위에서 아래로 책처럼 읽혀야 합니다. 1회성 함수는 독자의 시선을 끊고(Jump) "이 함수가 상태를 조작하지 않을까?" 하는 의심을 품게 만듭니다.
- **Premature Fragmentation**: 단순히 코드를 접어두기(Fold) 위해 함수를 만드는 것은 좋지 않습니다.

### ✅ Good: Comments & Local Functions (주석 또는 로컬 함수)

**대안 1: 그냥 주석으로 구획 나누기 (가장 선호)**
오히려 `// --- 3D Setup Start ---` 주석을 달고 인라인으로 쭉 쓰는 것을 선호합니다. 요즘 에디터(IDE)는 스크롤이 빠르기 때문입니다.

**대안 2: C# 로컬 함수 (Local Function)**
정 분리하고 싶다면, **물리적으로 바로 아래**에 두어 시야에서 사라지지 않게 하십시오.
```csharp
if (is3D) {
    Setup3D(); 
    
    void Setup3D() { // 바로 밑에 선언되어 있어 문맥 이동 없음
       // ...
    }
}
```

## 결론
**"Scroll is cheaper than Jump." (스크롤이 점프보다 저렴하다)**
코드가 50줄~100줄 정도 되어도, 논리적 흐름이 끊기지 않고 쭉 이어지는 것이 이리저리 점프하는 10줄짜리 함수 5개보다 훨씬 낫습니다.

