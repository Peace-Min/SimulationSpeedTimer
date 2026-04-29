using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 스키마 검증 데이터를 범용 비교 모델로 바꿉니다.
    /// </summary>
    public static class SchemaValidationComparisonFactory
    {
        public static ComparisonDataset CreateSourceDataset(SimulationSchema schema)
        {
            var dataset = new ComparisonDataset();

            if (schema == null || schema.Tables == null)
            {
                return dataset;
            }

            foreach (var table in schema.Tables.OrderBy(item => item.ObjectName ?? item.TableName, StringComparer.OrdinalIgnoreCase))
            {
                // DB 쪽은 ObjectName 기준으로 평탄화합니다.
                var key = table.ObjectName ?? table.TableName ?? string.Empty;
                var items = table.Columns
                    .Select(column => column.AttributeName)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                dataset.Objects[key] = items;
            }

            return dataset;
        }

        public static ComparisonDataset CreateTargetDataset(Dictionary<string, List<string>> expectedColumns)
        {
            var dataset = new ComparisonDataset();

            if (expectedColumns == null)
            {
                return dataset;
            }

            foreach (var pair in expectedColumns.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Config 쪽도 같은 형태로 맞춰 비교합니다.
                var key = pair.Key ?? string.Empty;
                var items = (pair.Value ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                dataset.Objects[key] = items;
            }

            return dataset;
        }

        public static ComparisonExportOptions CreateOptions(string dbPath)
        {
            var dbName = Path.GetFileNameWithoutExtension(dbPath);
            var title = string.IsNullOrWhiteSpace(dbName)
                ? "DB_Config_비교"
                : $"DB_Config_비교_{dbName}";

            return new ComparisonExportOptions
            {
                // 기본 출력 위치와 비교 방식을 여기서 고정합니다.
                OutputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Comparison"),
                Title = title,
                SourceLabel = "DB 스키마",
                TargetLabel = "Config",
                Mode = ComparisonMode.TargetInSource,
            };
        }
    }
}
