using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB의 전체 동적 스키마 정보를 메모리에 보유하는 모델
    /// (Object_Info, Column_Info의 내용을 객체화)
    /// </summary>
    public class SimulationSchema
    {
        // Key: Table Name (예: "Object_Table_0")
        private Dictionary<string, SchemaTableInfo> _tables;

        // Key: Object Name (예: "ourDetectRadar") - 논리적 이름으로 검색 최적화
        private Dictionary<string, SchemaTableInfo> _tablesByObject;

        public SimulationSchema()
        {
            _tables = new Dictionary<string, SchemaTableInfo>(StringComparer.OrdinalIgnoreCase);
            _tablesByObject = new Dictionary<string, SchemaTableInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public void AddTable(SchemaTableInfo table)
        {
            if (table != null)
            {
                // 물리적 이름 인덱싱
                _tables[table.TableName] = table;

                // 논리적 이름 인덱싱 (Object_Info 기준)
                if (!string.IsNullOrEmpty(table.ObjectName))
                {
                    _tablesByObject[table.ObjectName] = table;
                }
            }
        }

        /// <summary>
        /// 물리적 테이블 이름으로 스키마 조회
        /// </summary>
        public SchemaTableInfo GetTable(string tableName)
        {
            if (_tables.TryGetValue(tableName, out var table))
                return table;
            return null;
        }

        /// <summary>
        /// 논리적 객체 이름으로 스키마 조회 (Controller 설정 연동용)
        /// 예: "ourDetectRadar" -> Object_Table_0 정보 반환
        /// </summary>
        public SchemaTableInfo GetTableByObject(string objectName)
        {
            if (_tablesByObject.TryGetValue(objectName, out var table))
                return table;
            return null;
        }

        public IEnumerable<SchemaTableInfo> Tables => _tables.Values;
        
        /// <summary>
        /// 전체 스키마에 정의된 모든 물리적 컬럼의 총 개수 (디버깅/통계용)
        /// </summary>
        public int TotalColumnCount => _tables.Values.Sum(t => t.Columns.Count());
    }

    /// <summary>
    /// 하나의 물리적 테이블에 대한 메타데이터
    /// (Object_Info + Column_Info 집계)
    /// </summary>
    public class SchemaTableInfo
    {
        /// <summary>
        /// 물리적 테이블 이름 (예: "Object_Table_0")
        /// </summary>
        public string TableName { get; set; }

        /// <summary>
        /// 논리적 객체 이름 (예: "ourDetectRadar")
        /// </summary>
        public string ObjectName { get; set; }

        /// <summary>
        /// 이 테이블에 포함된 컬럼들의 목록
        /// Key: Column Name (예: "COL1") -> 물리적 컬럼명 기준 검색용
        /// </summary>
        public Dictionary<string, SchemaColumnInfo> ColumnsByPhysicalName { get; }
        
        /// <summary>
        /// 이 테이블에 포함된 컬럼들의 목록
        /// Key: Attribute Name (예: "distance") -> 논리적 속성명 기준 검색용
        /// </summary>
        public Dictionary<string, SchemaColumnInfo> ColumnsByAttributeName { get; }

        public SchemaTableInfo(string tableName, string objectName)
        {
            TableName = tableName;
            ObjectName = objectName;
            ColumnsByPhysicalName = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
            ColumnsByAttributeName = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public void AddColumn(SchemaColumnInfo column)
        {
            // 물리적 컬럼명으로 인덱싱 ("COL1")
            ColumnsByPhysicalName[column.ColumnName] = column;
            
            // 논리적 속성명으로 인덱싱 ("distance")
            // 속성명은 중복될 일이 드물지만, 혹시 모를 충돌 방지를 위해 체크
            if (!ColumnsByAttributeName.ContainsKey(column.AttributeName))
            {
                ColumnsByAttributeName[column.AttributeName] = column;
            }
        }
        
        /// <summary>
        /// 순회 및 쿼리 생성을 위한 컬럼 리스트 반환
        /// </summary>
        public IEnumerable<SchemaColumnInfo> Columns => ColumnsByPhysicalName.Values;
    }

    /// <summary>
    /// 하나의 컬럼에 대한 메타데이터
    /// (Column_Info의 한 행)
    /// </summary>
    public class SchemaColumnInfo
    {
        /// <summary>
        /// 물리적 컬럼 이름 (예: "COL1")
        /// </summary>
        public string ColumnName { get; set; }

        /// <summary>
        /// 논리적 속성 이름 (예: "distance")
        /// </summary>
        public string AttributeName { get; set; }

        /// <summary>
        /// 데이터 타입 (예: "DOUBLE_TYPE")
        /// </summary>
        public string DataType { get; set; }
        
        public SchemaColumnInfo(string columnName, string attributeName, string dataType)
        {
            ColumnName = columnName;
            AttributeName = attributeName;
            DataType = dataType;
        }
    }
}
