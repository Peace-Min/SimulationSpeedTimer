# DatabaseQueryConfig 리팩토링 가이드 (실제 DB 구조 기반)

## 📋 개요

기존의 `DatabaseQueryConfig`는 직접 테이블명과 컬럼명을 지정했지만, 새로운 구조에서는 **메타데이터 기반 조회**를 지원합니다.

### 변경 이유

1. **동적 매핑**: 조회하고자 하는 테이블의 첫 행이 채워져야 `Object_Info`, `Column_Info`에 데이터가 설정됨
2. **유연성**: X축과 Y축이 서로 다른 테이블에서 올 수 있음
3. **간결성**: 사용자는 `object_name`과 `attribute_name`만 지정하면 됨

---

## 🏗️ 새로운 구조

### 1. DatabaseQueryConfig (메타데이터)

사용자가 설정하는 메타데이터 정보만 포함:

```csharp
public class DatabaseQueryConfig
{
    public string DatabasePath { get; set; }
    
    // X축 메타데이터
    public string XAxisObjectName { get; set; }       // Object_Info.object_name
    public string XAxisAttributeName { get; set; }    // Column_Info.attribute_name
    
    // Y축 메타데이터
    public string YAxisObjectName { get; set; }       // Object_Info.object_name
    public string YAxisAttributeName { get; set; }    // Column_Info.attribute_name
    
    // 재시도 설정
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalMs { get; set; } = 10;
}
```

### 2. ResolvedQueryInfo (해석된 정보)

초기 조회 시점에 메타데이터를 기반으로 생성되는 실제 쿼리 정보:

```csharp
public class ResolvedQueryInfo
{
    // X축 실제 정보
    public string XAxisTableName { get; set; }        // 실제 테이블명 (예: Object_Table_0)
    public string XAxisColumnName { get; set; }       // 실제 컬럼명 (예: COL1)
    public string XAxisTimeColumnName { get; set; }   // 시간 컬럼명 (고정: s_time)
    
    // Y축 실제 정보
    public string YAxisTableName { get; set; }        // 실제 테이블명 (예: Object_Table_1)
    public string YAxisColumnName { get; set; }       // 실제 컬럼명 (예: COL5)
    public string YAxisTimeColumnName { get; set; }   // 시간 컬럼명 (고정: s_time)
    
    // 편의 속성
    public bool IsSameTable => XAxisTableName == YAxisTableName;
}
```

### 3. MetadataResolver (해석 서비스)

`Object_Info`, `Column_Info` 테이블에서 메타데이터를 조회하여 실제 정보로 변환:

```csharp
public static class MetadataResolver
{
    public static ResolvedQueryInfo Resolve(
        DatabaseQueryConfig config, 
        SQLiteConnection connection)
    {
        // Object_Info, Column_Info에서 실제 테이블명/컬럼명 조회
        // ...
    }
}
```

---

## 📊 실제 데이터베이스 스키마

### Object_Info 테이블

| 컬럼명 | 타입 | 설명 |
|--------|------|------|
| object_name | TEXT | 객체 이름 (사용자가 Config에 지정) |
| object_type | TEXT | 객체 타입 (예: "P") |
| p_object_name | TEXT | 부모 객체 이름 (nullable) |
| table_name | TEXT | 실제 테이블명 |

**실제 데이터:**
```json
{
    "object_name": "ourDetectRadar",
    "object_type": "P",
    "p_object_name": null,
    "table_name": "Object_Table_0"
}
```

### Column_Info 테이블

| 컬럼명 | 타입 | 설명 |
|--------|------|------|
| attribute_name | TEXT | 속성 이름 (사용자가 Config에 지정) |
| column_name | TEXT | 실제 컬럼명 (예: COL0, COL1, ...) |
| data_type | TEXT | 데이터 타입 (예: DOUBLE_TYPE, INT16_TYPE) |
| table_name | TEXT | 테이블명 |

**실제 데이터:**
```json
{
    "attribute_name": "distance",
    "column_name": "COL1",
    "data_type": "DOUBLE_TYPE",
    "table_name": "Object_Table_0"
}
```

### 실제 데이터 테이블 (Object_Table_0, Object_Table_1, Object_Table_2)

| 컬럼명 | 타입 | 설명 |
|--------|------|------|
| s_time | REAL | 시뮬레이션 시간 (고정 컬럼명) |
| COL0 | REAL | deltaT |
| COL1 | REAL | distance (ourDetectRadar) |
| COL2 ~ COL15 | REAL | 기타 속성들 |

**실제 데이터:**
```json
{
    "s_time": 0.01,
    "COL0": "0.01",
    "COL1": "4698.6578292799131",
    "COL2": "0",
    ...
}
```

---

## 💡 사용 예시

### 시나리오 1: X와 Y가 같은 테이블에 있는 경우

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db",
    
    // X축: ourDetectRadar의 distance 속성
    XAxisObjectName = "ourDetectRadar",
    XAxisAttributeName = "distance",
    
    // Y축: ourDetectRadar의 position.x 속성
    YAxisObjectName = "ourDetectRadar",
    YAxisAttributeName = "position.x",
    
    RetryCount = 3,
    RetryIntervalMs = 10
};

DatabaseQueryService.Start(config);
```

**해석 결과:**
```
X축: Object_Table_0.COL1
Y축: Object_Table_0.COL13
같은 테이블: true

쿼리:
SELECT COL1, COL13 
FROM Object_Table_0 
WHERE s_time = @time
```

### 시나리오 2: X와 Y가 다른 테이블에 있는 경우

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db",
    
    // X축: ourDetectRadar의 distance 속성
    XAxisObjectName = "ourDetectRadar",
    XAxisAttributeName = "distance",
    
    // Y축: ourLauncher의 missile_count 속성
    YAxisObjectName = "ourLauncher",
    YAxisAttributeName = "missile_count",
    
    RetryCount = 3,
    RetryIntervalMs = 10
};

DatabaseQueryService.Start(config);
```

**해석 결과:**
```
X축: Object_Table_0.COL1
Y축: Object_Table_1.COL11
같은 테이블: false

쿼리 (2개 필요):
SELECT COL1 FROM Object_Table_0 WHERE s_time = @time
SELECT COL11 FROM Object_Table_1 WHERE s_time = @time
```

### 시나리오 3: 미사일 위치 추적

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db",
    
    // X축: ourMissile의 position.x
    XAxisObjectName = "ourMissile",
    XAxisAttributeName = "position.x",
    
    // Y축: ourMissile의 position.y
    YAxisObjectName = "ourMissile",
    YAxisAttributeName = "position.y"
};

DatabaseQueryService.Start(config);
```

**해석 결과:**
```
X축: Object_Table_2.COL16
Y축: Object_Table_2.COL17
같은 테이블: true

쿼리:
SELECT COL16, COL17 
FROM Object_Table_2 
WHERE s_time = @time
```

---

## 🔄 처리 흐름

```
1. 사용자가 DatabaseQueryConfig 설정
   ↓
2. DatabaseQueryService.Start() 호출
   ↓
3. SQLite 연결 생성
   ↓
4. 첫 데이터 조회 시도
   ↓
5. MetadataResolver.Resolve() 호출
   ├─ Object_Info에서 table_name 조회
   │  (ourDetectRadar → Object_Table_0)
   ├─ Column_Info에서 column_name 조회
   │  (Object_Table_0 + distance → COL1)
   └─ ResolvedQueryInfo 생성
   ↓
6. ResolvedQueryInfo를 사용하여 실제 쿼리 실행
   ├─ 같은 테이블: 단일 쿼리
   └─ 다른 테이블: 2개 쿼리
   ↓
7. ChartDataPoint 반환
```

---

## ⚠️ 주의사항

### 1. 메타데이터 테이블 생성 시점

- `Object_Info`, `Column_Info`는 **조회 대상 테이블의 첫 행이 채워질 때** 생성됨
- 따라서 첫 조회 시점에 메타데이터 해석이 필요

### 2. 시간 컬럼 고정

- 모든 테이블의 시간 컬럼은 `s_time`으로 고정
- `TimeAttributeName` 설정 불필요

### 3. 오류 처리

메타데이터 조회 실패 시 명확한 오류 메시지:

```csharp
// Object_Info에 object_name이 없는 경우
throw new InvalidOperationException(
    $"Object_Info에서 object_name='{objectName}'을 찾을 수 없습니다.");

// Column_Info에 attribute_name이 없는 경우
throw new InvalidOperationException(
    $"Column_Info에서 table_name='{tableName}', " +
    $"attribute_name='{attributeName}'을 찾을 수 없습니다.");
```

### 4. 성능 고려사항

- 메타데이터 해석은 **최초 1회만** 수행
- 이후 `ResolvedQueryInfo`를 캐싱하여 재사용
- 연결은 WAL 모드로 재사용

---

## 🧪 사용 가능한 Object와 Attribute

### ourDetectRadar (Object_Table_0)

| Attribute | Column | Type |
|-----------|--------|------|
| deltaT | COL0 | DOUBLE |
| distance | COL1 | DOUBLE |
| enemyOrientation.phi | COL2 | DOUBLE |
| enemyOrientation.theta | COL3 | DOUBLE |
| enemyOrientation.psi | COL4 | DOUBLE |
| enemyPosition.x | COL5 | DOUBLE |
| enemyPosition.y | COL6 | DOUBLE |
| enemyPosition.z | COL7 | DOUBLE |
| enemySpeed | COL8 | DOUBLE |
| lockon | COL9 | INT16 |
| orientation.phi | COL10 | DOUBLE |
| orientation.theta | COL11 | DOUBLE |
| orientation.psi | COL12 | DOUBLE |
| position.x | COL13 | DOUBLE |
| position.y | COL14 | DOUBLE |
| position.z | COL15 | DOUBLE |

### ourLauncher (Object_Table_1)

| Attribute | Column | Type |
|-----------|--------|------|
| deltaT | COL0 | DOUBLE |
| enemyOrientation.phi | COL1 | DOUBLE |
| enemyOrientation.theta | COL2 | DOUBLE |
| enemyOrientation.psi | COL3 | DOUBLE |
| enemyPosition.x | COL4 | DOUBLE |
| enemyPosition.y | COL5 | DOUBLE |
| enemyPosition.z | COL6 | DOUBLE |
| enemySpeed | COL7 | DOUBLE |
| iLaunch | COL8 | INT16 |
| lockon | COL9 | INT16 |
| m_status | COL10 | INT16 |
| missile_count | COL11 | UINT32 |
| position.x | COL12 | DOUBLE |
| position.y | COL13 | DOUBLE |
| position.z | COL14 | DOUBLE |
| positionC.x | COL15 | DOUBLE |
| positionC.y | COL16 | DOUBLE |
| positionC.z | COL17 | DOUBLE |

### ourMissile (Object_Table_2)

| Attribute | Column | Type |
|-----------|--------|------|
| dTime | COL0 | DOUBLE |
| damageAssMode | COL1 | INT16 |
| deltaT | COL2 | DOUBLE |
| enemyOrientation.phi | COL3 | DOUBLE |
| enemyOrientation.theta | COL4 | DOUBLE |
| enemyOrientation.psi | COL5 | DOUBLE |
| enemyPosition.x | COL6 | DOUBLE |
| enemyPosition.y | COL7 | DOUBLE |
| enemyPosition.z | COL8 | DOUBLE |
| enemySpeed | COL9 | DOUBLE |
| fire | COL10 | INT16 |
| iLaunch | COL11 | INT16 |
| missileCount | COL12 | UINT32 |
| orientation.phi | COL13 | DOUBLE |
| orientation.theta | COL14 | DOUBLE |
| orientation.psi | COL15 | DOUBLE |
| position.x | COL16 | DOUBLE |
| position.y | COL17 | DOUBLE |
| position.z | COL18 | DOUBLE |
| positionC.x | COL19 | DOUBLE |
| positionC.y | COL20 | DOUBLE |
| positionC.z | COL21 | DOUBLE |

---

## 📝 마이그레이션 가이드

### 기존 코드

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"C:\Data\simulation.db",
    TableName = "SimulationData",           // ❌ 제거됨
    XAxisColumnName = "Temperature",        // ❌ 제거됨
    YAxisColumnName = "Pressure",           // ❌ 제거됨
    TimeColumnName = "Time"                 // ❌ 제거됨 (s_time으로 고정)
};
```

### 새로운 코드

```csharp
var config = new DatabaseQueryConfig
{
    DatabasePath = @"c:\Users\CEO\source\repos\SimulationSpeedTimer\SimulationSpeedTimer\journal_0000001.db",
    
    // Object_Info, Column_Info에서 조회할 메타데이터
    XAxisObjectName = "ourDetectRadar",     // ✅ 추가
    XAxisAttributeName = "distance",        // ✅ 추가
    YAxisObjectName = "ourLauncher",        // ✅ 추가
    YAxisAttributeName = "missile_count"    // ✅ 추가
};
```

---

## 🎯 다음 단계

1. ✅ `DatabaseQueryConfig` 리팩토링 완료
2. ✅ `ResolvedQueryInfo` 클래스 생성 완료
3. ✅ `MetadataResolver` 서비스 구현 완료
4. ⏳ `DatabaseQueryService.cs` 수정
   - `MetadataResolver` 통합
   - `ResolvedQueryInfo` 사용
   - 같은/다른 테이블 케이스 처리

5. ⏳ 테스트 코드 작성
   - 같은 테이블 시나리오
   - 다른 테이블 시나리오
   - 메타데이터 누락 시나리오

6. ⏳ 문서화
   - API 문서
   - 사용 예시
   - 트러블슈팅 가이드
