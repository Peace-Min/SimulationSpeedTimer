using System;
using System.Data.SQLite;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// Object_Info, Column_Info 테이블에서 메타데이터를 조회하여 실제 쿼리 정보로 변환하는 서비스
    /// </summary>
    public static class MetadataResolver
    {
        /// <summary>
        /// DatabaseQueryConfig의 메타데이터를 기반으로 실제 쿼리 정보를 해석
        /// </summary>
        /// <param name="config">메타데이터 설정</param>
        /// <param name="connection">SQLite 연결</param>
        /// <returns>해석된 쿼리 정보</returns>
        public static ResolvedQueryInfo Resolve(DatabaseQueryConfig config, SQLiteConnection connection)
        {
            var resolved = new ResolvedQueryInfo();

            // X축 정보 해석
            var xAxisInfo = ResolveAxis(
                connection,
                config.XAxisObjectName,
                config.XAxisAttributeName);

            resolved.XAxisTableName = xAxisInfo.TableName;
            resolved.XAxisColumnName = xAxisInfo.ColumnName;
            resolved.XAxisTimeColumnName = xAxisInfo.TimeColumnName;

            // Y축 정보 해석
            var yAxisInfo = ResolveAxis(
                connection,
                config.YAxisObjectName,
                config.YAxisAttributeName);

            resolved.YAxisTableName = yAxisInfo.TableName;
            resolved.YAxisColumnName = yAxisInfo.ColumnName;
            resolved.YAxisTimeColumnName = yAxisInfo.TimeColumnName;

            Console.WriteLine($"[Metadata] X축: {resolved.XAxisTableName}.{resolved.XAxisColumnName}");
            Console.WriteLine($"[Metadata] Y축: {resolved.YAxisTableName}.{resolved.YAxisColumnName}");
            Console.WriteLine($"[Metadata] 같은 테이블: {resolved.IsSameTable}");

            return resolved;
        }

        /// <summary>
        /// 단일 축(X 또는 Y)의 정보를 해석
        /// </summary>
        private static (string TableName, string ColumnName, string TimeColumnName) ResolveAxis(
            SQLiteConnection connection,
            string objectName,
            string attributeName)
        {
            string tableName = null;
            string columnName = null;
            string timeColumnName = null;

            // 1. Object_Info에서 table_name 조회
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT table_name 
                    FROM Object_Info 
                    WHERE object_name = @objectName
                    LIMIT 1";
                cmd.Parameters.AddWithValue("@objectName", objectName);

                var result = cmd.ExecuteScalar();
                if (result == null)
                {
                    throw new InvalidOperationException(
                        $"Object_Info에서 object_name='{objectName}'을 찾을 수 없습니다.");
                }

                tableName = result.ToString();
            }

            // 2. Column_Info에서 데이터 컬럼명 조회
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT column_name 
                    FROM Column_Info 
                    WHERE table_name = @tableName 
                      AND attribute_name = @attributeName
                    LIMIT 1";
                cmd.Parameters.AddWithValue("@tableName", tableName);
                cmd.Parameters.AddWithValue("@attributeName", attributeName);

                var result = cmd.ExecuteScalar();
                if (result == null)
                {
                    throw new InvalidOperationException(
                        $"Column_Info에서 table_name='{tableName}', attribute_name='{attributeName}'을 찾을 수 없습니다.");
                }

                columnName = result.ToString();
            }

            // 3. 시간 컬럼명 (모든 테이블에서 's_time'으로 고정)
            timeColumnName = "s_time";

            return (tableName, columnName, timeColumnName);
        }
    }
}
