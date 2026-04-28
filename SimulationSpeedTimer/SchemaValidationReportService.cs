using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SimulationSpeedTimer
{
    public class SchemaValidationReportService
    {
        public bool TryWrite(SchemaValidationReportSnapshot snapshot, out string reportPath)
        {
            reportPath = null;

            try
            {
                reportPath = Write(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SchemaValidationReport] Failed to save report: {ex.Message}");
                return false;
            }
        }

        public string Write(SchemaValidationReportSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var reportDirectory = Path.Combine(baseDirectory, "Logs", "SchemaValidation");
            Directory.CreateDirectory(reportDirectory);

            var resultText = snapshot.IsSuccess ? "PASS" : "FAIL";
            var fileName = $"SchemaValidation_{snapshot.CreatedAt:yyyyMMdd_HHmmss_fff}_{resultText}.html";
            var reportPath = Path.Combine(reportDirectory, fileName);

            File.WriteAllText(reportPath, BuildHtml(snapshot), Encoding.UTF8);
            Console.WriteLine($"[SchemaValidationReport] Saved: {reportPath}");

            return reportPath;
        }

        private static string BuildHtml(SchemaValidationReportSnapshot snapshot)
        {
            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html lang=\"ko\">");
            html.AppendLine("<head>");
            html.AppendLine("    <meta charset=\"utf-8\" />");
            html.AppendLine("    <title>Schema Validation Report</title>");
            html.AppendLine("    <style>");
            html.AppendLine("        body { margin: 20px; font-family: \"Segoe UI\", sans-serif; color: #222222; background: #ffffff; }");
            html.AppendLine("        h1, h2 { margin-bottom: 8px; }");
            html.AppendLine("        .meta { margin-bottom: 20px; line-height: 1.7; }");
            html.AppendLine("        table { width: 100%; border-collapse: collapse; margin-bottom: 24px; table-layout: fixed; }");
            html.AppendLine("        th, td { border: 1px solid #cccccc; padding: 8px 10px; text-align: left; vertical-align: top; font-size: 14px; }");
            html.AppendLine("        th { background: #f3f3f3; }");
            html.AppendLine("        .pass { color: #157f3b; font-weight: 700; }");
            html.AppendLine("        .fail { color: #c62828; font-weight: 700; }");
            html.AppendLine("        code { background: #f3f3f3; padding: 1px 4px; border-radius: 4px; }");
            html.AppendLine("        .attributes { display: flex; flex-wrap: wrap; gap: 6px; }");
            html.AppendLine("        .attr { display: inline-block; max-width: 100%; padding: 3px 8px; border: 1px solid #d7d7d7; border-radius: 999px; background: #f7f7f7; font-size: 12px; line-height: 1.4; word-break: break-word; }");
            html.AppendLine("        .attr-missing { color: #c62828; border-color: #efb0b0; background: #fff2f2; }");
            html.AppendLine("        .muted { color: #666666; }");
            html.AppendLine("    </style>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");
            html.AppendLine("    <h1>Schema Validation Report</h1>");
            html.AppendLine("    <div class=\"meta\">");
            html.AppendLine($"        <div><strong>Time:</strong> {Encode(snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))}</div>");
            html.AppendLine($"        <div><strong>DB:</strong> {Encode(snapshot.DbPath ?? string.Empty)}</div>");
            html.AppendLine($"        <div><strong>Result:</strong> <span class=\"{(snapshot.IsSuccess ? "pass" : "fail")}\">{(snapshot.IsSuccess ? "PASS" : "FAIL")}</span></div>");
            html.AppendLine("    </div>");

            html.AppendLine("    <h2>Configured Object Comparison</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 12%;\">ObjectName</th>");
            html.AppendLine("                <th style=\"width: 14%;\">TableName</th>");
            html.AppendLine("                <th style=\"width: 26%;\">Required Attributes</th>");
            html.AppendLine("                <th style=\"width: 26%;\">DB Attributes</th>");
            html.AppendLine("                <th style=\"width: 14%;\">Missing Attributes</th>");
            html.AppendLine("                <th style=\"width: 8%;\">Result</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            foreach (var entry in snapshot.ConfiguredObjects)
            {
                html.AppendLine("            <tr>");
                html.AppendLine($"                <td>{Encode(entry.ObjectName)}</td>");
                html.AppendLine($"                <td>{FormatTableName(entry.TableName)}</td>");
                html.AppendLine($"                <td>{RenderAttributes(entry.RequiredAttributes, false)}</td>");
                html.AppendLine($"                <td>{RenderDbAttributes(entry.TableName, entry.DbAttributes)}</td>");
                html.AppendLine($"                <td>{RenderAttributes(entry.MissingAttributes, true)}</td>");
                html.AppendLine($"                <td><span class=\"{(entry.IsSuccess ? "pass" : "fail")}\">{(entry.IsSuccess ? "PASS" : "FAIL")}</span></td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");

            html.AppendLine("    <h2>DB Raw Objects</h2>");
            html.AppendLine("    <table>");
            html.AppendLine("        <thead>");
            html.AppendLine("            <tr>");
            html.AppendLine("                <th style=\"width: 18%;\">DB ObjectName</th>");
            html.AppendLine("                <th style=\"width: 18%;\">TableName</th>");
            html.AppendLine("                <th style=\"width: 64%;\">Attributes</th>");
            html.AppendLine("            </tr>");
            html.AppendLine("        </thead>");
            html.AppendLine("        <tbody>");

            foreach (var entry in snapshot.DbObjects)
            {
                html.AppendLine("            <tr>");
                html.AppendLine($"                <td>{Encode(entry.ObjectName)}</td>");
                html.AppendLine($"                <td>{FormatTableName(entry.TableName)}</td>");
                html.AppendLine($"                <td>{RenderAttributes(entry.Attributes, false)}</td>");
                html.AppendLine("            </tr>");
            }

            html.AppendLine("        </tbody>");
            html.AppendLine("    </table>");
            html.AppendLine("</body>");
            html.AppendLine("</html>");

            return html.ToString();
        }

        private static string RenderDbAttributes(string tableName, System.Collections.Generic.IEnumerable<string> attributes)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return "<span class=\"muted\">Object mapping not found</span>";
            }

            return RenderAttributes(attributes, false);
        }

        private static string RenderAttributes(System.Collections.Generic.IEnumerable<string> attributes, bool isMissing)
        {
            var values = (attributes ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (values.Count == 0)
            {
                return "<span class=\"muted\">-</span>";
            }

            var html = new StringBuilder();
            html.Append("<div class=\"attributes\">");
            foreach (var value in values)
            {
                var cssClass = isMissing ? "attr attr-missing" : "attr";
                html.Append($"<span class=\"{cssClass}\">{Encode(value)}</span>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string FormatTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return "<span class=\"muted\">-</span>";
            }

            return $"<code>{Encode(tableName)}</code>";
        }

        private static string Encode(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
