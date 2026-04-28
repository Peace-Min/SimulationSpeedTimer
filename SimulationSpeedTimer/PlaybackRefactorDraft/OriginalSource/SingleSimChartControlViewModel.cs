using Arction.Wpf.Charting;

using Arction.Wpf.Charting.Series3D;

using Arction.Wpf.Charting.SeriesXY;

using DevExpress.CodeParser;

using DevExpress.Mvvm;

using DevExpress.PivotGrid.PivotTable;

using DevExpress.Xpf.Charts;

using OSTES.Chart;

using OSTES.Common;

using OSTES.Data;

using OSTES.Dialog;

using OSTES.Interface;

using OSTES.Model;

using OSTES.Service;

using OSTES.Utils;

using OSTES.View.Demo;

using OSTES.ViewModel.Dialog;

using System;

using System.Collections.Generic;

using System.ComponentModel;

using System.IO;

using System.Linq;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Media;

using System.Windows.Media.Animation;

using System.Windows.Threading;

using SeriesPoint = Arction.Wpf.Charting.SeriesPoint;



namespace OSTES.ViewModel.SIngleSim

{

    /// <summary>

    /// 사전 분석 - 그래프 분석 차트 뷰모델.

    /// </summary>

    public class SingleSimChartControlViewModel : ViewModelBase

    {

        private readonly OSTES.Dialog.IDialogService dialogService = new DialogService();

        private readonly GraphChartType _graphChartType;



        private CancellationTokenSource _renderCts;

        private SpatialSimulationModel _spatialSimulationModel;

        private ChartDefinition<SpatialData> _currentConfig;



        /// <summary>

        /// 시리즈 데이터를 저장하는 딕셔너리.

        /// </summary>

        private Dictionary<int, List<ChartPoint3D>> _seriesPointBuffer;



        /// <summary>

        /// 사전에 정의가 필요한 그래프 차트별 아군, 적군 정보.

        /// </summary>

        private Dictionary<GraphChartType, ChartDefinition<SpatialData>> _chartConfigs;



        public SingleSimChartControlViewModel(GraphChartType graphChartType)

        {

            _graphChartType = graphChartType;



            Initialize();

        }



        public IUserCustomChart ChartControl { get => GetValue<IUserCustomChart>(); set => SetValue(value); }



        private void Initialize()

        {

            UpdateConfig();

            CreateChartControl();

            SubscribeEvents();

        }



        private void CreateChartControl()

        {

            var chartThemaIndex = ChartThemeComboItem.GetThemaIndex(AppConst.DEFAULT_CHART_THEME);

            switch (_graphChartType)

            {

                case GraphChartType.XY:

                    ChartControl = new UserCustomChart_2DGraph(ChartViewType.TwoD_LL, _currentConfig.AxisXTitle, _currentConfig.AxisYTitle);

                    break;



                case GraphChartType.Yaw:

                case GraphChartType.Pitch:

                case GraphChartType.Roll:

                case GraphChartType.Alt:

                case GraphChartType.Lon_N:

                case GraphChartType.Lat_E:

                case GraphChartType.Alt_D:

                    ChartControl = new UserCustomChart_2DGraph(ChartViewType.TwoD_Time, _currentConfig.AxisXTitle, _currentConfig.AxisYTitle);

                    break;



                case GraphChartType.ThreeD:

                    ChartControl = new UserCustomChart_3DGraph(ChartViewType.ThreeD_LLA, _currentConfig.AxisXTitle, _currentConfig.AxisYTitle, _currentConfig.AxisZTitle);

                    break;



                default:

                    throw new ArgumentOutOfRangeException(nameof(_graphChartType));

            }



            var allyLineChartSeriesDTO = new LineChartSeriesDTO(0, chartThemaIndex, _currentConfig.AllyLegendBoxTitle, Colors.Blue, LineSeriesType.LineOnly);

            var enemyLineChartSeriesDTO = new LineChartSeriesDTO(1, chartThemaIndex, _currentConfig.EnemyLegendBoxTitle, Colors.Red, LineSeriesType.LineOnly);



            ChartControl.AddLineChartSeries(allyLineChartSeriesDTO);

            ChartControl.AddLineChartSeries(enemyLineChartSeriesDTO);



            if (ChartControl is IChartPlayback chartPlayback)

            {

                chartPlayback.SetPlaybackCursor();

            }



            ChartControl.ChartThemeSelection(chartThemaIndex);

            ChartControl.ChartLegendTheme(chartThemaIndex);

        }



        private void UpdateConfig()

        {

            _chartConfigs = new Dictionary<GraphChartType, ChartDefinition<SpatialData>>()

            {

                [GraphChartType.XY] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 미사일",

                    EnemyLegendBoxTitle = "표적",

                    AxisXTitle = "경도(deg)",

                    AxisYTitle = "위도(deg)",

                    XSelector = d => d.LonPos,

                    YSelector = d => d.LatPos,

                },

                [GraphChartType.Yaw] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Yaw",

                    EnemyLegendBoxTitle = "표적 Yaw",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Yaw(deg)",

                    XSelector = d => d.STime,

                    YSelector = d => d.Yaw,

                },

                [GraphChartType.Pitch] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Pitch",

                    EnemyLegendBoxTitle = "표적 Pitch",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Pitch(deg)",

                    XSelector = d => d.STime,

                    YSelector = d => d.Pitch,

                },

                [GraphChartType.Roll] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Roll",

                    EnemyLegendBoxTitle = "표적 Roll",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Roll(deg)",

                    XSelector = d => d.STime,

                    YSelector = d => d.Roll,

                },

                [GraphChartType.ThreeD] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 미사일",

                    EnemyLegendBoxTitle = "표적",

                    AxisXTitle = "경도(deg)",

                    AxisYTitle = "위도(deg)",

                    AxisZTitle = "고도(m)",

                    XSelector = d => d.LonPos,

                    YSelector = d => d.Alt,

                    ZSelector = d => d.Lat,

                },



                [GraphChartType.Alt] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Alt",

                    EnemyLegendBoxTitle = "표적 Alt",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "고도(m)",

                    XSelector = d => d.STime,

                    YSelector = d => d.AltPos,

                },

                [GraphChartType.Lon_N] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Lon 속도",

                    EnemyLegendBoxTitle = "표적 Lon 속도",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Lon 속도(deg/s)",

                    XSelector = d => d.STime,

                    YSelector = d => d.LonVel,

                },

                [GraphChartType.Lat_E] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Lat 속도",

                    EnemyLegendBoxTitle = "표적 Lat 속도",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Lat 속도(deg/s)",

                    XSelector = d => d.STime,

                    YSelector = d => d.LatVel,

                },

                [GraphChartType.Alt_D] = new ChartDefinition<SpatialData>

                {

                    AllyName = SingleSimAppConst.MISSIL_COMMPONENT,

                    EnemyName = SingleSimAppConst.Target_COMMPONENT,

                    AllyLegendBoxTitle = "아군 Alt 속도",

                    EnemyLegendBoxTitle = "표적 Alt 속도",

                    AxisXTitle = "시간(sec)",

                    AxisYTitle = "Alt 속도(m/s)",

                    XSelector = d => d.STime,

                    YSelector = d => d.AltVel,

                },

            };



            if (!_chartConfigs.TryGetValue(_graphChartType, out var config)) { return; }

            _currentConfig = config;

        }



        public void ClearChart()

        {

            _spatialSimulationModel = null;

            _currentConfig = null;

            _seriesPointBuffer = null;



            ChartControl.ClearChart();

        }



        public async Task InitializeSpatialDbSourceAsync(SpatialSimulationModel source)

        {

            _seriesPointBuffer = new Dictionary<int, List<ChartPoint3D>>();

            _spatialSimulationModel = source;

            UpdateConfig();



            // 현재 타입에 맞는 config 정보.

            if (!_chartConfigs.TryGetValue(_graphChartType, out var config)) { return; }

            _currentConfig = config;



            //CreateChartControl();



            var ally2D = new List<ChartPoint3D>();

            var ally3D = new List<ChartPoint3D>();

            var enermy2D = new List<ChartPoint3D>();

            var enermy3D = new List<ChartPoint3D>();

            var is3DViewer = _graphChartType == GraphChartType.ThreeD;



            // 데이터 추출.

            await Task.Run(() =>

            {

                foreach (var dto in source.SpatialData)

                {

                    if (dto.PlayerName == config.AllyName)

                    {

                        if (is3DViewer)

                        {

                            ally3D.Add(new ChartPoint3D(config.XSelector(dto), config.YSelector(dto), config.ZSelector(dto), dto.STime));

                        }

                        else

                        {

                            ally2D.Add(new ChartPoint3D(config.XSelector(dto), config.YSelector(dto), default, dto.STime));

                        }

                    }

                    else if (dto.PlayerName == config.EnemyName)

                    {

                        if (is3DViewer)

                        {

                            enermy3D.Add(new ChartPoint3D(config.XSelector(dto), config.YSelector(dto), config.ZSelector(dto), dto.STime));

                        }

                        else

                        {

                            enermy2D.Add(new ChartPoint3D(config.XSelector(dto), config.YSelector(dto), default, dto.STime));

                        }

                    }

                }

            });



            // 렌더링 수행.

            await Application.Current.Dispatcher.InvokeAsync(() =>

            {

                try

                {

                    ChartControl.BeginUpdate();

                    // 2D/3D 분기처리.

                    if (is3DViewer)

                    {

                        // 시리즈 buffer 저장.

                        _seriesPointBuffer.Add(0, ally3D);

                        _seriesPointBuffer.Add(1, enermy3D);



                        if (ally3D.Any())

                        {

                            var allyRangeData = new ChartRangeData()

                            {

                                X_Min = ally3D.Min(p => p.X),

                                X_Max = ally3D.Max(p => p.X),

                                Y_Min = ally3D.Min(p => p.Y),

                                Y_Max = ally3D.Max(p => p.Y), // 보정값이 10보다 작은 경우 10으로 설정.

                                Z_Min = ally3D.Min(p => p.Z),

                                Z_Max = ally3D.Max(p => p.Z), // 보정값이 10보다 작은 경우 10으로 설정.

                            };

                            var allySeriesPoints = ChartPointMapper.ToSeriesPoints3D(ally3D);

                            var allySeriesPointDTO = new AddSeriesPointDTO(0, null, allySeriesPoints);



                            ChartControl.AddPoints(allySeriesPointDTO);

                            ChartControl.SetRange(allyRangeData, 0);

                        }



                        if (enermy3D.Any())

                        {

                            var enermyRangeData = new ChartRangeData()

                            {

                                X_Min = enermy3D.Min(p => p.X),

                                X_Max = enermy3D.Max(p => p.X),

                                Y_Min = enermy3D.Min(p => p.Y),

                                Y_Max = enermy3D.Max(p => p.Y), // 보정값이 10보다 작은 경우 10으로 설정.

                                Z_Min = enermy3D.Min(p => p.Z),

                                Z_Max = enermy3D.Max(p => p.Z), // 보정값이 10보다 작은 경우 10으로 설정.

                            };



                            var enermySeriesPoints = ChartPointMapper.ToSeriesPoints3D(enermy3D);

                            var enermySeriesPointDTO = new AddSeriesPointDTO(1, null, enermySeriesPoints);



                            ChartControl.AddPoints(enermySeriesPointDTO);

                            ChartControl.SetRange(enermyRangeData, 1);

                        }

                    }

                    else

                    {

                        // 시리즈 buffer 저장.

                        _seriesPointBuffer.Add(0, ally2D);

                        _seriesPointBuffer.Add(1, enermy2D);



                        if (ally2D.Any())

                        {

                            var allyRangeData = new ChartRangeData()

                            {

                                X_Min = ally2D.Min(p => p.X),

                                X_Max = ally2D.Max(p => p.X),

                                Y_Min = ally2D.Min(p => p.Y),

                                Y_Max = ally2D.Max(p => p.Y), // 보정값이 10보다 작은 경우 10으로 설정.

                            };

                            var allySeriesPoints = ChartPointMapper.ToSeriesPoints2D(ally2D);

                            var allySeriesPointDTO = new AddSeriesPointDTO(0, allySeriesPoints, null);



                            ChartControl.AddPoints(allySeriesPointDTO);

                            ChartControl.SetRange(allyRangeData, 0);

                        }



                        if (enermy2D.Any())

                        {

                            var enermyRangeData = new ChartRangeData()

                            {

                                X_Min = enermy2D.Min(p => p.X),

                                X_Max = enermy2D.Max(p => p.X),

                                Y_Min = enermy2D.Min(p => p.Y),

                                Y_Max = enermy2D.Max(p => p.Y), // 보정값이 10보다 작은 경우 10으로 설정.

                            };

                            var enermySeriesPoints = ChartPointMapper.ToSeriesPoints2D(enermy2D);

                            var enermySeriesPointDTO = new AddSeriesPointDTO(1, enermySeriesPoints, null);



                            ChartControl.AddPoints(enermySeriesPointDTO);

                            ChartControl.SetRange(enermyRangeData, 1);

                        }

                    }

                }

                finally

                {

                    ChartControl.EndUpdate();

                }

            }, DispatcherPriority.Background);

        }



        #region EventHandlers & Subscriptions (이벤트 핸들러, 메시지 구독자, 통신 응답 처리 관련)

        private void SubscribeEvents()

        {

            Messenger.Default.Register<ChartThemeComboItem>(this, ReceiveUpdateChartTheme);

            Messenger.Default.Register<SimulationTimeChangedMessage>(this, ReceiveSimulationTimeChangedMessage);

        }



        private void ReceiveUpdateChartTheme(ChartThemeComboItem param)

        {

            var themeIndex = ChartThemeComboItem.GetThemaIndex(param.Name);

            var is3DViewer = _graphChartType == GraphChartType.ThreeD;



            ChartControl.ChartThemeSelection(themeIndex);

            ChartControl.ChartLegendTheme(themeIndex);

        }



        private void ReceiveSimulationTimeChangedMessage(SimulationTimeChangedMessage message)

        {

            if (_spatialSimulationModel == null) { return; }



            // 이전 작업 취소 및 토큰 갱신.

            _renderCts?.Cancel();

            _renderCts = new CancellationTokenSource();



            var token = _renderCts.Token;

            var receiveTime = message.NewTime;

            var is3DViewer = _graphChartType == GraphChartType.ThreeD;



            Task.Run(async () =>

            {

                try

                {

                    if (token.IsCancellationRequested) { return; } // 토큰 체크.

                    if (_currentConfig == null) { return; }

                    if (_spatialSimulationModel == null) { return; }



                    // (Background Thread) 데이터 필터링 수행.

                    var filteredSeriesPoints = await Task.Run(() =>

                    {

                        token.ThrowIfCancellationRequested(); // 취소 체크.



                        var filteredSeriesPointBuffer = new Dictionary<int, List<ChartPoint3D>>();

                        foreach (var kvp in _seriesPointBuffer)

                        {

                            var seriesIndex = kvp.Key;

                            var points = kvp.Value;

                            var filteredPoints = points.Where(p => p.Time == receiveTime).ToList();



                            filteredSeriesPointBuffer.Add(seriesIndex, filteredPoints);

                        }



                        return filteredSeriesPointBuffer;

                    }, token);



                    // (UI Thread) 렌더링 수행.

                    await Application.Current.Dispatcher.InvokeAsync(() =>

                    {

                        if (token.IsCancellationRequested) { return; } // 토큰 체크.



                        foreach (var kvp in filteredSeriesPoints)

                        {

                            var seriesIndex = kvp.Key;

                            var points = kvp.Value;



                            // 2D, 3D 분기처리.

                            if (is3DViewer)

                            {

                                if (ChartControl is IChartPlayback chartPlayback)

                                {

                                    var seriesPoints3D = ChartPointMapper.ToSeriesPoints3D(points);

                                    chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(seriesIndex, null, seriesPoints3D));

                                }

                            }

                            else

                            {

                                if (ChartControl is IChartPlayback chartPlayback)

                                {

                                    var seriesPoints2D = ChartPointMapper.ToSeriesPoints2D(points);

                                    chartPlayback.UpdatePlaybackCursorPosition(new AddSeriesPointDTO(seriesIndex, seriesPoints2D, null));

                                }

                            }



                        }

                    }, DispatcherPriority.Render, token);

                }

                catch (OperationCanceledException) { } // 새로운 요청이 들어와 현재 작업 취소.

            });

        }



        #endregion



    }

}
