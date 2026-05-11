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
        /// 특정 시간의 프레임을 시리즈별 커서 데이터로 변환한다.
        /// </summary>
        public List<SimulationFrameCursorPoint> BuildCursorFrame(
            double time,
            IReadOnlyList<DatabaseQueryConfig> configs,
            bool is3DViewer)
        {
            var cursorPoints = new List<SimulationFrameCursorPoint>(configs?.Count ?? 0);

            if (configs == null || configs.Count == 0)
            {
                return cursorPoints;
            }

            if (!_framesByTime.TryGetValue(time, out var frame))
            {
                AddEmptyCursorPoints(time, configs, cursorPoints);
                return cursorPoints;
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
                    cursorPoints.Add(SimulationFrameCursorPoint.Empty(seriesIndex, time));
                    continue;
                }

                if (is3DViewer)
                {
                    var z = GetValue(frame, config.ZColumn.ObjectName, config.ZColumn.AttributeName);
                    cursorPoints.Add(z.HasValue
                        ? SimulationFrameCursorPoint.From3D(seriesIndex, frame.Time, x.Value, y.Value, z.Value)
                        : SimulationFrameCursorPoint.Empty(seriesIndex, time));
                    continue;
                }

                cursorPoints.Add(SimulationFrameCursorPoint.From2D(seriesIndex, frame.Time, x.Value, y.Value));
            }

            return cursorPoints;
        }

        private static void AddEmptyCursorPoints(
            double time,
            IReadOnlyList<DatabaseQueryConfig> configs,
            ICollection<SimulationFrameCursorPoint> cursorPoints)
        {
            for (var seriesIndex = 0; seriesIndex < configs.Count; seriesIndex++)
            {
                var config = configs[seriesIndex];
                if (config == null)
                {
                    continue;
                }

                cursorPoints.Add(SimulationFrameCursorPoint.Empty(seriesIndex, time));
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

    /// <summary>
    /// 차트 라이브러리 DTO로 변환하기 전 단계의 재생 커서 데이터.
    /// </summary>
    public sealed class SimulationFrameCursorPoint
    {
        private SimulationFrameCursorPoint(int seriesIndex, double time, double x, double y, double? z, bool hasValue)
        {
            SeriesIndex = seriesIndex;
            Time = time;
            X = x;
            Y = y;
            Z = z;
            HasValue = hasValue;
        }

        public int SeriesIndex { get; }

        public double Time { get; }

        public double X { get; }

        public double Y { get; }

        public double? Z { get; }

        public bool HasValue { get; }

        public static SimulationFrameCursorPoint Empty(int seriesIndex, double time)
        {
            return new SimulationFrameCursorPoint(seriesIndex, time, 0d, 0d, null, false);
        }

        public static SimulationFrameCursorPoint From2D(int seriesIndex, double time, double x, double y)
        {
            return new SimulationFrameCursorPoint(seriesIndex, time, x, y, null, true);
        }

        public static SimulationFrameCursorPoint From3D(int seriesIndex, double time, double x, double y, double z)
        {
            return new SimulationFrameCursorPoint(seriesIndex, time, x, y, z, true);
        }
    }
}
