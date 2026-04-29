using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 비교 결과를 HTML로 만듭니다.
    /// </summary>
    public interface IComparisonHtmlRenderer
    {
        string Render(ComparisonReport report);
    }

    public class ComparisonHtmlRenderer : IComparisonHtmlRenderer
    {
        public string Render(ComparisonReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            // 비교 방식에 맞는 표 머리글을 고릅니다.
            var labels = ResolveLabels(report.Mode, report.SourceLabel, report.TargetLabel);
            var html = new StringBuilder();

            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html lang=\"ko\">");
            html.AppendLine("<head>");
            html.AppendLine("    <meta charset=\"utf-8\" />");
            html.AppendLine("    <title>" + Encode(report.Title) + "</title>");
            html.AppendLine("    <style>");
            html.AppendLine("        body { margin: 24px; font-family: \"Segoe UI\", sans-serif; color: #222222; background: #ffffff; }");
            html.AppendLine("        h1, h2 { margin-bottom: 8px; }");
            html.AppendLine("        .meta { margin-bottom: 20px; line-height: 1.7; }");
            html.AppendLine("        table { width: 100%; border-collapse: collapse; margin-bottom: 24px; table-layout: fixed; }");
            html.AppendLine("        th, td { border: 1px solid #cccccc; padding: 9px 10px; text-align: left; vertical-align: top; font-size: 14px; }");
            html.AppendLine("        th { background: #f3f3f3; font-weight: 700; }");
            html.AppendLine("        .count { font-size: 13px; color: #555555; }");
            html.AppendLine("        .result-true { font-weight: 700; }");
            html.AppendLine("        .result-false { font-weight: 700; text-decoration: underline; }");
            html.AppendLine("        .muted { color: #666666; }");
            html.AppendLine("        .details { margin-top: 8px; }");
            html.AppendLine("        details { border: 1px solid #cccccc; margin-bottom: 12px; background: #fafafa; }");
            html.AppendLine("        summary { cursor: pointer; padding: 10px 12px; font-weight: 700; background: #f3f3f3; }");
            html.AppendLine("        .detail-body { padding: 12px; }");
            html.AppendLine("        .detail-grid { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 12px; }");
            html.AppendLine("        .detail-box { border: 1px solid #d8d8d8; background: #ffffff; }");
            html.AppendLine("        .detail-box h3 { margin: 0; padding: 8px 10px; font-size: 14px; background: #f7f7f7; border-bottom: 1px solid #d8d8d8; }");
            html.AppendLine("        .list-wrap { max-height: 240px; overflow: auto; padding: 8px 10px; }");
            html.AppendLine("        ul { margin: 0; padding-left: 18px; }");
            html.AppendLine("        li { font-size: 12px; line-height: 1.5; word-break: break-word; }");
            html.AppendLine("        code { background: #f3f3f3; padding: 1px 4px; border-radius: 4px; }");
            html.AppendLine("        a { color: #222222; }");
            html.AppendLine("    </style>");
            html.AppendLine("    <script>");
            html.AppendLine("        function openDetailFromHash() {");
            html.AppendLine("            var hash = window.location.hash;");
            html.AppendLine("            if (!hash) { return; }");
            html.AppendLine("            var target = document.getElementById(hash.substring(1));");
            html.AppendLine("            if (!target) { return; }");
            html.AppendLine("            if (target.tagName && target.tagName.toLowerCase() === 'details') {");
            html.AppendLine("                target.open = true;");
            html.AppendLine("            }");
            html.AppendLine("            target.scrollIntoView({ behavior: 'smooth', block: 'start' });");
            html.AppendLine("        }");
            html.AppendLine("        document.addEventListener('DOMContentLoaded', openDetailFromHash);");
            html.AppendLine("        window.addEventListener('hashchange', openDetailFromHash);");
            html.AppendLine("    </script>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");
            html.AppendLine("    <h1>" + Encode(report.Title) + "</h1>");
            html.AppendLine("    <div class=\"meta\">");
            html.AppendLine("        <div><strong>생성 시각:</strong> " + Encode(report.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")) + "</div>");
            html.AppendLine("        <div><strong>비교 방식:</strong> <code>" + Encode(report.Mode.ToString()) + "</code></div>");
            html.AppendLine("        <div><strong>최종 결과:</strong> " + RenderResult(report.IsMatch) + "</div>");
            html.AppendLine("    </div>");

            AppendDatasetSummaryTable(
                html,
                "1. " + labels.SourceLabel + " 요약 표",
                labels.SourceKeyLabel,
                report.SourceDataset);

            AppendDatasetSummaryTable(
                html,
                "2. " + labels.TargetLabel + " 요약 표",
                labels.TargetKeyLabel,
                report.TargetDataset);

            AppendComparisonSummaryTable(html, report, labels);
            AppendDetailSections(html, report, labels);

            html.AppendLine("</body>");
            html.AppendLine("</html>");

            return html.ToString();
        }

        private static void AppendDatasetSummaryTable(
            StringBuilder html,
            string title,
            string keyLabel,
            ComparisonDataset dataset)
        {
            html.AppendLine("    <h2>" + title + "</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 70%;\">" + keyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 30%;\">항목 수</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            foreach (var pair in dataset.Objects)
            {
                html.AppendLine("            <tr>");
                html.AppendLine("                <td>" + Encode(pair.Key) + "</td>");
                html.AppendLine("                <td class=\"count\">" + pair.Value.Count + "</td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");
        }

        private static void AppendComparisonSummaryTable(
            StringBuilder html,
            ComparisonReport report,
            ComparisonRenderLabels labels)
        {
            html.AppendLine("    <h2>3. 비교 요약 표</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 22%;\">" + labels.PrimaryKeyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 18%;\">" + labels.SecondaryKeyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 12%;\">" + labels.PrimaryItemsLabel + " 수</th>");
            html.AppendLine("                <th style=\"width: 14%;\">" + labels.SecondaryItemsLabel + " 수</th>");
            html.AppendLine("                <th style=\"width: 12%;\">" + labels.DifferenceLabel + " 수</th>");
            html.AppendLine("                <th style=\"width: 10%;\">결과</th>");
            html.AppendLine("                <th style=\"width: 12%;\">상세</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            for (int i = 0; i < report.Rows.Count; i++)
            {
                var row = report.Rows[i];
                var primaryItems = labels.GetPrimaryItems(row) ?? new List<string>();
                var secondaryItems = labels.GetSecondaryItems(row) ?? new List<string>();
                var differenceItems = GetDifferenceItems(row, report.Mode);
                var detailId = BuildDetailId(labels.GetPrimaryKey(row), i);

                html.AppendLine("            <tr>");
                html.AppendLine("                <td>" + Encode(labels.GetPrimaryKey(row) ?? string.Empty) + "</td>");
                html.AppendLine("                <td>" + RenderText(labels.GetSecondaryKey(row), labels.EmptyKeyText) + "</td>");
                html.AppendLine("                <td class=\"count\">" + primaryItems.Count + "</td>");
                html.AppendLine("                <td class=\"count\">" + secondaryItems.Count + "</td>");
                html.AppendLine("                <td class=\"count\">" + differenceItems.Count + "</td>");
                html.AppendLine("                <td>" + RenderResult(row.IsMatch) + "</td>");
                html.AppendLine("                <td><a href=\"#" + detailId + "\">보기</a></td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");
        }

        private static void AppendDetailSections(
            StringBuilder html,
            ComparisonReport report,
            ComparisonRenderLabels labels)
        {
            html.AppendLine("    <h2>4. 상세</h2>");
            html.AppendLine("    <div class=\"details\">");

            for (int i = 0; i < report.Rows.Count; i++)
            {
                var row = report.Rows[i];
                var primaryKey = labels.GetPrimaryKey(row) ?? string.Empty;
                var secondaryKey = labels.GetSecondaryKey(row);
                var primaryItems = labels.GetPrimaryItems(row) ?? new List<string>();
                var secondaryItems = labels.GetSecondaryItems(row) ?? new List<string>();
                var differenceItems = GetDifferenceItems(row, report.Mode);
                var detailId = BuildDetailId(primaryKey, i);

                html.AppendLine("        <details id=\"" + detailId + "\">");
                html.AppendLine("            <summary>" + Encode(primaryKey) + "</summary>");
                html.AppendLine("            <div class=\"detail-body\">");
                html.AppendLine("                <div class=\"detail-grid\">");

                AppendDetailBox(html, labels.PrimaryItemsLabel, primaryItems.Count, primaryItems, null);
                AppendDetailBox(
                    html,
                    labels.SecondaryItemsLabel,
                    secondaryItems.Count,
                    secondaryItems,
                    string.IsNullOrWhiteSpace(secondaryKey) ? labels.EmptyItemsText : null);
                AppendDetailBox(html, labels.DifferenceLabel, differenceItems.Count, differenceItems, null);

                html.AppendLine("                </div>");
                html.AppendLine("            </div>");
                html.AppendLine("        </details>");
            }

            html.AppendLine("    </div>");
        }

        private static void AppendDetailBox(
            StringBuilder html,
            string title,
            int count,
            List<string> items,
            string emptyOverride)
        {
            html.AppendLine("                    <div class=\"detail-box\">");
            html.AppendLine("                        <h3>" + Encode(title) + " " + count + "</h3>");
            html.AppendLine("                        <div class=\"list-wrap\">");

            if (!string.IsNullOrWhiteSpace(emptyOverride))
            {
                html.AppendLine("                            <div class=\"muted\">" + Encode(emptyOverride) + ".</div>");
            }
            else
            {
                html.AppendLine(RenderList(items));
            }

            html.AppendLine("                        </div>");
            html.AppendLine("                    </div>");
        }

        private static string RenderList(List<string> items)
        {
            var values = (items ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (values.Count == 0)
            {
                return "                            <div class=\"muted\">없음.</div>";
            }

            var html = new StringBuilder();
            html.AppendLine("                            <ul>");
            foreach (var value in values)
            {
                html.AppendLine("                                <li>" + Encode(value) + "</li>");
            }

            html.Append("                            </ul>");
            return html.ToString();
        }

        private static List<string> GetDifferenceItems(ComparisonRow row, ComparisonMode mode)
        {
            if (mode == ComparisonMode.Equal)
            {
                // 동등 비교는 부족 항목과 추가 항목을 함께 보여줍니다.
                return row.MissingItems
                    .Concat(row.ExtraItems)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return row.MissingItems ?? new List<string>();
        }

        private static string RenderText(string value, string emptyText)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<span class=\"muted\">" + Encode(emptyText) + "</span>";
            }

            return Encode(value);
        }

        private static string RenderResult(bool isMatch)
        {
            return isMatch
                ? "<span class=\"result-true\">참</span>"
                : "<span class=\"result-false\">거짓</span>";
        }

        private static string BuildDetailId(string primaryKey, int index)
        {
            var source = string.IsNullOrWhiteSpace(primaryKey) ? "detail" : primaryKey;
            var buffer = new StringBuilder();

            foreach (var ch in source)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    buffer.Append('-');
                }
            }

            return "detail-" + buffer.ToString().Trim('-') + "-" + index;
        }

        private static string Encode(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static ComparisonRenderLabels ResolveLabels(ComparisonMode mode, string sourceLabel, string targetLabel)
        {
            sourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "소스" : sourceLabel;
            targetLabel = string.IsNullOrWhiteSpace(targetLabel) ? "타겟" : targetLabel;

            // 같은 데이터라도 기준 축이 달라지면 표 의미가 달라집니다.
            switch (mode)
            {
                case ComparisonMode.SourceInTarget:
                    return new ComparisonRenderLabels
                    {
                        SourceLabel = sourceLabel,
                        TargetLabel = targetLabel,
                        SourceKeyLabel = sourceLabel + " 키",
                        TargetKeyLabel = targetLabel + " 키",
                        PrimaryKeyLabel = sourceLabel + " 키",
                        SecondaryKeyLabel = "대응 " + targetLabel + " 키",
                        PrimaryItemsLabel = sourceLabel + " 항목",
                        SecondaryItemsLabel = targetLabel + " 항목",
                        DifferenceLabel = "부족 항목",
                        EmptyKeyText = "대응 " + targetLabel + " 없음",
                        EmptyItemsText = "비교 가능한 " + targetLabel + " 없음",
                        GetPrimaryKey = row => row.SourceKey,
                        GetSecondaryKey = row => row.TargetKey,
                        GetPrimaryItems = row => row.SourceItems,
                        GetSecondaryItems = row => row.TargetItems,
                    };
                case ComparisonMode.TargetInSource:
                    return new ComparisonRenderLabels
                    {
                        SourceLabel = sourceLabel,
                        TargetLabel = targetLabel,
                        SourceKeyLabel = sourceLabel + " 키",
                        TargetKeyLabel = targetLabel + " 키",
                        PrimaryKeyLabel = targetLabel + " 키",
                        SecondaryKeyLabel = "대응 " + sourceLabel + " 키",
                        PrimaryItemsLabel = targetLabel + " 항목",
                        SecondaryItemsLabel = sourceLabel + " 항목",
                        DifferenceLabel = "부족 항목",
                        EmptyKeyText = "대응 " + sourceLabel + " 없음",
                        EmptyItemsText = "비교 가능한 " + sourceLabel + " 없음",
                        GetPrimaryKey = row => row.TargetKey,
                        GetSecondaryKey = row => row.SourceKey,
                        GetPrimaryItems = row => row.TargetItems,
                        GetSecondaryItems = row => row.SourceItems,
                    };
                case ComparisonMode.Equal:
                default:
                    return new ComparisonRenderLabels
                    {
                        SourceLabel = sourceLabel,
                        TargetLabel = targetLabel,
                        SourceKeyLabel = sourceLabel + " 키",
                        TargetKeyLabel = targetLabel + " 키",
                        PrimaryKeyLabel = sourceLabel + " 키",
                        SecondaryKeyLabel = "대응 " + targetLabel + " 키",
                        PrimaryItemsLabel = sourceLabel + " 항목",
                        SecondaryItemsLabel = targetLabel + " 항목",
                        DifferenceLabel = "차이 항목",
                        EmptyKeyText = "대응 " + targetLabel + " 없음",
                        EmptyItemsText = "비교 가능한 " + targetLabel + " 없음",
                        GetPrimaryKey = row => row.SourceKey,
                        GetSecondaryKey = row => row.TargetKey,
                        GetPrimaryItems = row => row.SourceItems,
                        GetSecondaryItems = row => row.TargetItems,
                    };
            }
        }

        private class ComparisonRenderLabels
        {
            public string SourceLabel { get; set; }
            public string TargetLabel { get; set; }
            public string SourceKeyLabel { get; set; }
            public string TargetKeyLabel { get; set; }
            public string PrimaryKeyLabel { get; set; }
            public string SecondaryKeyLabel { get; set; }
            public string PrimaryItemsLabel { get; set; }
            public string SecondaryItemsLabel { get; set; }
            public string DifferenceLabel { get; set; }
            public string EmptyKeyText { get; set; }
            public string EmptyItemsText { get; set; }
            public Func<ComparisonRow, string> GetPrimaryKey { get; set; }
            public Func<ComparisonRow, string> GetSecondaryKey { get; set; }
            public Func<ComparisonRow, List<string>> GetPrimaryItems { get; set; }
            public Func<ComparisonRow, List<string>> GetSecondaryItems { get; set; }
        }
    }
}
