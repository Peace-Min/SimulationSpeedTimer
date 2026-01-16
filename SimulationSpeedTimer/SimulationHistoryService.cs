using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시뮬레이션 완료 후 저장된 DB 파일에서 데이터를 조회하는 분석용 서비스입니다.
    /// 실시간 스트리밍이 아닌, 특정 구간의 전체 이력(History)을 일괄 로드합니다.
    /// </summary>
    public class SimulationHistoryService
    {
        /// <summary>
        /// 지정된 DB 파일에서 start ~ end 구간의 모든 테이블 데이터를 로드하여 반환합니다.
        /// </summary>
        /// <param name="dbPath">SQLite DB 파일 경로</param>
        /// <param name="startTime">시작 시간 (이상)</param>
        /// <param name="endTime">종료 시간 (이하)</param>
        /// <returns>Key: 시간(s_time), Value: 해당 시간의 전체 통합 프레임</returns>
        public Dictionary<double, SimulationFrame> LoadFullHistory(string dbPath, double startTime, double endTime)
        {
            var frames = new Dictionary<double, SimulationFrame>();

            // 읽기 전용으로 연결 (파일 잠금 최소화)
            string connectionString = $"Data Source={dbPath};Version=3;ReadOnly=True;";

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                // 1. 스키마 로드 (테이블 및 컬럼 정보)
                var schema = LoadSchema(conn);
                if (schema == null || !schema.Tables.Any())
                {
                    Console.WriteLine("[SimulationHistoryService] Schema load failed or empty.");
                    return frames;
                }

                // 2. 모든 테이블을 순회하며 데이터 조회 및 병합 - 성능을 위해 Parallel 처리 고려 가능하나 SQLite 특성상 순차처리가 안전
                foreach (var tableInfo in schema.Tables)
                {
                    MergeTableData(conn, tableInfo, startTime, endTime, frames);
                }
            }

            // 시간순 정렬이 필요하다면 여기서 정렬된 Dictionary로 변환할 수 있으나, 
            // Dictionary 자체는 순서를 보장하지 않으므로 호출자가 OrderBy를 쓰는 것이 일반적입니다.
            return frames;
        }

        private void MergeTableData(SQLiteConnection conn, SchemaTableInfo tableInfo, double start, double end, Dictionary<double, SimulationFrame> frames)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    // 구간 조회 (Inclusive: start <= t <= end)
                    cmd.CommandText = $"SELECT * FROM {tableInfo.TableName} WHERE s_time >= @start AND s_time <= @end";
                    cmd.Parameters.AddWithValue("@start", start);
                    cmd.Parameters.AddWithValue("@end", end);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            double time = Convert.ToDouble(reader["s_time"]);

                            // 해당 시간의 프레임이 없으면 생성
                            if (!frames.TryGetValue(time, out var frame))
                            {
                                frame = new SimulationFrame(time);
                                frames[time] = frame;
                            }

                            // 테이블 논리명 매핑 (Object_Info 기준)
                            // [수정] 방어코드 제거: ObjectName이 비어있으면 데이터 무결성 오류로 간주하고 그대로 사용
                            string resolvedTableName = tableInfo.ObjectName;

                            var tableData = new SimulationTable(resolvedTableName);

                            // 컬럼 데이터 매핑
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                string phyColName = reader.GetName(i);
                                if (phyColName == "s_time") continue; // 시간 컬럼 제외

                                // 물리 컬럼명 -> 논리 속성명 변환 (Column_Info 기준)
                                string attrName = phyColName;
                                if (tableInfo.ColumnsByPhysicalName.TryGetValue(phyColName, out var colInfo))
                                {
                                    attrName = colInfo.AttributeName;
                                }

                                var val = reader.GetValue(i);
                                if (val != DBNull.Value)
                                {
                                    tableData.AddColumn(attrName, val);
                                }
                            }

                            // 프레임에 테이블 데이터 추가 (기존에 있으면 덮어씀)
                            frame.AddOrUpdateTable(tableData);
                        }
                    }
                }
            }
            catch (SQLiteException sqliteEx)
            {
                // [1, 3] DB 파일 손상(Malformed) 또는 파일 잠금/IO 에러
                Console.WriteLine($"[SimulationHistoryService] SQLite Error loading table {tableInfo.TableName}: {sqliteEx.Message}");
            }
            catch (Exception ex)
            {
                // 그 외 알 수 없는 에러
                Console.WriteLine($"[SimulationHistoryService] Unexpected Error loading table {tableInfo.TableName}: {ex.Message}");
            }
        }

        private SimulationSchema LoadSchema(SQLiteConnection conn)
        {
            try
            {
                var schema = new SimulationSchema();

                // 1. 테이블 정보 (Object_Info)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT object_name, table_name FROM Object_Info";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string tName = reader["table_name"]?.ToString();
                            string oName = reader["object_name"]?.ToString();
                            if (!string.IsNullOrEmpty(tName))
                            {
                                schema.AddTable(new SchemaTableInfo(tName, oName));
                            }
                        }
                    }
                }

                // 2. 컬럼 정보 (Column_Info)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT table_name, column_name, attribute_name, data_type FROM Column_Info";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string tName = reader["table_name"]?.ToString();
                            var table = schema.GetTable(tName);
                            if (table != null)
                            {
                                table.AddColumn(new SchemaColumnInfo(
                                    reader["column_name"]?.ToString(),
                                    reader["attribute_name"]?.ToString(),
                                    reader["data_type"]?.ToString()
                                ));
                            }
                        }
                    }
                }

                return schema;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimulationHistoryService] Schema Load Error: {ex.Message}");
                return null;
            }
        }
    }
}
