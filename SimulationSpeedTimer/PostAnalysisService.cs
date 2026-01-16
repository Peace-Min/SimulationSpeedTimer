using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// [사후 분석용 모델]
    /// 하나의 물리/논리 테이블에 대한 "전체 시간" 데이터를 담는 객체.
    /// 시간(Key) 기준으로 해당 시점의 모든 컬럼 값을 조회할 수 있음.
    /// </summary>
    public class SimulationCompleteTable
    {
        /// <summary>
        /// 테이블 이름 (Object_Info가 있으면 논리명, 없으면 물리명)
        /// </summary>
        public string TableName { get; private set; }

        /// <summary>
        /// 시간별 데이터 모음
        /// Key: s_time (시간)
        /// Value: 해당 시간의 컬럼 값들 (Key: 컬럼명/속성명, Value: 실제 값)
        /// </summary>
        public Dictionary<double, Dictionary<string, object>> TimeData { get; } 
            = new Dictionary<double, Dictionary<string, object>>();

        public SimulationCompleteTable(string tableName)
        {
            TableName = tableName;
        }

        public void AddRow(double time, Dictionary<string, object> rowData)
        {
            // 중복 시간 데이터 덮어쓰기 허용 (최신 값 기준)
            TimeData[time] = rowData;
        }
    }

    /// <summary>
    /// [사후 분석용 서비스]
    /// 시뮬레이션 종료 후 DB 경로만 입력받아
    /// 모든 테이블의 전체 데이터를 메모리로 로드하여 반환하는 정적 서비스.
    /// GlobalDataService(실시간)와는 독립적으로 동작함.
    /// </summary>
    public static class PostAnalysisService
    {
        /// <summary>
        /// 지정된 DB의 모든 테이블 데이터를 읽어와 분석용 모델 리스트로 반환합니다.
        /// </summary>
        /// <param name="dbPath">SQLite DB 파일 경로</param>
        /// <returns>테이블별 전체 데이터 리스트</returns>
        public static List<SimulationCompleteTable> LoadAllData(string dbPath)
        {
            var result = new List<SimulationCompleteTable>();
            var connectionString = $"Data Source={dbPath};Pooling=false;FailIfMissing=true;Read Only=True;";

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                // 1. 스키마 정보 로드 (테이블 목록 & 컬럼 매핑 정보)
                var schema = LoadAnalysisSchema(conn);

                // 2. 각 테이블별 전체 데이터 로드
                foreach (var tableInfo in schema.Tables)
                {
                    var completeTable = LoadTableData(conn, tableInfo);
                    result.Add(completeTable);
                }
            }

            return result;
        }

        private static SimulationSchema LoadAnalysisSchema(SQLiteConnection conn)
        {
            var schema = new SimulationSchema();

            try
            {
                // Object_Info (테이블 매핑)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Object_Info'";
                    var exists = cmd.ExecuteScalar();
                    
                    if (exists != null)
                    {
                        cmd.CommandText = "SELECT object_name, table_name FROM Object_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                schema.AddTable(new SchemaTableInfo(
                                    reader["table_name"]?.ToString(), 
                                    reader["object_name"]?.ToString()
                                ));
                            }
                        }
                    }
                }

                // Column_Info (컬럼 매핑)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Column_Info'";
                    var exists = cmd.ExecuteScalar();

                    if (exists != null)
                    {
                        cmd.CommandText = "SELECT table_name, column_name, attribute_name, data_type FROM Column_Info";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var t = schema.GetTable(reader["table_name"]?.ToString());
                                // 만약 Object_Info에 없던 테이블이라도 Column_Info에 있으면 일단 물리명=논리명으로 생성 시도
                                if (t == null)
                                {
                                    string tName = reader["table_name"]?.ToString();
                                    t = new SchemaTableInfo(tName, tName);
                                    schema.AddTable(t);
                                }

                                t.AddColumn(new SchemaColumnInfo(
                                    reader["column_name"]?.ToString(), 
                                    reader["attribute_name"]?.ToString(), 
                                    reader["data_type"]?.ToString()
                                ));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PostAnalysisService] Schema Load Warning: {ex.Message}");
            }

            return schema;
        }

        private static SimulationCompleteTable LoadTableData(SQLiteConnection conn, SchemaTableInfo tableInfo)
        {
            // 논리명 우선, 없으면 물리명 사용
            string displayName = !string.IsNullOrEmpty(tableInfo.ObjectName) ? tableInfo.ObjectName : tableInfo.TableName;
            var completeTable = new SimulationCompleteTable(displayName);

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    // 시간 순 정렬
                    cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} ORDER BY s_time ASC";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            double time = 0.0;
                            var rowColumns = new Dictionary<string, object>();

                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                string colName = reader.GetName(i);
                                var val = reader.GetValue(i);

                                if (colName.Equals("s_time", StringComparison.OrdinalIgnoreCase))
                                {
                                    time = Convert.ToDouble(val);
                                    continue;
                                }

                                if (val == DBNull.Value) continue;

                                // 컬럼명 매핑 (COL1 -> Velocity)
                                string mappedName = colName;
                                if (tableInfo.ColumnsByPhysicalName.TryGetValue(colName, out var colInfo))
                                {
                                    mappedName = colInfo.AttributeName;
                                }

                                rowColumns[mappedName] = val;
                            }

                            // 로우 추가
                            completeTable.AddRow(time, rowColumns);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PostAnalysisService] Failed to load table {tableInfo.TableName}: {ex.Message}");
            }

            return completeTable;
        }
    }
}
