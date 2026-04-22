using Arction.Wpf.Charting.Series3D;
using OSTES.Data;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OSTES.Chart
{
    /// <summary>
    /// Playback draft for UserCustomChart_3DGraph.
    /// This file is intended as a refactor reference and is kept outside the main build.
    /// </summary>
    public partial class UserCustomChart_3DGraph
    {
        private readonly Dictionary<int, PointLineSeries3D> _playbackCursorSeriesMap
            = new Dictionary<int, PointLineSeries3D>();

        private readonly Dictionary<int, PlaybackSeriesSnapshot> _playbackCursorSnapshots
            = new Dictionary<int, PlaybackSeriesSnapshot>();

        private struct PlaybackSeriesSnapshot
        {
            public bool Visible;
            public int PointCount;
            public SeriesPoint3D[] Points;
        }

        public void SetPlaybackCursor()
        {
            _chart.BeginUpdate();

            try
            {
                _playbackCursorSeriesMap.Clear();
                _playbackCursorSnapshots.Clear();

                var chartDataSeries = ChartDataPointLineSeries3D.ToList();

                for (int seriesIndex = 0; seriesIndex < chartDataSeries.Count; seriesIndex++)
                {
                    var dataSeries = chartDataSeries[seriesIndex];

                    PointLineSeries3D playbackCursorSeries = null;

                    if (dataSeries.Tag is PointLineSeries3D taggedSeries)
                    {
                        playbackCursorSeries = taggedSeries;
                    }

                    if (playbackCursorSeries == null)
                    {
                        playbackCursorSeries = CreateBoxMarkerSeries(AnnotationType.PlaybackCursor);
                        playbackCursorSeries.PointStyle.Shape3D = PointShape3D.Torus;
                        playbackCursorSeries.PointStyle.Size3D.SetValues(4, 4, 4);
                        playbackCursorSeries.Material.DiffuseColor = dataSeries.Material.DiffuseColor;
                        playbackCursorSeries.Material.EmissiveColor = dataSeries.Material.EmissiveColor;
                        playbackCursorSeries.Visible = false;
                        playbackCursorSeries.Points = new[] { CreateHiddenPoint() };

                        _chart.View3D.PointLineSeries3D.Add(playbackCursorSeries);
                        dataSeries.Tag = playbackCursorSeries;
                    }
                    else
                    {
                        playbackCursorSeries.Visible = false;
                    }

                    _playbackCursorSeriesMap[seriesIndex] = playbackCursorSeries;
                    _playbackCursorSnapshots[seriesIndex] = new PlaybackSeriesSnapshot
                    {
                        Visible = false,
                        PointCount = 1,
                        Points = new[] { CreateHiddenPoint() }
                    };
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

            var dataSeries = ChartDataPointLineSeries3D.ElementAtOrDefault(addSeriesPointDTO.SeriesIndex);
            if (dataSeries == null)
            {
                return;
            }

            if (!_playbackCursorSeriesMap.TryGetValue(addSeriesPointDTO.SeriesIndex, out var playbackCursorSeries))
            {
                return;
            }

            var nextPoints = addSeriesPointDTO.SeriesPoint3D ?? Array.Empty<SeriesPoint3D>();
            _playbackCursorSnapshots.TryGetValue(addSeriesPointDTO.SeriesIndex, out var snapshot);

            bool shouldBeVisible = nextPoints.Length > 0;
            if (!HasPlaybackSeriesChanged(snapshot, nextPoints, shouldBeVisible))
            {
                return;
            }

            _chart.BeginUpdate();

            try
            {
                if (!shouldBeVisible)
                {
                    playbackCursorSeries.Visible = false;
                    playbackCursorSeries.Points = new[] { CreateHiddenPoint() };

                    _playbackCursorSnapshots[addSeriesPointDTO.SeriesIndex] = new PlaybackSeriesSnapshot
                    {
                        Visible = false,
                        PointCount = 1,
                        Points = new[] { CreateHiddenPoint() }
                    };

                    return;
                }

                playbackCursorSeries.Visible = true;
                playbackCursorSeries.Points = nextPoints;

                _playbackCursorSnapshots[addSeriesPointDTO.SeriesIndex] = new PlaybackSeriesSnapshot
                {
                    Visible = true,
                    PointCount = nextPoints.Length,
                    Points = ClonePoints(nextPoints)
                };
            }
            finally
            {
                _chart.EndUpdate();
            }
        }

        private bool HasPlaybackSeriesChanged(
            PlaybackSeriesSnapshot snapshot,
            SeriesPoint3D[] nextPoints,
            bool shouldBeVisible)
        {
            if (snapshot.Visible != shouldBeVisible)
            {
                return true;
            }

            if (!shouldBeVisible)
            {
                return false;
            }

            if (snapshot.PointCount != nextPoints.Length)
            {
                return true;
            }

            if (snapshot.Points == null || snapshot.Points.Length != nextPoints.Length)
            {
                return true;
            }

            for (int i = 0; i < nextPoints.Length; i++)
            {
                var current = snapshot.Points[i];
                var next = nextPoints[i];

                if (current.X != next.X || current.Y != next.Y || current.Z != next.Z)
                {
                    return true;
                }
            }

            return false;
        }

        private SeriesPoint3D[] ClonePoints(SeriesPoint3D[] source)
        {
            var clone = new SeriesPoint3D[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }

        private SeriesPoint3D CreateHiddenPoint()
        {
            return new SeriesPoint3D(double.NaN, double.NaN, double.NaN);
        }

        private void ResetPlaybackCursorState()
        {
            _playbackCursorSeriesMap.Clear();
            _playbackCursorSnapshots.Clear();
        }
    }
}
