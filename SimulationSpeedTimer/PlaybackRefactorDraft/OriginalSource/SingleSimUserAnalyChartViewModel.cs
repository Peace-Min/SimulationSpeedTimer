using AddSIMIDE.Common.VO;

using Arction.Wpf.Charting;

using DevExpress.Mvvm;

using DevExpress.Mvvm.Native;

using DevExpress.PivotGrid.PivotTable;

using DevExpress.Utils.Filtering;

using DevExpress.Xpf.Charts;

using DevExpress.XtraSpreadsheet.Model;

using OSTES.Chart;

using OSTES.Common;

using OSTES.Data;

using OSTES.Dialog;

using OSTES.Interface;

using OSTES.Model;

using OSTES.Service;

using OSTES.ViewModel.Dialog;

using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Linq;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Media.Animation;

using System.Windows.Threading;

using ChartViewType = OSTES.Common.ChartViewType;



namespace OSTES.ViewModel.SIngleSim

{

    /// <summary>

    /// 사전 분석 - 사용자 정의 차트 뷰모델.

    /// </summary>

    public class SingleSimUserAnalyChartViewModel : ViewModelBase, IDisposable

    {

        private const double _defaultMinRange = 0; // 차트전시에 사용되는 기본 Min 범위.

        private const double _defaultMaxRange = 10; // 차트전시에 사용되는 기본 Max 범위.



        private readonly Guid _guid; // 식별자 Id.

        private readonly OSTES.Dialog.IDialogService _dialogService = new DialogService();



        private bool _isAutuFit = true;

        private bool _is3DViewer;



        private List<ChartQueryRequest> _chartQueryRequests;

        private ChartRangeData _charRangeData;

        private CancellationTokenSource _renderCts;



        private ChartViewType _chartViewType;



        /// <summary>

        /// 시리즈 데이터를 저장하는 딕셔너리.

        /// </summary>

        private Dictionary<int, List<ChartPoint3D>> _seriesPointBuffer;



        public SingleSimUserAnalyChartViewModel()

        {

            _guid = Guid.NewGuid();

            Initialize();

        }



        public SingleSimUserAnalyChartViewModel(AnalysisChartProfile profile, CScenarioInfo scenarioInfo) : this()

        {

            UserAnalSetViewModel = new UserAnalSetViewModel(profile.Config, scenarioInfo);

        }



        public string Title { get => GetValue<string>(); set => SetValue(value); }



        public IUserCustomChart ChartControl { get => GetValue<IUserCustomChart>(); set => SetValue(value); }



        public UserAnalSetViewModel UserAnalSetViewModel { get; set; }



        private void Initialize()

        {

            Title = "시험 결과";

            UserAnalSetViewModel = new UserAnalSetViewModel(Title);

            ChartControl = new UserCustomChart_2DGraph(ChartViewType.TwoD_Time, UserAnalSetViewModel.AxisXTitleText, UserAnalSetViewModel.AxisYTitleText);



            SubscribeEvents();

        }



        public void LoadScenario(CScenarioInfo scenarioInfo)

        {

            UserAnalSetViewModel.LoadScenario(scenarioInfo);

        }



        public List<ChartQueryRequest> ChartSetting(bool isLoadedFromConfig = false)

        {

            // 차트 설정 시점 이벤트 해제.

            DynamicUnSubscribeEvents();



            if (isLoadedFromConfig) // Config를 통한 Load 작업 시 선택된 정보가 없으면 해당 메소드 수행하지않는다.

            {

                if (!UserAnalSetViewModel.SeriesSelectionResults.Any()) { return default; }

            }

            else // dialogService는 Config Load에서는 Show하지 않는다.

            {

                if (_dialogService.ShowDialog("분석 설정", UserAnalSetViewModel) == false) { return default; }

            }



            // 260408(min) 전역테마 적용.

            UserAnalSetViewModel.ChartThemeIndex = ChartThemeComboItem.GetThemaIndex(AppConst.DEFAULT_CHART_THEME);



            _chartQueryRequests = new List<ChartQueryRequest>();

            _seriesPointBuffer = new Dictionary<int, List<ChartPoint3D>>();



            // 1. 이전 차트 컨트롤 초기화.

            if (ChartControl is IDisposable disposable)

            {

                disposable?.Dispose();

            }

            ChartControl = null;



            // 2. Setting ViewModel에서 설정한 값을 현재 ViewModel에 반영한다.

            Title = UserAnalSetViewModel.TitleText; // 설정된 타이틀을 반영한다.

            _isAutuFit = UserAnalSetViewModel.IsAutoFit; // 설정된 자동 맞춤을 반영한다.

            _charRangeData = UserAnalSetViewModel.ChartRangeModelItem.Clone(); // 설정된 X,Y,Z 전시 범위를 반영한다.

            _is3DViewer = UserAnalSetViewModel.Is3DChartViewer;

            _chartViewType = UserAnalSetViewModel.SelectedChartViewType;



            // 시간축 차트 분기처리.

            if (UserAnalSetViewModel.IsTimeBasedAxis)

            {

                //Subplot vs MultiSeries 분기 처리.

                if (UserAnalSetViewModel.SelectedChartViewType == ChartViewType.TwoD_Time_Multi) // Subplot.

                {

                    // 차트 초기화.

                    ChartControl = new UserCustomChart_2DSubplotGraph(UserAnalSetViewModel.SelectedChartViewType, UserAnalSetViewModel.AxisXTitleText, UserAnalSetViewModel.AxisYTitleText);



                    var seriesIndex = 0;

                    foreach (var axisItem in UserAnalSetViewModel.SeriesSelectionResults)

                    {

                        var selectedXItem = axisItem.SelectedSeriesX;

                        var selectedYItem = axisItem.SelectedSeriesY;

                        //var labelName = $"{selectedYItem.Label}";

                        var chartPrimaryKey = new ChartPrimaryKey(_guid, seriesIndex);

                        var dbConfig = new DatabaseQueryConfig() { SeriesIndex = seriesIndex };



                        SetYConfig(selectedYItem.ObjectName, selectedYItem.AttributeName, ref dbConfig);

                        SetXConfig(selectedXItem.ObjectName, selectedXItem.AttributeName, ref dbConfig);



                        // 차트 시리즈 설정.

                        var lineChartSeriesDTO = new LineChartSeriesDTO(seriesIndex++, UserAnalSetViewModel.ChartThemeIndex, axisItem.LineSeriesTitle, selectedYItem.Color, selectedYItem.LineSeriesType, UserAnalSetViewModel.AxisYTitleText);

                        ChartControl.AddLineChartSeries(lineChartSeriesDTO);

                        _chartQueryRequests.Add(new ChartQueryRequest(chartPrimaryKey, dbConfig));

                    }



                    ChartControl.ChartLegendTheme(UserAnalSetViewModel.ChartThemeIndex);

                    if (ChartControl is IChartUIControl chartUIControl)

                    {

                        chartUIControl.UpdateTrackAnnotationColorFromResource();

                    }

                }

                else

                {

                    // 차트 초기화.

                    ChartControl = new UserCustomChart_2DGraph(UserAnalSetViewModel.SelectedChartViewType, UserAnalSetViewModel.AxisXTitleText, UserAnalSetViewModel.AxisYTitleText);



                    var seriesIndex = 0;

                    foreach (var axisItem in UserAnalSetViewModel.SeriesSelectionResults)

                    {

                        var selectedXItem = axisItem.SelectedSeriesX;

                        var selectedYItem = axisItem.SelectedSeriesY;

                        //var labelName = $"{selectedXItem.Label} vs {selectedYItem.Label}";

                        var chartPrimaryKey = new ChartPrimaryKey(_guid, seriesIndex);

                        var dbConfig = new DatabaseQueryConfig() { SeriesIndex = seriesIndex };



                        SetYConfig(selectedYItem.ObjectName, selectedYItem.AttributeName, ref dbConfig);

                        SetXConfig(selectedXItem.ObjectName, selectedXItem.AttributeName, ref dbConfig);



                        // 차트 시리즈 설정.

                        var lineChartSeriesDTO = new LineChartSeriesDTO(seriesIndex++, UserAnalSetViewModel.ChartThemeIndex, axisItem.LineSeriesTitle, selectedYItem.Color, selectedYItem.LineSeriesType);

                        ChartControl.AddLineChartSeries(lineChartSeriesDTO);

                        _chartQueryRequests.Add(new ChartQueryRequest(chartPrimaryKey, dbConfig));

                    }



                    ChartControl.ChartThemeSelection(UserAnalSetViewModel.ChartThemeIndex);

                    ChartControl.ChartLegendTheme(UserAnalSetViewModel.ChartThemeIndex);

                }

            }

            else

            {

                // 3D 분기 처리.

                if (UserAnalSetViewModel.Is3DChartViewer)

                {

                    // 차트 초기화.

                    ChartControl = new UserCustomChart_3DGraph(UserAnalSetViewModel.SelectedChartViewType, UserAnalSetViewModel.AxisXTitleText, UserAnalSetViewModel.AxisYTitleText, UserAnalSetViewModel.AxisZTitleText);



                    var seriesIndex = 0;

                    foreach (var axisItem in UserAnalSetViewModel.SeriesSelectionResults)

                    {

                        var selectedXItem = axisItem.SelectedSeriesX;

                        var selectedYItem = axisItem.SelectedSeriesY;

                        var selectedZItem = axisItem.SelectedSeriesZ;



                        var chartPrimaryKey = new ChartPrimaryKey(_guid, seriesIndex);

                        var dbConfig = new DatabaseQueryConfig() { SeriesIndex = seriesIndex };



                        SetZConfig(selectedZItem.ObjectName, selectedZItem.AttributeName, ref dbConfig);

                        SetYConfig(selectedYItem.ObjectName, selectedYItem.AttributeName, ref dbConfig);

                        SetXConfig(selectedXItem.ObjectName, selectedXItem.AttributeName, ref dbConfig);



                        // 차트 시리즈 설정.

                        var lineChartSeriesDTO = new LineChartSeriesDTO(seriesIndex++, UserAnalSetViewModel.ChartThemeIndex, axisItem.LineSeriesTitle, selectedYItem.Color, selectedYItem.LineSeriesType);

                        ChartControl.AddLineChartSeries(lineChartSeriesDTO);

                        _chartQueryRequests.Add(new ChartQueryRequest(chartPrimaryKey, dbConfig));

                    }



                    ChartControl.ChartThemeSelection(UserAnalSetViewModel.ChartThemeIndex);

                    ChartControl.ChartLegendTheme(UserAnalSetViewModel.ChartThemeIndex);

                }

                else

                {

                    // 차트 초기화.

                    ChartControl = new UserCustomChart_2DGraph(UserAnalSetViewModel.SelectedChartViewType, UserAnalSetViewModel.AxisXTitleText, UserAnalSetViewModel.AxisYTitleText);



                    var seriesIndex = 0;

                    foreach (var axisItem in UserAnalSetViewModel.SeriesSelectionResults)

                    {

                        var selectedXItem = axisItem.SelectedSeriesX;

                        var selectedYItem = axisItem.SelectedSeriesY;

                        //var labelName = $"{selectedXItem.Label} vs {selectedYItem.Label}";

                        var chartPrimaryKey = new ChartPrimaryKey(_guid, seriesIndex);

                        var dbConfig = new DatabaseQueryConfig() { SeriesIndex = seriesIndex };



                        SetYConfig(selectedYItem.ObjectName, selectedYItem.AttributeName, ref dbConfig);

                        SetXConfig(selectedXItem.ObjectName, selectedXItem.AttributeName, ref dbConfig);



                        // 차트 시리즈 설정.

                        var lineChartSeriesDTO = new LineChartSeriesDTO(seriesIndex++, UserAnalSetViewModel.ChartThemeIndex, axisItem.LineSeriesTitle, selectedYItem.Color, selectedYItem.LineSeriesType);

                        ChartControl.AddLineChartSeries(lineChartSeriesDTO);

                        _chartQueryRequests.Add(new ChartQueryRequest(chartPrimaryKey, dbConfig));

                    }



                    ChartControl.ChartThemeSelection(UserAnalSetViewModel.ChartThemeIndex);

                    ChartControl.ChartLegendTheme(UserAnalSetViewModel.ChartThemeIndex);

                }

            }



            if (ChartControl is IChartPlayback chartPlayback)

            {

                chartPlayback.SetPlaybackCursor();

            }



            return _chartQueryRequests;



            #region Local Function

            void SetXConfig(string objectname, string attributeName, ref DatabaseQueryConfig config)

            {

                config.XColumn.ObjectName = objectname;

                config.XColumn.AttributeName = attributeName;

            }



            void SetYConfig(string objectname, string attributeName, ref DatabaseQueryConfig config)

            {

                config.YColumn.ObjectName = objectname;

                config.YColumn.AttributeName = attributeName;

            }



            void SetZConfig(string objectname, string attributeName, ref DatabaseQueryConfig config)

            {

                config.ZColumn.ObjectName = objectname;

                config.ZColumn.AttributeName = attributeName;

            }

            #endregion

        }



        /// <summary>

        /// DB 조회 데이터 수신.

        /// </summary>

        public async Task ReceiveChartSeriesBuffer(ChartSeriesBuffer buffer)

        {

            // 데이터 할당 시점에 이벤트 등록.

            DynamicUnSubscribeEvents();

            DynamicSubscribeEvents();



            var chartPrimaryKey = buffer.ChartPrimaryKey;



            // 시리즈 buffer 저장.

            _seriesPointBuffer.Add(buffer.ChartPrimaryKey.SeriesIndex, buffer.ChartPoint3DList);



            await Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>

            {

                // 2. 2D, 3D 분기처리.

                if (_is3DViewer)

                {

                    var seriesPoints = ChartPointMapper.ToSeriesPoints3D(buffer.ChartPoint3DList);



                    //  AutoFit인 경우 ChartRange를 min, max값으로 초기화한다.

                    if (_isAutuFit)

                    {

                        this._charRangeData = new ChartRangeData()

                        {

                            X_Min = seriesPoints.Min(p => p.X),

                            X_Max = seriesPoints.Max(p => p.X),

                            Y_Min = seriesPoints.Min(p => p.Y),

                            Y_Max = seriesPoints.Max(p => p.Y), // 보정값이 10보다 작은 경우 10으로 설정.

                            Z_Min = seriesPoints.Min(p => p.Z),

                            Z_Max = seriesPoints.Max(p => p.Z), // 보정값이 10보다 작은 경우 10으로 설정.

                        };

                    }

                    else

                    {

                        this._charRangeData.SwapYZ();

                    }



                    ChartControl.BeginUpdate();



                    var addSeriesPointDTO = new AddSeriesPointDTO(buffer.ChartPrimaryKey.SeriesIndex, null, seriesPoints);

                    ChartControl.AddPoints(addSeriesPointDTO);

                    ChartControl.SetRange(_charRangeData, buffer.ChartPrimaryKey.SeriesIndex);

                    ChartControl.EndUpdate();

                }

                else

                {

                    var seriesPoints = ChartPointMapper.ToSeriesPoints2D(buffer.ChartPoint3DList);



                    //  AutoFit인 경우 ChartRange를 min, max값으로 초기화한다.

                    if (_isAutuFit)

                    {

                        //var filterd = seriesPoints.Skip(1).ToList();

                        this._charRangeData = new ChartRangeData()

                        {

                            X_Min = seriesPoints.Min(p => p.X),

                            X_Max = seriesPoints.Max(p => p.X),

                            Y_Min = seriesPoints.Min(p => p.Y),

                            Y_Max = seriesPoints.Max(p => p.Y),//Math.Max(seriesPoints.Max(p => p.Y) * AppConst.CHART_Y_MARGIN_FACTOR, 10), // 보정값이 10보다 작은 경우 10으로 설정.

                        };

                    }



                    var addSeriesPointDTO = new AddSeriesPointDTO(buffer.ChartPrimaryKey.SeriesIndex, seriesPoints, null);

                    ChartControl.BeginUpdate();

                    ChartControl.AddPoints(addSeriesPointDTO);



                    // 차트 종류별 YAxes SetRange 분기처리.

                    switch (_chartViewType)

                    {

                        case ChartViewType.TwoD_Time_Multi:

                            //this._charRangeData.Y_Min *= AppConst.CHART_Y_MARGIN_FACTOR; // subplot은 yMax 보정값 적용.

                            ChartControl.SetRange(_charRangeData, buffer.ChartPrimaryKey.SeriesIndex);

                            break;



                        case ChartViewType.TwoD_Time:

                        case ChartViewType.TwoD_AxisSelectable:

                        case ChartViewType.TwoD_LL:

                        case ChartViewType.TwoD_XY:

                            ChartControl.SetRange(_charRangeData, 0);

                            //ChartControl.ZoomToFit();

                            break;



                        default:

                            throw new ArgumentOutOfRangeException(nameof(UserAnalSetViewModel.SelectedChartViewType));

                    }



                    ChartControl.EndUpdate();

                }

            }));

        }



        #region Command & Command Method(Command 관련 선언)

        #endregion



        #region EventHandlers & Subscriptions (이벤트 핸들러, 통신 응답 처리 관련)

        private void SubscribeEvents()

        {

            Messenger.Default.Register<ChartThemeComboItem>(this, ReceiveUpdateChartTheme);

        }



        private void DynamicSubscribeEvents()

        {

            Messenger.Default.Register<SimulationTimeChangedMessage>(this, ReceiveSimulationTimeChangedMessage);

        }



        private void DynamicUnSubscribeEvents()

        {

            Messenger.Default.Unregister<SimulationTimeChangedMessage>(this);

        }



        private void ReceiveUpdateChartTheme(ChartThemeComboItem param)

        {

            var themeIndex = ChartThemeComboItem.GetThemaIndex(param.Name);



            ChartControl.BeginUpdate();

            ChartControl.ChartThemeSelection(themeIndex);

            ChartControl.ChartLegendTheme(themeIndex);

            ChartControl.EndUpdate();

        }



        private void ReceiveSimulationTimeChangedMessage(SimulationTimeChangedMessage message)

        {

            if (_chartQueryRequests == null) { return; } // 차트 설정이 안되어있으면 reuturn.



            // 이전 작업 취소 및 토큰 갱신.

            _renderCts?.Cancel();

            _renderCts = new CancellationTokenSource();



            var token = _renderCts.Token;

            var receiveTime = message.NewTime;



            Task.Run(async () =>

            {

                try

                {

                    if (token.IsCancellationRequested) { return; } // 토큰 체크.



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

                            if (_is3DViewer)

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



        public void Dispose()

        {

            DynamicUnSubscribeEvents();

            ChartControl?.Dispose();

        }

    }

}
