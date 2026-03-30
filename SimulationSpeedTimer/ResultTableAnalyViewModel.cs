using DevExpress.Xpf.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

namespace SimulationSpeedTimer
{
    public class SubcomponentLink
    {
        public string SubcomponentTableName { get; set; }

        public string ParentTableName { get; set; }
    }

    public class RowPatch
    {
        public double Time { get; set; }

        public Dictionary<string, object> FieldValues { get; set; }
    }

    public class ResultTableAnalyViewModel : ViewModelBase
    {
        private const int BatchCount = 3000;
        private readonly object _collectionLock = new object();

        private bool _isUpdating = false;
        private Guid _currentSessionId = Guid.Empty;
        private ConcurrentDictionary<string, ConcurrentQueue<RowPatch>> _pendingBuffer = new ConcurrentDictionary<string, ConcurrentQueue<RowPatch>>();
        private DispatcherTimer _uiRefreshTimer;
        private Dictionary<string, ObservableCollection<GridBandItem>> _bandCache;
        private Dictionary<string, ObservableCollectionCore<ExpandoObject>> _rowCache;

        private Dictionary<string, string> _subcomponentParentMap;
        private Dictionary<string, HashSet<string>> _configuredFieldCache;
        private Dictionary<string, Dictionary<double, ExpandoObject>> _rowIndexCache;

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
                return isVisibleTabName ? _bandCache[SelectedTableName] : null;
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
                return isVisibleTabName ? _rowCache[SelectedTableName] : null;
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
            _bandCache = new Dictionary<string, ObservableCollection<GridBandItem>>();
            _rowCache = new Dictionary<string, ObservableCollectionCore<ExpandoObject>>();
            _subcomponentParentMap = new Dictionary<string, string>();
            _configuredFieldCache = new Dictionary<string, HashSet<string>>();
            _rowIndexCache = new Dictionary<string, Dictionary<double, ExpandoObject>>();

            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(100);
            _uiRefreshTimer.Tick += OnUIRefreshTimerTick;
            _uiRefreshTimer.Start();
        }

        /// <summary>
        /// UI Thread에 대한 주기적 배치 업데이트 수행.
        /// </summary>
        private async void OnUIRefreshTimerTick(object sender, EventArgs e)
        {
            if (_isUpdating || string.IsNullOrEmpty(SelectedTableName))
            {
                return;
            }

            var selectedTableName = SelectedTableName;

            if (_pendingBuffer.TryGetValue(selectedTableName, out var queue))
            {
                if (queue.IsEmpty)
                {
                    return;
                }

                if (!_rowCache.TryGetValue(selectedTableName, out var targetCollection))
                {
                    return;
                }

                if (!_rowIndexCache.TryGetValue(selectedTableName, out var rowIndex))
                {
                    return;
                }

                _isUpdating = true;
                try
                {
                    var batchItems = await Task.Run(() =>
                    {
                        var items = new List<RowPatch>();
                        var count = 0;

                        while (count < BatchCount && queue.TryDequeue(out var item))
                        {
                            items.Add(item);
                            count++;
                        }

                        return items;
                    });

                    if (batchItems.Count > 0)
                    {
                        try
                        {
                            targetCollection.BeginUpdate();
                            foreach (var patch in batchItems)
                            {
                                if (!rowIndex.TryGetValue(patch.Time, out var row))
                                {
                                    row = CreateExpandoRow(selectedTableName, patch.Time, patch.FieldValues);
                                    rowIndex[patch.Time] = row;
                                    targetCollection.Add(row);
                                    continue;
                                }

                                var dict = (IDictionary<string, object>)row;
                                foreach (var pair in patch.FieldValues)
                                {
                                    dict[pair.Key] = pair.Value;
                                }
                            }
                        }
                        finally
                        {
                            targetCollection.EndUpdate();
                        }
                    }
                }
                finally
                {
                    _isUpdating = false;
                }
            }
        }

        private ExpandoObject CreateExpandoRow(string tableName, double time, IDictionary<string, object> fieldValues)
        {
            var row = new ExpandoObject();
            var dict = (IDictionary<string, object>)row;

            dict[AppConst.TimeAttributeName] = time;
            if (_configuredFieldCache.TryGetValue(tableName, out var configuredFields))
            {
                foreach (var fieldName in configuredFields)
                {
                    dict[fieldName] = "—";
                }
            }

            foreach (var pair in fieldValues)
            {
                dict[pair.Key] = pair.Value;
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
        private void ResultTableAnalyViewModel_OnScenarioSetupCompleted(object sender, EventArgs e)
        {
            SelectedTableName = default;

            var currentCurrentScenario = AddSIMManagerHandler.GetInstance().GetCurrentScenario();
            if (currentCurrentScenario == null)
            {
                return;
            }

            var subcomponentLinks = new List<SubcomponentLink>();
            var tableConfigDTOList = new List<TableConfig>();
            var journalingNodes = ScenarioQueries.For(currentCurrentScenario).AttributeQuery().JournalingData(true).ToLeavesByObject();
            var subComponentJournalingNodes = ScenarioQueries.For(currentCurrentScenario).AttributeQuery().JournalingData(true).ToLeavesBySubComponentObject();

            foreach (var subComponent in subComponentJournalingNodes)
            {
                var subComponentLink = new SubcomponentLink
                {
                    SubcomponentTableName = subComponent.Key.Path,
                    ParentTableName = subComponent.Key.ParentNode.Name,
                };
                subcomponentLinks.Add(subComponentLink);
            }

            foreach (var node in journalingNodes)
            {
                if (!node.Value.Any())
                {
                    continue;
                }

                var addTableConfig = new TableConfig(node.Key.playerObjectName);
                foreach (var leafNode in node.Value)
                {
                    var tempBandConfig = new BandConfig(string.Empty);

                    var (_, attributeName) = AttributeInfoParser.DeconstructFullPath(leafNode);
                    var (_, attributeLabel) = AttributeInfoParser.DeconstructFullLabel(leafNode);

                    var addColumnConfig = new ColumnConfig(attributeName, attributeLabel);

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

            foreach (var queue in _pendingBuffer.Values)
            {
                while (queue.TryDequeue(out _))
                {
                }
            }

            _pendingBuffer.Clear();
            foreach (var rowIndex in _rowIndexCache.Values)
            {
                rowIndex.Clear();
            }

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
            if (_currentSessionId != sessionId)
            {
                return;
            }

            if (frames == null || frames.Count == 0)
            {
                return;
            }

            foreach (var frame in frames)
            {
                if (!frame.AllTables.Any())
                {
                    continue;
                }

                var rowPatchCache = new Dictionary<string, Dictionary<string, object>>();

                foreach (var tableData in frame.AllTables)
                {
                    if (!_subcomponentParentMap.TryGetValue(tableData.TableName, out var parentTableName))
                    {
                        continue;
                    }

                    if (!_configuredFieldCache.TryGetValue(parentTableName, out var configuredFields))
                    {
                        continue;
                    }

                    if (!rowPatchCache.TryGetValue(parentTableName, out var fieldValues))
                    {
                        fieldValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        rowPatchCache[parentTableName] = fieldValues;
                    }

                    foreach (var colName in tableData.ColumnNames)
                    {
                        if (!configuredFields.Contains(colName))
                        {
                            continue;
                        }

                        var value = tableData[colName];
                        if (value != null)
                        {
                            fieldValues[colName] = value;
                        }
                    }
                }

                foreach (var patch in rowPatchCache)
                {
                    if (patch.Value.Count == 0)
                    {
                        continue;
                    }

                    var queue = _pendingBuffer.GetOrAdd(patch.Key, _ => new ConcurrentQueue<RowPatch>());
                    queue.Enqueue(new RowPatch
                    {
                        Time = frame.Time,
                        FieldValues = patch.Value
                    });
                }
            }
        }

        private void UpdateTableConfig(List<TableConfig> tableConfigs, IEnumerable<SubcomponentLink> subcomponentLinks)
        {
            if (tableConfigs == null)
            {
                return;
            }

            _bandCache.Clear();
            _rowCache.Clear();
            _pendingBuffer.Clear();
            _subcomponentParentMap.Clear();
            _configuredFieldCache.Clear();
            _rowIndexCache.Clear();

            foreach (var tableConfig in tableConfigs)
            {
                if (tableConfig.Bands.Count == 0)
                {
                    continue;
                }

                _subcomponentParentMap[tableConfig.TableObjectName] = tableConfig.TableObjectName;

                var rows = new ObservableCollectionCore<ExpandoObject>();
                BindingOperations.EnableCollectionSynchronization(rows, _collectionLock);

                _rowCache[tableConfig.TableObjectName] = rows;
                _rowIndexCache[tableConfig.TableObjectName] = new Dictionary<double, ExpandoObject>();

                var timeColumn = new GridColumnItem
                {
                    FieldName = AppConst.TimeAttributeName,
                    Header = AppConst.TimeAttributeName
                };

                var timeBand = new GridBandItem
                {
                    Header = AppConst.TimeAttributeName,
                    ColumnItems = new ObservableCollection<GridColumnItem> { timeColumn }
                };

                var bands = new ObservableCollection<GridBandItem> { timeBand };
                var configuredFields = new HashSet<string>();

                foreach (var band in tableConfig.Bands)
                {
                    var addBand = new GridBandItem { Header = band.Header };
                    var columns = band.Columns.Select(c => new GridColumnItem
                    {
                        FieldName = c.FieldName,
                        Header = c.Header
                    });

                    foreach (var column in columns)
                    {
                        addBand.ColumnItems.Add(column);
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
                        string.IsNullOrEmpty(link.ParentTableName))
                    {
                        continue;
                    }

                    if (!_rowCache.ContainsKey(link.ParentTableName))
                    {
                        continue;
                    }

                    _subcomponentParentMap[link.SubcomponentTableName] = link.ParentTableName;
                }
            }

            RaisePropertiesChanged(nameof(Bands));
            RaisePropertiesChanged(nameof(Items));
        }
        #endregion
    }
}
