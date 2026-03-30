using AddSIMIDE.BSM.DA.Scenario;
using AddSIMIDE.Common;
using AddSIMIDE.Common.VO;
using DevExpress.Mvvm;
using DevExpress.Mvvm.DataAnnotations;
using DevExpress.Mvvm.Native;
using DevExpress.Xpf.CodeView;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Data;
using DevExpress.Xpf.Grid;
using OSTES.Common;
using OSTES.Data;
using OSTES.Model;
using OSTES.Service;
using OSTES.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SimulationSpeedTimer
{
    public class SubcomponentLink
    {
        public string SubcomponentTableName { get; set; }

        public string ParentTableName { get; set; }
    }

    public class ResultTableAnalyViewModel : ViewModelBase
    {
        private const int BatchCount = 3000; // 실시간 배치처리 갯수.
        private readonly object _collectionLock = new object(); // 컬렉션 동기화용 락 객체.

        private bool _isUpdating = false;
        private Guid _currentSessionId = Guid.Empty;
        private ConcurrentDictionary<string, ConcurrentQueue<ExpandoObject>> _pendingBuffer = new ConcurrentDictionary<string, ConcurrentQueue<ExpandoObject>>(StringComparer.OrdinalIgnoreCase); // 백그라운드 데이터 버퍼.
        private DispatcherTimer _uiRefreshTimer; // UI 갱신 타이머.
        private Dictionary<string, ObservableCollection<GridBandItem>> _bandCache; // GridControl 동적 컬림 바인딩 컬렉션 내부 캐시.
        private Dictionary<string, ObservableCollectionCore<ExpandoObject>> _rowCache; // GridControl 아이템소스 컬렉션 내부 캐시.

        private Dictionary<string, string> _subcomponentParentMap; // 서브컴포넌트 테이블명 -> 부모 컴포넌트 테이블명.
        private Dictionary<string, HashSet<string>> _configuredFieldCache; // 테이블별 허용 컬럼 캐시.
        private Dictionary<string, Dictionary<double, ExpandoObject>> _rowIndexCache; // 같은 테이블/시간대 row를 다시 찾기 위한 인덱스 캐시.

        public string SelectedTableName
        {
            get => GetValue<string>();
            set
            {
                SetValue(value);
                RaisePropertiesChanged(nameof(Bands));
                RaisePropertiesChanged(nameof(Items));
            }
        }

        /// <summary>
        /// GridControl 동적 컬림 바인딩 컬렉션.
        /// </summary>
        public ObservableCollection<GridBandItem> Bands
        {
            get
            {
                var isVisibleTabName = !string.IsNullOrEmpty(SelectedTableName) && _bandCache.ContainsKey(SelectedTableName);
                return isVisibleTabName == true ? _bandCache[SelectedTableName] : null;
            }
        }

        /// <summary>
        /// GridControl 아이템소스 컬렉션.
        /// </summary>
        public ObservableCollection<ExpandoObject> Items
        {
            get
            {
                var isVisibleTabName = !string.IsNullOrEmpty(SelectedTableName) && _rowCache.ContainsKey(SelectedTableName);
                return isVisibleTabName == true ? _rowCache[SelectedTableName] : null;
            }
        }

        /// <summary>
        /// 화면 좌측 객체리스트 ViewModel.
        /// </summary>
        public ModelTreeViewModel ModelTreeViewModel { get; set; }

        public ResultTableAnalyViewModel()
        {
            Initialize();
            SubscribeEvents();
        }

        private void Initialize()
        {
            ModelTreeViewModel = new ModelTreeViewModel();
            _bandCache = new Dictionary<string, ObservableCollection<GridBandItem>>(StringComparer.OrdinalIgnoreCase);
            _rowCache = new Dictionary<string, ObservableCollectionCore<ExpandoObject>>(StringComparer.OrdinalIgnoreCase);
            _subcomponentParentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _configuredFieldCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _rowIndexCache = new Dictionary<string, Dictionary<double, ExpandoObject>>(StringComparer.OrdinalIgnoreCase);

            // UI 렌더링 부하를 줄이기 위한 Throttling Timer.
            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(100); // 50ms 주기로 UI 갱신.
            _uiRefreshTimer.Tick += OnUIRefreshTimerTick;
            _uiRefreshTimer.Start();
        }

        /// <summary>
        /// UI Thread에 대한 주기적 배치 업데이트 수행.
        /// </summary>
        private async void OnUIRefreshTimerTick(object sender, EventArgs e)
        {
            var selectedTableName = SelectedTableName;

            // 1. 업데이트 중이거나, 현재 선택된 테이블이 없으면 아무런 동작 수행 X.
            if ((_isUpdating) || (string.IsNullOrEmpty(selectedTableName))) { return; }

            // 2. 현재 선택된 테이블의 큐만 처리
            if (_pendingBuffer.TryGetValue(selectedTableName, out var queue))
            {
                if (queue.IsEmpty) { return; }
                if (!_rowCache.TryGetValue(selectedTableName, out var targetCollection)) { return; }

                _isUpdating = true;
                try
                {
                    var batchItems = await Task.Run(() =>
                    {
                        var items = new List<ExpandoObject>();
                        var count = 0;

                        while ((count < BatchCount) && (queue.TryDequeue(out var item)))
                        {
                            items.Add(item);
                            count++;
                        }

                        return items;
                    });

                    if (batchItems.Count > 0)
                    {
                        if (_rowIndexCache.TryGetValue(selectedTableName, out var rowIndex))
                        {
                            try
                            {
                                targetCollection.BeginUpdate();
                                foreach (var item in batchItems)
                                {
                                    var dict = (IDictionary<string, object>)item;
                                    if (!dict.TryGetValue("Time", out var timeValue) || timeValue == null)
                                    {
                                        continue;
                                    }

                                    var time = Convert.ToDouble(timeValue);
                                    if (!rowIndex.TryGetValue(time, out var row))
                                    {
                                        rowIndex[time] = item;
                                        targetCollection.Add(item);
                                        continue;
                                    }

                                    var rowDict = (IDictionary<string, object>)row;
                                    foreach (var kvp in dict)
                                    {
                                        if (kvp.Key == "Time")
                                        {
                                            continue;
                                        }

                                        if (kvp.Value == null)
                                        {
                                            continue;
                                        }

                                        if ((kvp.Value is string stringValue) && (stringValue == "-"))
                                        {
                                            continue;
                                        }

                                        rowDict[kvp.Key] = kvp.Value;
                                    }
                                }
                            }
                            finally
                            {
                                targetCollection.EndUpdate();
                            }
                        }
                        else
                        {
                            try
                            {
                                targetCollection.BeginUpdate();
                                targetCollection.AddRange(batchItems);
                            }
                            finally
                            {
                                targetCollection.EndUpdate();
                            }
                        }
                    }
                }
                finally
                {
                    _isUpdating = false;
                }
            }
        }

        private ExpandoObject CreateExpandoRow(SimulationFrame frame, SimulationTable tableData)
        {
            var row = new ExpandoObject();
            var dict = (IDictionary<string, object>)row;
            var tableName = tableData.TableName;

            if (_subcomponentParentMap.TryGetValue(tableData.TableName, out var parentTableName))
            {
                tableName = parentTableName;
            }

            dict["Time"] = frame.Time;

            var isMergeTarget = _rowIndexCache.ContainsKey(tableName);
            var hasConfiguredFields = _configuredFieldCache.TryGetValue(tableName, out var configuredFields);

            if (isMergeTarget && hasConfiguredFields)
            {
                foreach (var colName in configuredFields)
                {
                    dict[colName] = "-";
                }
            }

            foreach (var colName in tableData.ColumnNames)
            {
                if (isMergeTarget &&
                    hasConfiguredFields &&
                    !configuredFields.Contains(colName))
                {
                    continue;
                }

                dict[colName] = tableData[colName];
            }

            return row;
        }

        #region Command & Command Method(Command 관련 선언)
        #endregion

        #region EventHandlers & Subscriptions (이벤트 핸들러, 메시지 구독자, 통신 응답 처리 관련)
        private void SubscribeEvents()
        {
            ModelTreeViewModel.OnSelectedModelTreeData += ModelTreeViewModel_OnSelectedModelTreeData;
            SimulationContext.Instance.OnSessionStarted += HandleSessionStarted;
            SimulationContext.Instance.OnSessionStopped += Instance_OnSessionStopped;
            SharedFrameRepository.Instance.OnFramesAdded += HandleFramesAdded;
            AddSIMManagerHandler.GetInstance().OnScenarioSetupCompleted += ResultTableAnalyViewModel_OnScenarioSetupCompleted;
        }

        /// <summary>
        /// 컬럼 정보 업데이트
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ResultTableAnalyViewModel_OnScenarioSetupCompleted(object sender, EventArgs e)
        {
            // 전시 컬럼 목록 초기화.
            SelectedTableName = default;

            var currentCurrentScenario = AddSIMManagerHandler.GetInstance().GetCurrentScenario();
            if (currentCurrentScenario == null) { return; }

            var subcomponentLinks = new List<SubcomponentLink>();
            var tableConfigDTOList = new List<TableConfig>();
            var journalingNodes = ScenarioQueries.For(currentCurrentScenario).AttributeQuery().JournalingData(true).ToLeavesByObject();
            var subComponentJournalingNodes = ScenarioQueries.For(currentCurrentScenario).AttributeQuery().JournalingData(true).ToLeavesBySubComponentObject();
            foreach (var subComponent in subComponentJournalingNodes)
            {
                var subComponentLink = new SubcomponentLink()
                {
                    SubcomponentTableName = subComponent.Key.Path,
                    ParentTableName = subComponent.Key.ParentNode.Name,
                };
                subcomponentLinks.Add(subComponentLink);
            }

            foreach (var node in journalingNodes)
            {
                if (!node.Value.Any()) { continue; }

                var addTableConfig = new TableConfig(node.Key.playerObjectName);
                foreach (var leafNode in node.Value)
                {
                    // Band 구조 미사용 (더미 데이터 Band Instance).
                    var tempBandConfig = new BandConfig(string.Empty);

                    // FullPath, FullLabel 분해.
                    var (ObjectName, AttributeName) = AttributeInfoParser.DeconstructFullPath(leafNode);
                    var (ObjectLabel, AttributeLabel) = AttributeInfoParser.DeconstructFullLabel(leafNode);

                    var addColumnConfig = new ColumnConfig(AttributeName, AttributeLabel);

                    tempBandConfig.Columns.Add(addColumnConfig);
                    addTableConfig.Bands.Add(tempBandConfig);
                }
                tableConfigDTOList.Add(addTableConfig);
            }

            UpdateTableConfig(tableConfigDTOList, subcomponentLinks);
        }

        /// <summary>
        /// 객체 선택 이벤트 핸들러.
        /// </summary>
        private void ModelTreeViewModel_OnSelectedModelTreeData(object sender, ModelTreeData e)
        {
            SelectedTableName = e.ObjectName;
        }

        private void HandleSessionStarted(Guid sessionId)
        {
            _currentSessionId = sessionId;

            // 1. 대기열(Buffer) 비우기.
            foreach (var queue in _pendingBuffer.Values)
            {
                while (queue.TryDequeue(out _)) ;
            }
            _pendingBuffer.Clear();

            foreach (var rowIndex in _rowIndexCache.Values)
            {
                rowIndex.Clear();
            }

            // 2. 화면(Grid) 비우기.
            foreach (var collection in _rowCache.Values)
            {
                collection.Clear();
            }
        }

        private void Instance_OnSessionStopped()
        {
            _currentSessionId = Guid.Empty;
        }

        /// <summary>
        /// 공유 저장소로부터 데이터 수신.
        /// 해당 메소드에서 직접 UI에 업데이트하지않고 수신 데이터 Enqueue만 수행.
        /// </summary>
        private void HandleFramesAdded(List<SimulationFrame> frames, Guid sessionId)
        {
            if (_currentSessionId != sessionId) { return; }
            if ((frames == null) || (frames.Count == 0)) { return; }

            foreach (var frame in frames)
            {
                foreach (var tableData in frame.AllTables)
                {
                    var tableName = tableData.TableName;

                    if (_subcomponentParentMap.TryGetValue(tableData.TableName, out var parentTableName))
                    {
                        tableName = parentTableName;
                    }
                    else if (!_rowCache.ContainsKey(tableName))
                    {
                        continue;
                    }

                    var row = CreateExpandoRow(frame, tableData);
                    var queue = _pendingBuffer.GetOrAdd(tableName, _ => new ConcurrentQueue<ExpandoObject>());

                    queue.Enqueue(row);
                }
            }
        }

        private void UpdateTableConfig(List<TableConfig> tableConfigs, IEnumerable<SubcomponentLink> subcomponentLinks)
        {
            if (tableConfigs == null) { return; }

            _bandCache.Clear();
            _rowCache.Clear();
            _pendingBuffer.Clear(); // 설정 초기화 시 버퍼도 비움.
            _subcomponentParentMap.Clear();
            _configuredFieldCache.Clear();
            _rowIndexCache.Clear();

            // 0. 테이블 기준 반복문 수행.
            foreach (var tableConfig in tableConfigs)
            {
                if (tableConfig.Bands.Count == 0) { continue; }

                // 1. 행 데이터 컬렉션 생성
                var rows = new ObservableCollectionCore<ExpandoObject>();

                // WPF 백그라운드 스레드 업데이트 지원 설정
                BindingOperations.EnableCollectionSynchronization(rows, _collectionLock);

                _rowCache[tableConfig.TableObjectName] = rows;

                // 2. 밴드 구성.
                var timeColumn = new GridColumnItem() { FieldName = "Time", Header = "Time" }; // 시간 컬럼(고정).
                var timeBand = new GridBandItem() { Header = "Time", ColumnItems = new ObservableCollection<GridColumnItem>() { timeColumn } }; // 시간 밴드(고정).
                var bands = new ObservableCollection<GridBandItem>() { timeBand };
                var configuredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var band in tableConfig.Bands)
                {
                    var addBand = new GridBandItem()
                    {
                        Header = band.Header,
                    };
                    addBand.ColumnItems.AddRange(band.Columns.Select(c => new GridColumnItem { FieldName = c.FieldName, Header = c.Header }));

                    foreach (var column in band.Columns)
                    {
                        configuredFields.Add(column.FieldName);
                    }

                    bands.Add(addBand);
                }

                _bandCache[tableConfig.TableObjectName] = bands;
                _configuredFieldCache[tableConfig.TableObjectName] = configuredFields;
            }

            if (subcomponentLinks != null)
            {
                foreach (var link in subcomponentLinks)
                {
                    if (link == null ||
                        string.IsNullOrEmpty(link.SubcomponentTableName) ||
                        string.IsNullOrEmpty(link.ParentTableName)) continue;
                    if (!_rowCache.ContainsKey(link.ParentTableName)) continue;

                    _subcomponentParentMap[link.SubcomponentTableName] = link.ParentTableName;

                    if (!_rowIndexCache.ContainsKey(link.ParentTableName))
                    {
                        _rowIndexCache[link.ParentTableName] = new Dictionary<double, ExpandoObject>();
                    }
                }
            }

            RaisePropertiesChanged(nameof(Bands));
            RaisePropertiesChanged(nameof(Items));
        }
        #endregion
    }
}
