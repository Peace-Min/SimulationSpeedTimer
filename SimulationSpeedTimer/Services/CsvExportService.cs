using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimulationSpeedTimer.Models;

namespace SimulationSpeedTimer.Services
{
    public class CsvExportService
    {
        /// <summary>
        /// Exports data to a CSV file.
        /// Supports both static types and dynamic ExpandoObjects.
        /// </summary>
        /// <typeparam name="T">Type of objects to export (can be ExpandoObject).</typeparam>
        /// <param name="data">Data collection.</param>
        /// <param name="filePath">Target file path.</param>
        /// <param name="headers">Optional. Explicit column headers (ordered keys). If null, inferred from Type or first Dictionary item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task ExportAsync<T>(IEnumerable<T> data, string filePath, IEnumerable<string> headers = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            await Task.Run(async () =>
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                cancellationToken.ThrowIfCancellationRequested();

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    // 1. Resolve Headers
                    string headerRow = UniversalCsvModel.GetHeaderRow<T>(headers);
                    await writer.WriteLineAsync(headerRow);

                    // 2. Write Data
                    foreach (var item in data)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Pass explicit headers to ensure we pluck values in the correct order for Dictionaries
                        string row = UniversalCsvModel.GetDataRow(item, headers);
                        await writer.WriteLineAsync(row);
                    }
                }
            }, cancellationToken);
        }
    }
}
