using DevExpress.Mvvm;
using OSTES.Common;
using OSTES.Data;
using OSTES.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OSTES.ViewModel.SIngleSim
{
    /// <summary>
    /// Playback-path-only draft for SingleSimUserAnalyChartViewModel.
    /// This file is intended as a refactor reference and is kept outside the main build.
    /// </summary>
    public partial class SingleSimUserAnalyChartViewModel : ViewModelBase
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

        private void IndexSeriesBuffer(ChartSeriesBuffer buffer)
        {
            if (buffer == null || buffer.ChartPoint3DList == null)
            {
                return;
            }

            var timeMap = new Dictionary<double, List<ChartPoint3D>>();

            foreach (var point in buffer.ChartPoint3DList)
            {
                if (!timeMap.TryGetValue(point.Time, out var pointsAtTime))
                {
                    pointsAtTime = new List<ChartPoint3D>();
                    timeMap[point.Time] = pointsAtTime;
                }

                pointsAtTime.Add(point);
            }

            _playbackTimeIndex[buffer.ChartPrimaryKey.SeriesIndex] = timeMap;
        }

        private List<ChartPoint3D> GetPointsAtTime(int seriesIndex, double time)
        {
            if (!_playbackTimeIndex.TryGetValue(seriesIndex, out var timeMap))
            {
                return s_emptyChartPoint3DList;
            }

            return timeMap.TryGetValue(time, out var points) ? points : s_emptyChartPoint3DList;
        }

        private async Task ReceiveChartSeriesBuffer_Optimized(ChartSeriesBuffer buffer)
        {
            DynamicUnSubscribeEvents();
            DynamicSubscribeEvents();

            _seriesPointBuffer[buffer.ChartPrimaryKey.SeriesIndex] = buffer.ChartPoint3DList;
            IndexSeriesBuffer(buffer);

            await Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                ReceiveChartSeriesBuffer_Render(buffer);
            }));
        }

        private void ReceiveSimulationTimeChangedMessage_Optimized(SimulationTimeChangedMessage message)
        {
            if (_chartQueryRequests == null)
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

            Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    var filteredSeriesPoints = BuildPlaybackFrame(receiveTime, token);
                    if (filteredSeriesPoints.Count == 0)
                    {
                        return;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        token.ThrowIfCancellationRequested();

                        if (_lastRenderedPlaybackTime.HasValue && _lastRenderedPlaybackTime.Value == receiveTime)
                        {
                            return;
                        }

                        RenderPlaybackFrame(filteredSeriesPoints);
                        _lastRenderedPlaybackTime = receiveTime;
                    }, DispatcherPriority.Render, token);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }

        private Dictionary<int, List<ChartPoint3D>> BuildPlaybackFrame(double receiveTime, CancellationToken token)
        {
            var filteredSeriesPointBuffer = new Dictionary<int, List<ChartPoint3D>>();

            foreach (var seriesIndex in _seriesPointBuffer.Keys)
            {
                token.ThrowIfCancellationRequested();
                filteredSeriesPointBuffer[seriesIndex] = GetPointsAtTime(seriesIndex, receiveTime);
            }

            return filteredSeriesPointBuffer;
        }

        private void RenderPlaybackFrame(Dictionary<int, List<ChartPoint3D>> filteredSeriesPoints)
        {
            if (!(ChartControl is IChartPlayback chartPlayback))
            {
                return;
            }

            foreach (var kvp in filteredSeriesPoints)
            {
                var seriesIndex = kvp.Key;
                var points = kvp.Value;

                if (_is3DViewer)
                {
                    var seriesPoints3D = ChartPointMapper.ToSeriesPoints3D(points);
                    chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(seriesIndex, null, seriesPoints3D));
                }
                else
                {
                    var seriesPoints2D = ChartPointMapper.ToSeriesPoints2D(points);
                    chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(seriesIndex, seriesPoints2D, null));
                }
            }
        }

        private void ReceiveChartSeriesBuffer_Render(ChartSeriesBuffer buffer)
        {
            if (_is3DViewer)
            {
                var seriesPoints = ChartPointMapper.ToSeriesPoints3D(buffer.ChartPoint3DList);

                if (_isAutuFit)
                {
                    _charRangeData = new ChartRangeData
                    {
                        X_Min = seriesPoints.Min(p => p.X),
                        X_Max = seriesPoints.Max(p => p.X),
                        Y_Min = seriesPoints.Min(p => p.Y),
                        Y_Max = seriesPoints.Max(p => p.Y),
                        Z_Min = seriesPoints.Min(p => p.Z),
                        Z_Max = seriesPoints.Max(p => p.Z),
                    };
                }
                else
                {
                    _charRangeData.SwapYZ();
                }

                ChartControl.BeginUpdate();

                var addSeriesPointDTO = new AddSeriesPointDTO(buffer.ChartPrimaryKey.SeriesIndex, null, seriesPoints);
                ChartControl.AddPoints(addSeriesPointDTO);
                ChartControl.SetRange(_charRangeData, buffer.ChartPrimaryKey.SeriesIndex);

                ChartControl.EndUpdate();
                return;
            }

            var seriesPoints2D = ChartPointMapper.ToSeriesPoints2D(buffer.ChartPoint3DList);

            if (_isAutuFit)
            {
                _charRangeData = new ChartRangeData
                {
                    X_Min = seriesPoints2D.Min(p => p.X),
                    X_Max = seriesPoints2D.Max(p => p.X),
                    Y_Min = seriesPoints2D.Min(p => p.Y),
                    Y_Max = seriesPoints2D.Max(p => p.Y),
                };
            }

            var dto = new AddSeriesPointDTO(buffer.ChartPrimaryKey.SeriesIndex, seriesPoints2D, null);

            ChartControl.BeginUpdate();
            ChartControl.AddPoints(dto);

            switch (_chartViewType)
            {
                case ChartViewType.TwoD_Time_Multi:
                    ChartControl.SetRange(_charRangeData, buffer.ChartPrimaryKey.SeriesIndex);
                    break;

                case ChartViewType.TwoD_Time:
                case ChartViewType.TwoD_AxisSelectable:
                case ChartViewType.TwoD_LL:
                case ChartViewType.TwoD_XY:
                    ChartControl.SetRange(_charRangeData, 0);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_chartViewType));
            }

            ChartControl.EndUpdate();
        }

        private static readonly List<ChartPoint3D> s_emptyChartPoint3DList = new List<ChartPoint3D>(0);
    }
}
