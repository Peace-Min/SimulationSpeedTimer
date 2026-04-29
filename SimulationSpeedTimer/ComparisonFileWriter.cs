using System;
using System.IO;
using System.Text;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 보고서 파일을 기록합니다.
    /// </summary>
    public interface IComparisonFileWriter
    {
        void WriteAllText(string filePath, string content);
    }

    public class ComparisonFileWriter : IComparisonFileWriter
    {
        public void WriteAllText(string filePath, string content)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath is required.", nameof(filePath));

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                // 저장 폴더가 없으면 먼저 만듭니다.
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, content ?? string.Empty, Encoding.UTF8);
        }
    }
}
