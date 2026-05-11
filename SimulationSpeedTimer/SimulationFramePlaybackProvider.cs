using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 로드된 시뮬레이션 프레임에서 재생 커서에 필요한 시리즈별 값을 조회한다.
    /// </summary>
    public sealed class SimulationFramePlaybackProvider
    {
        private readonly IReadOnlyDictionary<double, SimulationFrame> _framesByTime;

        public SimulationFramePlaybackProvider(IReadOnlyDictionary<double, SimulationFrame> framesByTime)
        {
            _framesByTime = framesByTime ?? throw new ArgumentNullException(nameof(framesByTime));
        }

        /// <summary>
        /// 특정 시간의 프레임을 차트 커서 갱신 객체로 변환한다.
        /// </summary>
        public List<TCursorUpdate> BuildCursorFrame<TCursorUpdate>(
            double time,
            IReadOnlyList<DatabaseQueryConfig> configs,
            bool is3DViewer,
            Func<int, double, double, TCursorUpdate> create2D,
            Func<int, double, double, double, TCursorUpdate> create3D,
            Func<int, TCursorUpdate> createEmpty)
        {
            if (create2D == null) throw new ArgumentNullException(nameof(create2D));
            if (create3D == null) throw new ArgumentNullException(nameof(create3D));
            if (createEmpty == null) throw new ArgumentNullException(nameof(createEmpty));

            var cursorUpdates = new List<TCursorUpdate>(configs?.Count ?? 0);

            if (configs == null || configs.Count == 0)
            {
                return cursorUpdates;
            }

            if (!_framesByTime.TryGetValue(time, out var frame))
            {
                AddEmptyCursorUpdates(configs, cursorUpdates, createEmpty);
                return cursorUpdates;
            }

            for (var seriesIndex = 0; seriesIndex < configs.Count; seriesIndex++)
            {
                var config = configs[seriesIndex];
                if (config == null)
                {
                    continue;
                }

                var x = config.IsXAxisTime
                    ? frame.Time
                    : GetValue(frame, config.XColumn.ObjectName, config.XColumn.AttributeName);
                var y = GetValue(frame, config.YColumn.ObjectName, config.YColumn.AttributeName);

                if (!x.HasValue || !y.HasValue)
                {
                    cursorUpdates.Add(createEmpty(seriesIndex));
                    continue;
                }

                if (is3DViewer)
                {
                    var z = GetValue(frame, config.ZColumn.ObjectName, config.ZColumn.AttributeName);
                    cursorUpdates.Add(z.HasValue
                        ? create3D(seriesIndex, x.Value, y.Value, z.Value)
                        : createEmpty(seriesIndex));
                    continue;
                }

                cursorUpdates.Add(create2D(seriesIndex, x.Value, y.Value));
            }

            return cursorUpdates;
        }

        private static void AddEmptyCursorUpdates<TCursorUpdate>(
            IReadOnlyList<DatabaseQueryConfig> configs,
            ICollection<TCursorUpdate> cursorUpdates,
            Func<int, TCursorUpdate> createEmpty)
        {
            for (var seriesIndex = 0; seriesIndex < configs.Count; seriesIndex++)
            {
                var config = configs[seriesIndex];
                if (config == null)
                {
                    continue;
                }

                cursorUpdates.Add(createEmpty(seriesIndex));
            }
        }

        private double? GetValue(SimulationFrame frame, string tableName, string columnName)
        {
            if (frame == null) return null;
            if (string.IsNullOrEmpty(tableName)) return null;
            if (string.IsNullOrEmpty(columnName)) return null;

            var table = frame.GetTable(tableName);
            if (table == null) return null;

            var value = table[columnName];
            if (value == null) return null;

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
    }
}
