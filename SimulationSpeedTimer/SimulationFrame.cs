using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 특정 시점(Time)의 시뮬레이션 전체 데이터 스냅샷
    /// </summary>
    public class SimulationFrame
    {
        /// <summary>
        /// 시뮬레이션 시간 (s_time)
        /// </summary>
        public double Time { get; private set; }

        /// <summary>
        /// 테이블 데이터 컬렉션 (Key: Table Name)
        /// </summary>
        private Dictionary<string, SimulationTable> _tables;

        public SimulationFrame(double time)
        {
            Time = time;
            _tables = new Dictionary<string, SimulationTable>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 테스트 및 전체 접근용 테이블 컬렉션
        /// </summary>
        public IReadOnlyDictionary<string, SimulationTable> Tables => _tables;

        /// <summary>
        /// 테이블 데이터를 추가하거나 갱신합니다.
        /// </summary>
        public void AddOrUpdateTable(SimulationTable table)
        {
            if (table != null)
            {
                _tables[table.TableName] = table;
            }
        }

        /// <summary>
        /// 특정 테이블 데이터 조회
        /// </summary>
        public SimulationTable GetTable(string tableName)
        {
            if (_tables.TryGetValue(tableName, out var table))
                return table;
            return null;
        }

        /// <summary>
        /// 모든 테이블 데이터 순회
        /// </summary>
        public IEnumerable<SimulationTable> AllTables => _tables.Values;

        public bool IsEmpty => _tables.Count == 0;
    }

    /// <summary>
    /// 단일 테이블의 한 시점 Row 데이터 모델
    /// Dictionary를 래핑하여 가독성과 타입 편의성을 제공합니다.
    /// </summary>
    public class SimulationTable
    {
        public string TableName { get; }

        // 실제 컬럼 데이터 저장소 (Key: ColumnName, 예: "COL1")
        private Dictionary<string, object> _columns;

        public SimulationTable(string tableName, Dictionary<string, object> data = null)
        {
            TableName = tableName;
            // 대소문자 구분 없이 접근 가능하도록 설정
            _columns = data != null 
                ? new Dictionary<string, object>(data, StringComparer.OrdinalIgnoreCase) 
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 데이터 추가 (빌더 패턴 지원)
        /// </summary>
        public SimulationTable AddColumn(string columnName, object value)
        {
            _columns[columnName] = value;
            return this;
        }

        /// <summary>
        /// [인덱서] 컬럼명으로 값에 접근 (object 반환)
        /// 예: table["COL1"]
        /// </summary>
        public object this[string columnName]
        {
            get
            {
                if (_columns.TryGetValue(columnName, out var val)) return val;
                return null;
            }
        }

        /// <summary>
        /// [Helper] 타입 변환을 포함한 값 조회
        /// 예: table.Get<double>("COL1")
        /// </summary>
        public T Get<T>(string columnName, T defaultValue = default)
        {
            if (_columns.TryGetValue(columnName, out var val) && val != null)
            {
                try
                {
                    return (T)Convert.ChangeType(val, typeof(T));
                }
                catch
                {
                    // 변환 실패 시 기본값 리턴
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        public bool ContainsColumn(string columnName) => _columns.ContainsKey(columnName);
        
        public int ColumnCount => _columns.Count;
        
        public IEnumerable<string> ColumnNames => _columns.Keys;
    }
}
