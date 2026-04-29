using System;
using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 비교 기준을 정합니다.
    /// </summary>
    public enum ComparisonMode
    {
        // 양쪽 항목이 완전히 같은지 비교합니다.
        Equal,
        // 소스 항목이 타겟 안에 모두 있는지 비교합니다.
        SourceInTarget,
        // 타겟 항목이 소스 안에 모두 있는지 비교합니다.
        TargetInSource,
    }

    /// <summary>
    /// 비교 입력 집합입니다.
    /// </summary>
    public class ComparisonDataset
    {
        public ComparisonDataset()
        {
            Objects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 객체 키와 항목 목록입니다.
        /// </summary>
        public Dictionary<string, List<string>> Objects { get; set; }
    }

    /// <summary>
    /// 비교 내보내기 옵션입니다.
    /// </summary>
    public class ComparisonExportOptions
    {
        public string OutputDirectory { get; set; }

        public string Title { get; set; }

        public string SourceLabel { get; set; }

        public string TargetLabel { get; set; }

        public ComparisonMode Mode { get; set; }
    }

    /// <summary>
    /// 비교 표의 한 행입니다.
    /// </summary>
    public class ComparisonRow
    {
        public string SourceKey { get; set; }

        public string TargetKey { get; set; }

        public List<string> SourceItems { get; set; } = new List<string>();

        public List<string> TargetItems { get; set; } = new List<string>();

        public List<string> MissingItems { get; set; } = new List<string>();

        public List<string> ExtraItems { get; set; } = new List<string>();

        public bool IsMatch { get; set; }
    }

    /// <summary>
    /// 렌더링에 사용하는 최종 비교 결과입니다.
    /// </summary>
    public class ComparisonReport
    {
        public DateTime CreatedAt { get; set; }

        public string Title { get; set; }

        public ComparisonMode Mode { get; set; }

        public string SourceLabel { get; set; }

        public string TargetLabel { get; set; }

        public ComparisonDataset SourceDataset { get; set; }

        public ComparisonDataset TargetDataset { get; set; }

        public List<ComparisonRow> Rows { get; set; } = new List<ComparisonRow>();

        public bool IsMatch { get; set; }
    }
}
