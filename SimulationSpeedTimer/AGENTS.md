# AGENTS.md

## 프로젝트 개요

SimulationSpeedTimer는 C# .NET Framework 4.7.2 기반의 시뮬레이션 타이밍 및 데이터 수집 시스템입니다. 
WPF 프레젠테이션 계층과 SQLite 데이터베이스를 사용하며, 독립적 폴링 아키텍처를 구현합니다.

## 빌드/테스트 명령어

### 빌드
```bash
# Debug 빌드
msbuild SimulationSpeedTimer.csproj /p:Configuration=Debug

# Release 빌드
msbuild SimulationSpeedTimer.csproj /p:Configuration=Release

# NuGet 패키지 복원
nuget restore SimulationSpeedTimer.csproj
```

### 테스트 실행
```bash
# 전체 테스트 실행 (Program.cs 통해)
bin\Debug\SimulationSpeedTimer.exe

# 단일 테스트 클래스 실행
# Program.cs의 MainTest 클래스에서 주석/해제하여 선택
```

### 코드 검증
```bash
# 아키텍처 검증 테스트
Tests.ArchitectureValidator.Run()

# 독립적 폴링 검증 테스트
Tests.IndependentPollingVerification.Run()

# 안정성 체크
Tests.StabilityCheck.Run()
```

## 코드 스타일 가이드라인

### 임포트 규칙
```csharp
// System 네임스페이스 먼저
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// 외부 라이브러리
using NUnit.Framework;

// 프로젝트 내부 네임스페이스
using SimulationSpeedTimer.Models;
using SimulationSpeedTimer.Services;
```

### 포맷팅
- **들여쓰기**: 4스페이스 (탭 사용 금지)
- **중괄호**: 새 줄에 시작 (Allman 스타일)
- **라인 길이**: 120자 이하 권장
- **공백**: 메서드 사이에 1줄, 논리적 블록 사이에 1줄

```csharp
public class ExampleClass
{
    public void ExampleMethod()
    {
        if (condition)
        {
            // 코드
        }
    }
}
```

### 타입 및 변수 명명
- **클래스**: PascalCase (예: `SimulationTimer`)
- **메서드**: PascalCase (예: `RunIndependentPolling`)
- **변수/필드**: camelCase (예: `_sessionId`, `currentFrame`)
- **상수**: UPPER_SNAKE_CASE (예: `TickIntervalMs`)
- **프라이빗 필드**: 밑줄 접두사 (예: `_stopwatch`, `_cts`)

### 주석 규칙
```csharp
/// <summary>
/// 클래스/메서드에 대한 XML 문서 주석
/// </summary>
/// <param name="parameter">매개변수 설명</param>
/// <returns>반환값 설명</returns>
public class ExampleClass
{
    // 복잡한 로직에 대한 한 줄 주석
    private int _exampleField; // 필드 설명
}
```

### 예외 처리
```csharp
public void ExampleMethod()
{
    try
    {
        // 예외가 발생할 수 있는 코드
        RiskyOperation();
    }
    catch (SpecificException ex)
    {
        // 구체적인 예외 처리
        LogError(ex);
        throw new CustomException("메시지", ex);
    }
    catch (Exception ex)
    {
        // 일반 예외 처리
        LogError(ex);
        throw;
    }
    finally
    {
        // 정리 코드
        Cleanup();
    }
}
```

## 아키텍처 원칙

### 독립적 폴링 아키텍처
- 각 데이터 소스는 독립적인 폴링 스레드를 가짐
- 데이터 병합은 중앙 서비스에서 처리
- 세션 기반 격리 보장

### 레이어 분리
- **Models**: 데이터 모델 (`Models/Spatial/`)
- **Services**: 비즈니스 로직 (`SimulationHistoryService`, `GlobalDataService`)
- **ViewModels**: WPF 뷰모델 (`*ViewModel.cs`)
- **Tests**: 단위/통합 테스트 (`Tests/`)

### 데이터베이스
- SQLite 사용
- Entity Framework 6.5.1
- `SimulationSchema.cs`에서 스키마 관리

## 테스트 가이드라인

### 테스트 구조
```csharp
[TestFixture]
public class ExampleTest
{
    [SetUp]
    public void Setup()
    {
        // 테스트 초기화
    }

    [Test]
    public void Test_Method_ExpectedBehavior()
    {
        // Arrange
        var expected = "result";
        
        // Act
        var actual = SystemUnderTest.Method();
        
        // Assert
        Assert.AreEqual(expected, actual);
    }

    [TearDown]
    public void TearDown()
    {
        // 정리
    }
}
```

### 테스트 명명 규칙
- `Test_[MethodName]_[ExpectedBehavior]`
- `Test_[Feature]_[Scenario]_[ExpectedResult]`

## 의존성 관리

### NuGet 패키지
- EntityFramework 6.5.1
- System.Data.SQLite 관련 패키지들
- NUnit 3.13.3 (테스팅)

### 패키지 복원
```bash
nuget restore
# 또는
.\restore-packages.ps1
```

## WPF 관련

### XAML 파일
- `ProgressView.xaml`: 진행 상황 뷰
- 코드비하인드는 `.xaml.cs` 확장자

### ViewModel 패턴
- `INotifyPropertyChanged` 구현
- 커맨드 패턴 사용
- 데이터 바인딩 기반 UI 업데이트

## 로깅 및 디버깅

### 콘솔 출력
- 테스트 결과는 `Console.WriteLine`으로 출력
- 세션 ID, 타이밍 정보 포함

### 데이터베이스 검사
- `DbInspector.cs`로 데이터베이스 상태 확인
- 테스트용 DB 파일: `test_*.db`

## 성능 고려사항

### 타이밍 정확도
- 10ms 간격 타이머 사용
- 배속 조절 기능 (`_speedMultiplier`)
- `Stopwatch` 기반 고정밀 타이밍

### 메모리 관리
- `CancellationTokenSource`로 스레드 관리
- `IDisposable` 구현 권장
- 대용량 데이터 처리 시 스트리밍 고려

## 보안 주의사항

- 데이터베이스 연결 문자열 노출 금지
- 예외 메시지에 민감 정보 포함 금지
- 입력 데이터 검증 필수