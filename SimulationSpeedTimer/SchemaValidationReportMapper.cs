using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    public static class SchemaValidationReportMapper
    {
        public static SchemaValidationReportSnapshot Map(
            string dbPath,
            Dictionary<string, List<string>> expectedColumns,
            SimulationSchema schema,
            bool isSuccess)
        {
            var normalizedExpectedColumns = expectedColumns ?? new Dictionary<string, List<string>>();
            var snapshot = new SchemaValidationReportSnapshot
            {
                CreatedAt = System.DateTime.Now,
                DbPath = dbPath,
                IsSuccess = isSuccess,
            };

            foreach (var configuredPair in normalizedExpectedColumns.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var objectName = configuredPair.Key ?? string.Empty;
                var requiredAttributes = (configuredPair.Value ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var matchedTable = schema?.GetTableByObject(objectName);
                var dbAttributes = matchedTable == null
                    ? new List<string>()
                    : matchedTable.Columns
                        .Select(column => column.AttributeName)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                var dbAttributeSet = new HashSet<string>(dbAttributes, StringComparer.OrdinalIgnoreCase);
                var missingAttributes = requiredAttributes
                    .Where(attribute => !dbAttributeSet.Contains(attribute))
                    .ToList();

                snapshot.ConfiguredObjects.Add(new SchemaValidationConfiguredObjectEntry
                {
                    ObjectName = objectName,
                    TableName = matchedTable?.TableName,
                    RequiredAttributes = requiredAttributes,
                    DbAttributes = dbAttributes,
                    MissingAttributes = missingAttributes,
                    IsSuccess = matchedTable != null && missingAttributes.Count == 0,
                });
            }

            if (schema != null && schema.Tables != null)
            {
                snapshot.DbObjects = schema.Tables
                    .OrderBy(table => table.ObjectName ?? table.TableName, StringComparer.OrdinalIgnoreCase)
                    .Select(table => new SchemaValidationDbObjectEntry
                    {
                        ObjectName = table.ObjectName,
                        TableName = table.TableName,
                        Attributes = table.Columns
                            .Select(column => column.AttributeName)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    })
                    .ToList();
            }

            return snapshot;
        }
    }
}
