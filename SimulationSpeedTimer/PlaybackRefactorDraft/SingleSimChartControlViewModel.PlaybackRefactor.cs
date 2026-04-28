using DevExpress.Mvvm;
using OSTES.Data;
using OSTES.Interface;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OSTES.ViewModel.SIngleSim
{
    /// <summary>
    /// Playback-path-only draft for SingleSimChartControlViewModel.
    /// This file is intended as a refactor reference and is kept outside the main build.
    /// </summary>
    public partial class SingleSimChartControlViewModel : ViewModelBase
    {
        private readonly Dictionary<int, Dictionary<double, List<ChartPoint3D>>> _playbackTimeIndex
            = new Dictionary<int, Dictionary<double, List<ChartPoint3D>>>();

        private double? _lastRequestedPlaybackTime;
        private double? _lastRenderedPlaybackTime;

        private void ResetPlaybackBufferState()
        {
            _playbackTimeIndex.Clear();
            _lastRequestedPlaybackTime = null;
            _lastRenderedPlaybackTime = null;
        }

        private void BuildPlaybackIndex()
        {
            _playbackTimeIndex.Clear();

            foreach (var kvp in _seriesPointBuffer)
            {
                var timeMap = new Dictionary<double, List<ChartPoint3D>>();

                foreach (var point in kvp.Value)
                {
                    if (!timeMap.TryGetValue(point.Time, out var pointsAtTime))
                    {
                        pointsAtTime = new List<ChartPoint3D>();
                        timeMap[point.Time] = pointsAtTime;
                    }

                    pointsAtTime.Add(point);
                }

                _playbackTimeIndex[kvp.Key] = timeMap;
            }
        }

        private void ReceiveSimulationTimeChangedMessage_Optimized(SimulationTimeChangedMessage message)
        {
            if (_spatialSimulationModel == null || _currentConfig == null)
            {
                return;
            }

            double receiveTime = message.NewTime;
            if (_lastRequestedPlaybackTime.HasValue && _lastRequestedPlaybackTime.Value == receiveTime)
            {
                return;
            }

            _lastRequestedPlaybackTime = receiveTime;

            _renderCts?.Cancel();
            _renderCts?.Dispose();
            _renderCts = new CancellationTokenSource();

            var token = _renderCts.Token;
            var is3DViewer = _graphChartType == GraphChartType.ThreeD;

            Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    var filteredSeriesPoints = new Dictionary<int, List<ChartPoint3D>>();

                    foreach (var kvp in _playbackTimeIndex)
                    {
                        token.ThrowIfCancellationRequested();
                        filteredSeriesPoints[kvp.Key] = kvp.Value.TryGetValue(receiveTime, out var points)
                            ? points
                            : s_emptyChartPoint3DList;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        token.ThrowIfCancellationRequested();

                        if (_lastRenderedPlaybackTime.HasValue && _lastRenderedPlaybackTime.Value == receiveTime)
                        {
                            return;
                        }

                        if (!(ChartControl is IChartPlayback chartPlayback))
                        {
                            return;
                        }

                        foreach (var kvp in filteredSeriesPoints)
                        {
                            if (is3DViewer)
                            {
                                var seriesPoints3D = ChartPointMapper.ToSeriesPoints3D(kvp.Value);
                                chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(kvp.Key, null, seriesPoints3D));
                            }
                            else
                            {
                                var seriesPoints2D = ChartPointMapper.ToSeriesPoints2D(kvp.Value);
                                chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(kvp.Key, seriesPoints2D, null));
                            }
                        }

                        _lastRenderedPlaybackTime = receiveTime;
                    }, DispatcherPriority.Render, token);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }

        private static readonly List<ChartPoint3D> s_emptyChartPoint3DList = new List<ChartPoint3D>(0);
    }
}
