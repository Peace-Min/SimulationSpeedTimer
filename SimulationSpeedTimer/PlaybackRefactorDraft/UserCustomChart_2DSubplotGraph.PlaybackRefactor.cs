using Arction.Wpf.Charting.Annotations;
using Arction.Wpf.Charting.EventMarkers;
using Arction.Wpf.Charting.SeriesXY;
using Arction.Wpf.Charting.Views.ViewXY;
using OSTES.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace OSTES.Chart
{
    /// <summary>
    /// Playback and tracking draft for UserCustomChart_2DSubplotGraph.
    /// This file is intended as a refactor reference and is kept outside the main build.
    /// </summary>
    public partial class UserCustomChart_2DSubplotGraph
    {
        private readonly Dictionary<int, List<SeriesEventMarker>> _playbackMarkerPool
            = new Dictionary<int, List<SeriesEventMarker>>();

        private readonly Dictionary<int, PlaybackCursorSnapshot[]> _playbackSnapshots
            = new Dictionary<int, PlaybackCursorSnapshot[]>();

        private readonly Dictionary<int, SeriesEventMarker> _trackingMarkerMap
            = new Dictionary<int, SeriesEventMarker>();

        private readonly Dictionary<int, TrackingCursorSnapshot> _trackingSnapshots
            = new Dictionary<int, TrackingCursorSnapshot>();

        private readonly Dictionary<int, AnnotationXY> _trackingAnnotationMap
            = new Dictionary<int, AnnotationXY>();

        private struct PlaybackCursorSnapshot
        {
            public double X;
            public double Y;
            public bool Visible;
        }

        private struct TrackingCursorSnapshot
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
                _trackingMarkerMap.Clear();
                _trackingSnapshots.Clear();
                _trackingAnnotationMap.Clear();

                for (int seriesIndex = 0; seriesIndex < _chart.ViewXY.FreeformPointLineSeries.Count; seriesIndex++)
                {
                    var series = _chart.ViewXY.FreeformPointLineSeries[seriesIndex];

                    var playbackMarkers = series.SeriesEventMarkers
                        .Where(m => (m.Tag is MarkerType type) && type == MarkerType.PlaybackCursor)
                        .ToList();

                    if (playbackMarkers.Count == 0)
                    {
                        var marker = CreatePlaybackMarker(series.LineStyle.Color);
                        series.SeriesEventMarkers.Add(marker);
                        playbackMarkers.Add(marker);
                    }
                    else
                    {
                        foreach (var marker in playbackMarkers)
                        {
                            ApplyPlaybackMarkerStyle(marker, series.LineStyle.Color);
                            marker.Visible = false;
                        }
                    }

                    _playbackMarkerPool[seriesIndex] = playbackMarkers;
                    _playbackSnapshots[seriesIndex] = CreateHiddenPlaybackSnapshots(playbackMarkers.Count);

                    var trackingMarker = series.SeriesEventMarkers
                        .FirstOrDefault(m => (m.Tag is MarkerType type) && type == MarkerType.CursorTooltip);

                    if (trackingMarker == null)
                    {
                        trackingMarker = CreateTrackingMarker();
                        series.SeriesEventMarkers.Add(trackingMarker);
                    }
                    else
                    {
                        trackingMarker.Visible = false;
                    }

                    _trackingMarkerMap[seriesIndex] = trackingMarker;
                    _trackingSnapshots[seriesIndex] = new TrackingCursorSnapshot { Visible = false };

                    var trackingAnnotation = _chart.ViewXY.Annotations.ElementAtOrDefault(seriesIndex);
                    if (trackingAnnotation != null)
                    {
                        trackingAnnotation.Visible = false;
                        _trackingAnnotationMap[seriesIndex] = trackingAnnotation;
                    }
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

        private void Cursor_PositionChanged_Optimized(object sender, PositionChangedEventArgs e)
        {
            e.CancelRendering = true;

            if (!(sender is LineSeriesCursor cursor))
            {
                return;
            }

            bool hasVisualChange = false;
            var pending = new List<TrackingCursorUpdate>();

            for (int seriesIndex = 0; seriesIndex < _chart.ViewXY.FreeformPointLineSeries.Count; seriesIndex++)
            {
                var series = _chart.ViewXY.FreeformPointLineSeries.ElementAtOrDefault(seriesIndex);
                if (series == null)
                {
                    continue;
                }

                if ((series.PointCount == 0) || (!series.Visible))
                {
                    if (_trackingSnapshots.TryGetValue(seriesIndex, out var hiddenState) && hiddenState.Visible)
                    {
                        hasVisualChange = true;
                    }

                    pending.Add(TrackingCursorUpdate.Hidden(seriesIndex));
                    continue;
                }

                var targetPoint = LightningChartMathUtils.FindNearestPointByX_Binary(
                    series.Points,
                    series.PointCount,
                    cursor.ValueAtXAxis);

                _trackingSnapshots.TryGetValue(seriesIndex, out var snapshot);

                bool changed = !snapshot.Visible
                    || snapshot.X != targetPoint.X
                    || snapshot.Y != targetPoint.Y;

                if (changed)
                {
                    hasVisualChange = true;
                }

                pending.Add(new TrackingCursorUpdate
                {
                    SeriesIndex = seriesIndex,
                    X = targetPoint.X,
                    Y = targetPoint.Y,
                    Visible = true
                });
            }

            if (!hasVisualChange)
            {
                return;
            }

            _chart.BeginUpdate();

            try
            {
                _trackingCurosr.Visible = true;

                foreach (var update in pending)
                {
                    if (!_trackingMarkerMap.TryGetValue(update.SeriesIndex, out var marker))
                    {
                        continue;
                    }

                    if (!_trackingAnnotationMap.TryGetValue(update.SeriesIndex, out var annotation))
                    {
                        continue;
                    }

                    if (!update.Visible)
                    {
                        marker.Visible = false;
                        annotation.Visible = false;
                        _trackingSnapshots[update.SeriesIndex] = new TrackingCursorSnapshot { Visible = false };
                        continue;
                    }

                    marker.XValue = update.X;
                    marker.YValue = update.Y;
                    marker.Visible = true;

                    annotation.AssignYAxisIndex = update.SeriesIndex;
                    SetAnnotation(annotation, update.X, update.Y);
                    annotation.Text = $"{GetAxisDipslayValue(update.X, true)}\n{GetAxisDipslayValue(update.Y, false)}";
                    annotation.Visible = true;

                    _trackingSnapshots[update.SeriesIndex] = new TrackingCursorSnapshot
                    {
                        X = update.X,
                        Y = update.Y,
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

        private SeriesEventMarker CreateTrackingMarker()
        {
            var marker = CreateEventMarker(MarkerType.CursorTooltip);
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

        private PlaybackCursorSnapshot[] CreateHiddenPlaybackSnapshots(int count)
        {
            var snapshots = new PlaybackCursorSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                snapshots[i] = new PlaybackCursorSnapshot { Visible = false };
            }

            return snapshots;
        }

        private void ResetPlaybackAndTrackingState()
        {
            _playbackMarkerPool.Clear();
            _playbackSnapshots.Clear();
            _trackingMarkerMap.Clear();
            _trackingSnapshots.Clear();
            _trackingAnnotationMap.Clear();
        }

        private struct TrackingCursorUpdate
        {
            public int SeriesIndex;
            public double X;
            public double Y;
            public bool Visible;

            public static TrackingCursorUpdate Hidden(int seriesIndex)
            {
                return new TrackingCursorUpdate
                {
                    SeriesIndex = seriesIndex,
                    Visible = false
                };
            }
        }
    }
}
