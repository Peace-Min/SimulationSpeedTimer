using System;
using System.IO;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 보고서 저장 경로를 만듭니다.
    /// </summary>
    public interface IComparisonReportPathBuilder
    {
        string BuildPath(ComparisonExportOptions options, DateTime createdAt);
    }

    public class ComparisonReportPathBuilder : IComparisonReportPathBuilder
    {
        public string BuildPath(ComparisonExportOptions options, DateTime createdAt)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var outputDirectory = ResolveOutputDirectory(options.OutputDirectory);
            var fileName = BuildFileName(options.Title, createdAt);
            return Path.Combine(outputDirectory, fileName);
        }

        private static string ResolveOutputDirectory(string outputDirectory)
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                return outputDirectory;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Comparison");
        }

        private static string BuildFileName(string title, DateTime createdAt)
        {
            // 제목은 파일명으로 쓸 수 있게 한 번 정리합니다.
            var safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "\uBE44\uAD50_\uBCF4\uACE0\uC11C" : title);
            return $"{safeTitle}_{createdAt:yyyyMMdd_HHmmss_fff}.html";
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var buffer = value.ToCharArray();

            for (int i = 0; i < buffer.Length; i++)
            {
                if (Array.IndexOf(invalidChars, buffer[i]) >= 0)
                {
                    buffer[i] = '_';
                }
            }

            return new string(buffer).Trim();
        }
    }
}
