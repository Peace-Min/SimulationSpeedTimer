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
            string timeColumnName = "s_time"; // 시간 컬럼은 's_time'으로 고정

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
            // 예외 처리: 속성명이 's_time'인 경우 메타데이터 조회 없이 바로 사용
            if (attributeName.Equals("s_time", StringComparison.OrdinalIgnoreCase) ||
                attributeName.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                columnName = "s_time";
            }
            else
            {
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
            }

            return (tableName, columnName, timeColumnName);
        }

        /// <summary>
        /// 메타데이터 테이블(Object_Info, Column_Info)이 존재하고, 필요한 매핑 정보가 있는지 확인
        /// </summary>
        public static bool AreMetadataTablesReady(SQLiteConnection connection, DatabaseQueryConfig config = null)
        {
            // 1. 테이블 존재 여부 확인
            if (!TableExists(connection, "Object_Info") || !TableExists(connection, "Column_Info"))
            {
                return false;
            }

            // 2. config가 없으면 테이블 존재만으로 true 반환
            if (config == null)
            {
                return true;
            }

            // 3. 실제 매핑 데이터가 존재하는지 확인 (ResolveAxis가 성공할 수 있는지)
            return IsAxisReady(connection, config.XAxisObjectName, config.XAxisAttributeName) &&
                   IsAxisReady(connection, config.YAxisObjectName, config.YAxisAttributeName);
        }

        private static bool IsAxisReady(SQLiteConnection connection, string objectName, string attributeName)
        {
            string tableName = null;

            // 1. Object_Info에서 table_name 조회
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT table_name FROM Object_Info WHERE object_name = @objectName LIMIT 1";
                cmd.Parameters.AddWithValue("@objectName", objectName);
                var result = cmd.ExecuteScalar();
                if (result == null) return false;
                tableName = result.ToString();
            }

            // 2. Column_Info에서 데이터 컬럼명 조회
            // 예외 처리: 속성명이 's_time'인 경우 메타데이터 조회 없이 바로 사용 가능하므로 true
            if (attributeName.Equals("s_time", StringComparison.OrdinalIgnoreCase) ||
                attributeName.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM Column_Info WHERE table_name = @tableName AND attribute_name = @attributeName LIMIT 1";
                cmd.Parameters.AddWithValue("@tableName", tableName);
                cmd.Parameters.AddWithValue("@attributeName", attributeName);
                return cmd.ExecuteScalar() != null;
            }
        }

        private static bool TableExists(SQLiteConnection connection, string tableName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
                cmd.Parameters.AddWithValue("@name", tableName);
                return cmd.ExecuteScalar() != null;
            }
        }
    }
}
