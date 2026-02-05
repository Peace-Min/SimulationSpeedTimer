using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SimulationSpeedTimer.Models
{
    /// <summary>
    /// A universal helper to extracting CSV-friendly data from any object type T.
    /// Supports both Static POCOs (via Reflection) and Dynamic Objects (via IDictionary).
    /// </summary>
    public static class UniversalCsvModel
    {
        // Cache property info to avoid expensive Reflection on every row
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<CsvPropertyMetadata>> _cache
            = new ConcurrentDictionary<Type, IReadOnlyList<CsvPropertyMetadata>>();

        /// <summary>
        /// Gets the CSV header row.
        /// If explicitHeaders is provided, uses that.
        /// Otherwise, tries to infer from Type T (Reflection).
        /// </summary>
        public static string GetHeaderRow<T>(IEnumerable<string> explicitHeaders = null, string separator = ",")
        {
            IEnumerable<string> headers;

            if (explicitHeaders != null && explicitHeaders.Any())
            {
                headers = explicitHeaders;
            }
            else
            {
                // Fallback to Reflection for static types
                var properties = GetCachedProperties(typeof(T));
                headers = properties.Select(p => p.HeaderName);
            }

            return string.Join(separator, headers.Select(h => EscapeCsvValue(h, separator)));
        }

        /// <summary>
        /// Gets a CSV data row for a specific item instance.
        /// If explicitHeaders is provided, it attempts to fetch values by those keys (for Dictionary/Expando).
        /// Otherwise, uses Reflection.
        /// </summary>
        public static string GetDataRow<T>(T item, IEnumerable<string> explicitHeaders = null, string separator = ",")
        {
            if (item == null) return string.Empty;

            IEnumerable<string> values;

            // Case 1: Dynamic / Dictionary (ExpandoObject)
            if (item is IDictionary<string, object> dict)
            {
                // If headers are provided, pick values in that order
                var keysToUse = explicitHeaders ?? dict.Keys;

                values = keysToUse.Select(key =>
                {
                    if (dict.TryGetValue(key, out object val))
                    {
                        return EscapeCsvValue(FormatValue(val), separator);
                    }
                    return ""; // Key not found
                });
            }
            // Case 2: Static POCO
            else
            {
                var properties = GetCachedProperties(typeof(T));

                values = properties.Select(p =>
                {
                    try
                    {
                        object val = p.PropertyInfo.GetValue(item);
                        return EscapeCsvValue(FormatValue(val), separator);
                    }
                    catch
                    {
                        return "";
                    }
                });
            }

            return string.Join(separator, values);
        }

        private static IReadOnlyList<CsvPropertyMetadata> GetCachedProperties(Type type)
        {
            return _cache.GetOrAdd(type, t =>
            {
                // Select public instance properties that are readable
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead)
                             .ToList();

                var metaList = new List<CsvPropertyMetadata>();

                foreach (var p in props)
                {
                    // Check for [Browsable(false)]
                    var browsableAttr = p.GetCustomAttribute<BrowsableAttribute>();
                    if (browsableAttr != null && !browsableAttr.Browsable)
                        continue;

                    // Check for [DisplayName]
                    var displayAttr = p.GetCustomAttribute<DisplayNameAttribute>();
                    string header = displayAttr != null ? displayAttr.DisplayName : p.Name;

                    metaList.Add(new CsvPropertyMetadata
                    {
                        PropertyInfo = p,
                        HeaderName = header
                    });
                }

                return metaList;
            });
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "";

            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            if (value is bool b)
            {
                return b ? "Yes" : "No";
            }

            return value.ToString();
        }

        private static string EscapeCsvValue(string value, string separator)
        {
            if (string.IsNullOrEmpty(value)) return "";

            if (value.Contains(separator) || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
            {
                value = value.Replace("\"", "\"\"");
                return $"\"{value}\"";
            }

            return value;
        }

        private class CsvPropertyMetadata
        {
            public PropertyInfo PropertyInfo { get; set; }
            public string HeaderName { get; set; }
        }
    }
}
