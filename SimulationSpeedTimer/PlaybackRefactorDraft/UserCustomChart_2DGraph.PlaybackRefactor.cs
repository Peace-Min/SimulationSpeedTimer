using Arction.Wpf.Charting.EventMarkers;
using Arction.Wpf.Charting.SeriesXY;
using OSTES.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace OSTES.Chart
{
    /// <summary>
    /// Playback cursor handling draft for UserCustomChart_2DGraph.
    /// This file is intended as a refactor reference and is kept outside the main build.
    /// </summary>
    public partial class UserCustomChart_2DGraph
    {
        private readonly Dictionary<int, List<SeriesEventMarker>> _playbackMarkerPool
            = new Dictionary<int, List<SeriesEventMarker>>();

        private readonly Dictionary<int, PlaybackCursorSnapshot[]> _playbackSnapshots
            = new Dictionary<int, PlaybackCursorSnapshot[]>();

        private struct PlaybackCursorSnapshot
        {
            public double X;
            public double Y;
            public bool Visible;
        }

        public void SetPlaybackCursor()
        {
            _chart.BeginUpdate();

            try
            {
                _playbackMarkerPool.Clear();
                _playbackSnapshots.Clear();

                for (int seriesIndex = 0; seriesIndex < _chart.ViewXY.FreeformPointLineSeries.Count; seriesIndex++)
                {
                    var series = _chart.ViewXY.FreeformPointLineSeries[seriesIndex];
                    var existingMarkers = series.SeriesEventMarkers
                        .Where(m => (m.Tag is MarkerType type) && type == MarkerType.PlaybackCursor)
                        .ToList();

                    if (existingMarkers.Count == 0)
                    {
                        var marker = CreatePlaybackMarker(series.LineStyle.Color);
                        series.SeriesEventMarkers.Add(marker);
                        existingMarkers.Add(marker);
                    }
                    else
                    {
                        foreach (var marker in existingMarkers)
                        {
                            ApplyPlaybackMarkerStyle(marker, series.LineStyle.Color);
                            marker.Visible = false;
                        }
                    }

                    _playbackMarkerPool[seriesIndex] = existingMarkers;
                    _playbackSnapshots[seriesIndex] = CreateHiddenSnapshots(existingMarkers.Count);
                }
            }
            finally
            {
                _chart.EndUpdate();
            }
        }

        public void UpdatePlaybackCursorPosition(AddSeriesPointDTO addSeriesPointDTO)
        {
            if (addSeriesPointDTO == null)
            {
                return;
            }

            var series = _chart.ViewXY.FreeformPointLineSeries.ElementAtOrDefault(addSeriesPointDTO.SeriesIndex);
            if (series == null)
            {
                return;
            }

            var points = addSeriesPointDTO.SeriesPoint2D ?? Array.Empty<SeriesPoint>();
            EnsurePlaybackMarkerPool(addSeriesPointDTO.SeriesIndex, points.Length, series.LineStyle.Color);

            var markers = _playbackMarkerPool[addSeriesPointDTO.SeriesIndex];
            var snapshots = _playbackSnapshots[addSeriesPointDTO.SeriesIndex];

            bool hasVisualChange = false;

            for (int i = 0; i < markers.Count; i++)
            {
                bool shouldBeVisible = i < points.Length;
                if (!shouldBeVisible)
                {
                    if (snapshots[i].Visible)
                    {
                        hasVisualChange = true;
                    }

                    continue;
                }

                var point = points[i];
                if (!snapshots[i].Visible || snapshots[i].X != point.X || snapshots[i].Y != point.Y)
                {
                    hasVisualChange = true;
                }
            }

            if (!hasVisualChange)
            {
                return;
            }

            _chart.BeginUpdate();

            try
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    bool shouldBeVisible = i < points.Length;
                    var marker = markers[i];

                    if (!shouldBeVisible)
                    {
                        marker.Visible = false;
                        snapshots[i] = new PlaybackCursorSnapshot { Visible = false };
                        continue;
                    }

                    var point = points[i];
                    marker.XValue = point.X;
                    marker.YValue = point.Y;
                    marker.Visible = true;

                    snapshots[i] = new PlaybackCursorSnapshot
                    {
                        X = point.X,
                        Y = point.Y,
                        Visible = true
                    };
                }
            }
            finally
            {
                _chart.EndUpdate();
            }
        }

        private void EnsurePlaybackMarkerPool(int seriesIndex, int requiredCount, Color color)
        {
            if (!_playbackMarkerPool.TryGetValue(seriesIndex, out var markers))
            {
                markers = new List<SeriesEventMarker>();
                _playbackMarkerPool[seriesIndex] = markers;
            }

            while (markers.Count < requiredCount)
            {
                var marker = CreatePlaybackMarker(color);
                _chart.ViewXY.FreeformPointLineSeries[seriesIndex].SeriesEventMarkers.Add(marker);
                markers.Add(marker);
            }

            if (!_playbackSnapshots.TryGetValue(seriesIndex, out var snapshots))
            {
                snapshots = Array.Empty<PlaybackCursorSnapshot>();
            }

            if (snapshots.Length >= markers.Count)
            {
                return;
            }

            var expanded = new PlaybackCursorSnapshot[markers.Count];
            Array.Copy(snapshots, expanded, snapshots.Length);
            _playbackSnapshots[seriesIndex] = expanded;
        }

        private SeriesEventMarker CreatePlaybackMarker(Color color)
        {
            var marker = CreateEventMarker(MarkerType.PlaybackCursor);
            ApplyPlaybackMarkerStyle(marker, color);
            marker.Visible = false;
            return marker;
        }

        private void ApplyPlaybackMarkerStyle(SeriesEventMarker marker, Color color)
        {
            marker.Symbol.BorderColor = color;
            marker.Symbol.Color1 = color;
            marker.Symbol.Color2 = color;
            marker.Symbol.Color3 = color;
        }

        private PlaybackCursorSnapshot[] CreateHiddenSnapshots(int count)
        {
            var snapshots = new PlaybackCursorSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                snapshots[i] = new PlaybackCursorSnapshot { Visible = false };
            }

            return snapshots;
        }

        private void ResetPlaybackCursorState()
        {
            _playbackMarkerPool.Clear();
            _playbackSnapshots.Clear();
        }
    }
}
