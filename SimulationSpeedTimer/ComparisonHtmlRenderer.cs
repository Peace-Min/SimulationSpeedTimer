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
            var labels = ResolveLabels(report.Mode);
            var html = new StringBuilder();

            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html lang=\"ko\">");
            html.AppendLine("<head>");
            html.AppendLine("    <meta charset=\"utf-8\" />");
            html.AppendLine("    <title>" + Encode(report.Title) + "</title>");
            html.AppendLine("    <style>");
            html.AppendLine("        body { margin: 24px; font-family: \"Segoe UI\", sans-serif; color: #222222; background: #ffffff; }");
            html.AppendLine("        h1, h2 { margin-bottom: 8px; }");
            html.AppendLine("        .meta { margin-bottom: 18px; line-height: 1.7; }");
            html.AppendLine("        table { width: 100%; border-collapse: collapse; margin-bottom: 24px; table-layout: fixed; }");
            html.AppendLine("        th, td { border: 1px solid #cccccc; padding: 9px 10px; text-align: left; vertical-align: top; font-size: 14px; }");
            html.AppendLine("        th { background: #f3f3f3; font-weight: 700; }");
            html.AppendLine("        .item-list { margin: 0; padding-left: 18px; column-count: 2; column-gap: 18px; }");
            html.AppendLine("        .item-list li { font-size: 12px; line-height: 1.5; word-break: break-word; break-inside: avoid; }");
            html.AppendLine("        .single-column { column-count: 1; }");
            html.AppendLine("        .cell-block { min-height: 64px; }");
            html.AppendLine("        .cell-note { margin-top: 8px; font-size: 12px; color: #666666; }");
            html.AppendLine("        .muted { color: #666666; }");
            html.AppendLine("        code { background: #f3f3f3; padding: 1px 4px; border-radius: 4px; }");
            html.AppendLine("    </style>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");
            html.AppendLine("    <h1>" + Encode(report.Title) + "</h1>");
            html.AppendLine("    <div class=\"meta\">");
            html.AppendLine("        <div><strong>\uC0DD\uC131 \uC2DC\uAC01:</strong> " + Encode(report.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")) + "</div>");
            html.AppendLine("        <div><strong>\uBE44\uAD50 \uBC29\uC2DD:</strong> <code>" + Encode(report.Mode.ToString()) + "</code></div>");
            html.AppendLine("        <div><strong>\uCD5C\uC885 \uACB0\uACFC:</strong> " + (report.IsMatch ? "\uCC38" : "\uAC70\uC9D3") + "</div>");
            html.AppendLine("    </div>");

            AppendDatasetTable(html, "1. \uC18C\uC2A4 \uD45C", "\uC18C\uC2A4 \uD0A4", "\uC18C\uC2A4 \uD56D\uBAA9", report.SourceDataset);
            AppendDatasetTable(html, "2. \uD0C0\uAC9F \uD45C", "\uD0C0\uAC9F \uD0A4", "\uD0C0\uAC9F \uD56D\uBAA9", report.TargetDataset);

            html.AppendLine("    <h2>3. \uBE44\uAD50 \uD45C(" + labels.BasisLabel + ")</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 14%;\">" + labels.PrimaryKeyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 14%;\">" + labels.SecondaryKeyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 22%;\">" + labels.PrimaryItemsLabel + "</th>");
            html.AppendLine("                <th style=\"width: 22%;\">" + labels.SecondaryItemsLabel + "</th>");
            html.AppendLine("                <th style=\"width: 16%;\">" + labels.DifferenceLabel + "</th>");
            html.AppendLine("                <th style=\"width: 12%;\">\uBE44\uAD50 \uACB0\uACFC</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            foreach (var row in report.Rows)
            {
                html.AppendLine("            <tr>");
                html.AppendLine("                <td>" + Encode(labels.GetPrimaryKey(row) ?? string.Empty) + "</td>");
                html.AppendLine("                <td>" + RenderText(labels.GetSecondaryKey(row), labels.EmptyKeyText) + "</td>");
                html.AppendLine("                <td>" + RenderItems(labels.GetPrimaryItems(row), true) + "</td>");
                html.AppendLine("                <td>" + RenderSecondaryItems(labels.GetSecondaryKey(row), labels.GetSecondaryItems(row), labels.EmptyItemsText) + "</td>");
                html.AppendLine("                <td>" + RenderDifferenceItems(row, report.Mode) + "</td>");
                html.AppendLine("                <td>" + (row.IsMatch ? "\uCC38" : "\uAC70\uC9D3") + "</td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");
            html.AppendLine("</body>");
            html.AppendLine("</html>");

            return html.ToString();
        }

        private static void AppendDatasetTable(
            StringBuilder html,
            string title,
            string keyLabel,
            string itemsLabel,
            ComparisonDataset dataset)
        {
            html.AppendLine("    <h2>" + title + "</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 24%;\">" + keyLabel + "</th>");
            html.AppendLine("                <th style=\"width: 76%;\">" + itemsLabel + "</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            foreach (var pair in dataset.Objects)
            {
                html.AppendLine("            <tr>");
                html.AppendLine("                <td>" + Encode(pair.Key) + "</td>");
                html.AppendLine("                <td>" + RenderItems(pair.Value, true) + "<div class=\"cell-note\">\uD56D\uBAA9 \uC218 " + pair.Value.Count + "</div></td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");
        }

        private static string RenderSecondaryItems(string key, List<string> items, string emptyText)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "<span class=\"muted\">" + Encode(emptyText) + "</span>";
            }

            return RenderItems(items, true);
        }

        private static string RenderDifferenceItems(ComparisonRow row, ComparisonMode mode)
        {
            if (mode == ComparisonMode.Equal)
            {
                // 동등 비교는 부족 항목과 추가 항목을 함께 보여줍니다.
                var differences = row.MissingItems
                    .Concat(row.ExtraItems)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return RenderItems(differences, false);
            }

            return RenderItems(row.MissingItems, false);
        }

        private static string RenderItems(List<string> items, bool useTwoColumns)
        {
            var values = (items ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (values.Count == 0)
            {
                return "<span class=\"muted\">\uC5C6\uC74C</span>";
            }

            var cssClass = useTwoColumns ? "item-list cell-block" : "item-list single-column";
            var html = new StringBuilder();
            html.Append("<ul class=\"" + cssClass + "\">");
            foreach (var value in values)
            {
                html.Append("<li>" + Encode(value) + "</li>");
            }

            html.Append("</ul>");
            return html.ToString();
        }

        private static string RenderText(string value, string emptyText)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<span class=\"muted\">" + Encode(emptyText) + "</span>";
            }

            return Encode(value);
        }

        private static string Encode(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static ComparisonRenderLabels ResolveLabels(ComparisonMode mode)
        {
            // 같은 데이터라도 기준 축이 달라지면 표 의미가 달라집니다.
            switch (mode)
            {
                case ComparisonMode.SourceInTarget:
                    return new ComparisonRenderLabels
                    {
                        BasisLabel = "\uC18C\uC2A4 \uAE30\uC900",
                        PrimaryKeyLabel = "\uC18C\uC2A4 \uD0A4",
                        SecondaryKeyLabel = "\uB300\uC751 \uD0C0\uAC9F \uD0A4",
                        PrimaryItemsLabel = "\uC18C\uC2A4 \uD56D\uBAA9",
                        SecondaryItemsLabel = "\uD0C0\uAC9F \uD56D\uBAA9",
                        DifferenceLabel = "\uBD80\uC871 \uD56D\uBAA9",
                        EmptyKeyText = "\uB300\uC751 \uD0C0\uAC9F \uC5C6\uC74C",
                        EmptyItemsText = "\uBE44\uAD50 \uAC00\uB2A5\uD55C \uD0C0\uAC9F \uC5C6\uC74C",
                        GetPrimaryKey = row => row.SourceKey,
                        GetSecondaryKey = row => row.TargetKey,
                        GetPrimaryItems = row => row.SourceItems,
                        GetSecondaryItems = row => row.TargetItems,
                    };
                case ComparisonMode.TargetInSource:
                    return new ComparisonRenderLabels
                    {
                        BasisLabel = "\uD0C0\uAC9F \uAE30\uC900",
                        PrimaryKeyLabel = "\uD0C0\uAC9F \uD0A4",
                        SecondaryKeyLabel = "\uB300\uC751 \uC18C\uC2A4 \uD0A4",
                        PrimaryItemsLabel = "\uD0C0\uAC9F \uD56D\uBAA9",
                        SecondaryItemsLabel = "\uC18C\uC2A4 \uD56D\uBAA9",
                        DifferenceLabel = "\uBD80\uC871 \uD56D\uBAA9",
                        EmptyKeyText = "\uB300\uC751 \uC18C\uC2A4 \uC5C6\uC74C",
                        EmptyItemsText = "\uBE44\uAD50 \uAC00\uB2A5\uD55C \uC18C\uC2A4 \uC5C6\uC74C",
                        GetPrimaryKey = row => row.TargetKey,
                        GetSecondaryKey = row => row.SourceKey,
                        GetPrimaryItems = row => row.TargetItems,
                        GetSecondaryItems = row => row.SourceItems,
                    };
                case ComparisonMode.Equal:
                default:
                    return new ComparisonRenderLabels
                    {
                        BasisLabel = "\uB3D9\uB4F1 \uBE44\uAD50",
                        PrimaryKeyLabel = "\uC18C\uC2A4 \uD0A4",
                        SecondaryKeyLabel = "\uB300\uC751 \uD0C0\uAC9F \uD0A4",
                        PrimaryItemsLabel = "\uC18C\uC2A4 \uD56D\uBAA9",
                        SecondaryItemsLabel = "\uD0C0\uAC9F \uD56D\uBAA9",
                        DifferenceLabel = "\uCC28\uC774 \uD56D\uBAA9",
                        EmptyKeyText = "\uB300\uC751 \uD0C0\uAC9F \uC5C6\uC74C",
                        EmptyItemsText = "\uBE44\uAD50 \uAC00\uB2A5\uD55C \uD0C0\uAC9F \uC5C6\uC74C",
                        GetPrimaryKey = row => row.SourceKey,
                        GetSecondaryKey = row => row.TargetKey,
                        GetPrimaryItems = row => row.SourceItems,
                        GetSecondaryItems = row => row.TargetItems,
                    };
            }
        }

        private class ComparisonRenderLabels
        {
            public string BasisLabel { get; set; }
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
