using System;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 비교 보고서 내보내기를 수행합니다.
    /// </summary>
    public interface IComparisonExportService
    {
        bool TryExportHtml(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset,
            ComparisonExportOptions options,
            out string reportPath);
    }

    public class ComparisonExportService : IComparisonExportService
    {
        // 비교 계산, 렌더링, 경로 생성, 파일 저장을 각각 분리합니다.
        private readonly IComparisonReportBuilder _reportBuilder;
        private readonly IComparisonHtmlRenderer _htmlRenderer;
        private readonly IComparisonReportPathBuilder _pathBuilder;
        private readonly IComparisonFileWriter _fileWriter;

        public ComparisonExportService()
            : this(
                new ComparisonReportBuilder(),
                new ComparisonHtmlRenderer(),
                new ComparisonReportPathBuilder(),
                new ComparisonFileWriter())
        {
        }

        public ComparisonExportService(
            IComparisonReportBuilder reportBuilder,
            IComparisonHtmlRenderer htmlRenderer,
            IComparisonReportPathBuilder pathBuilder,
            IComparisonFileWriter fileWriter)
        {
            _reportBuilder = reportBuilder ?? throw new ArgumentNullException(nameof(reportBuilder));
            _htmlRenderer = htmlRenderer ?? throw new ArgumentNullException(nameof(htmlRenderer));
            _pathBuilder = pathBuilder ?? throw new ArgumentNullException(nameof(pathBuilder));
            _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        }

        public bool TryExportHtml(
            ComparisonDataset sourceDataset,
            ComparisonDataset targetDataset,
            ComparisonExportOptions options,
            out string reportPath)
        {
            reportPath = null;

            try
            {
                if (options == null) throw new ArgumentNullException(nameof(options));

                // 예외가 나더라도 호출부로 전파하지 않고 false로 막습니다.
                var report = _reportBuilder.Build(sourceDataset, targetDataset, options);
                var html = _htmlRenderer.Render(report);
                reportPath = _pathBuilder.BuildPath(options, report.CreatedAt);

                _fileWriter.WriteAllText(reportPath, html);

                Console.WriteLine($"[ComparisonExport] Saved: {reportPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ComparisonExport] Failed: {ex.Message}");
                return false;
            }
        }
    }
}
