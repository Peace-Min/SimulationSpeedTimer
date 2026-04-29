using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 비교 결과를 만듭니다.
    /// </summary>
    public interface IComparisonReportBuilder
    {
        ComparisonReport Build(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset,
            ComparisonExportOptions options);
    }

    public class ComparisonReportBuilder : IComparisonReportBuilder
    {
        public ComparisonReport Build(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset,
            ComparisonExportOptions options)
        {
            if (sourceDataset == null) throw new ArgumentNullException(nameof(sourceDataset));
            if (targetDataset == null) throw new ArgumentNullException(nameof(targetDataset));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var normalizedSource = NormalizeDataset(sourceDataset);
            var normalizedTarget = NormalizeDataset(targetDataset);

            // 렌더링 전에 입력 데이터를 먼저 정리합니다.
            var report = new ComparisonReport
            {
                CreatedAt = DateTime.Now,
                Title = string.IsNullOrWhiteSpace(options.Title) ? "\uBE44\uAD50 \uBCF4\uACE0\uC11C" : options.Title,
                Mode = options.Mode,
                SourceDataset = normalizedSource,
                TargetDataset = normalizedTarget,
            };

            report.Rows = BuildRows(normalizedSource, normalizedTarget, options.Mode);
            report.IsMatch = report.Rows.All(row => row.IsMatch);

            return report;
        }

        private static ComparisonDataset NormalizeDataset(ComparisonDataset dataset)
        {
            var normalized = new ComparisonDataset();

            if (dataset.Objects == null)
            {
                return normalized;
            }

            foreach (var pair in dataset.Objects.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = pair.Key ?? string.Empty;
                var normalizedItems = (pair.Value ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                normalized.Objects[key] = normalizedItems;
            }

            return normalized;
        }

        private static List<ComparisonRow> BuildRows(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset,
            ComparisonMode mode)
        {
            // 비교 방식에 따라 기준 집합을 바꿉니다.
            switch (mode)
            {
                case ComparisonMode.SourceInTarget:
                    return BuildSourceInTargetRows(sourceDataset, targetDataset);
                case ComparisonMode.TargetInSource:
                    return BuildTargetInSourceRows(sourceDataset, targetDataset);
                case ComparisonMode.Equal:
                default:
                    return BuildEqualRows(sourceDataset, targetDataset);
            }
        }

        private static List<ComparisonRow> BuildSourceInTargetRows(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset)
        {
            var rows = new List<ComparisonRow>();

            foreach (var pair in sourceDataset.Objects)
            {
                // 소스 항목이 타겟 안에 모두 있는지 확인합니다.
                var sourceKey = pair.Key;
                var sourceItems = pair.Value;
                var targetItems = targetDataset.Objects.TryGetValue(sourceKey, out var matchedTargetItems)
                    ? matchedTargetItems
                    : new List<string>();

                var missingItems = sourceItems
                    .Where(item => !targetItems.Contains(item, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                rows.Add(new ComparisonRow
                {
                    SourceKey = sourceKey,
                    TargetKey = targetDataset.Objects.ContainsKey(sourceKey) ? sourceKey : null,
                    SourceItems = sourceItems,
                    TargetItems = targetItems,
                    MissingItems = missingItems,
                    IsMatch = missingItems.Count == 0,
                });
            }

            return rows;
        }

        private static List<ComparisonRow> BuildTargetInSourceRows(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset)
        {
            var rows = new List<ComparisonRow>();

            foreach (var pair in targetDataset.Objects)
            {
                // 타겟 항목이 소스 안에 모두 있는지 확인합니다.
                var targetKey = pair.Key;
                var targetItems = pair.Value;
                var sourceItems = sourceDataset.Objects.TryGetValue(targetKey, out var matchedSourceItems)
                    ? matchedSourceItems
                    : new List<string>();

                var missingItems = targetItems
                    .Where(item => !sourceItems.Contains(item, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                rows.Add(new ComparisonRow
                {
                    SourceKey = sourceDataset.Objects.ContainsKey(targetKey) ? targetKey : null,
                    TargetKey = targetKey,
                    SourceItems = sourceItems,
                    TargetItems = targetItems,
                    MissingItems = missingItems,
                    IsMatch = missingItems.Count == 0,
                });
            }

            return rows;
        }

        private static List<ComparisonRow> BuildEqualRows(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset)
        {
            // 동등 비교는 양쪽 키 전체를 기준으로 봅니다.
            var keys = sourceDataset.Objects.Keys
                .Union(targetDataset.Objects.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

            var rows = new List<ComparisonRow>();

            foreach (var key in keys)
            {
                var sourceItems = sourceDataset.Objects.TryGetValue(key, out var matchedSourceItems)
                    ? matchedSourceItems
                    : new List<string>();
                var targetItems = targetDataset.Objects.TryGetValue(key, out var matchedTargetItems)
                    ? matchedTargetItems
                    : new List<string>();

                var missingItems = targetItems
                    .Where(item => !sourceItems.Contains(item, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var extraItems = sourceItems
                    .Where(item => !targetItems.Contains(item, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                rows.Add(new ComparisonRow
                {
                    SourceKey = sourceDataset.Objects.ContainsKey(key) ? key : null,
                    TargetKey = targetDataset.Objects.ContainsKey(key) ? key : null,
                    SourceItems = sourceItems,
                    TargetItems = targetItems,
                    MissingItems = missingItems,
                    ExtraItems = extraItems,
                    IsMatch = missingItems.Count == 0 && extraItems.Count == 0,
                });
            }

            return rows;
        }
    }
}
