using DevExpress.Xpf.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 실제 SW 환경을 기준으로 한 ResultTableAnalyViewModel 스케치입니다.
    /// 이 파일은 현재 csproj에 포함되지 않으며, 구조 설명과 이식용 코드 목적입니다.
    ///
    /// 핵심 정책:
    /// 1. 모든 테이블 데이터는 메모리 저장소에 유지
    /// 2. GridControl은 현재 선택된 테이블의 PagedAsyncSource만 바인딩
    /// 3. 수신 시 전체 테이블 저장은 하되, UI 갱신은 선택된 테이블 source만 refresh
    /// 4. leaf node로 선택된 컬럼만 메모리에 저장해 row payload를 줄임
    /// </summary>
    public class ResultTableAnalyViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ConcurrentDictionary<string, TablePageStore> _tableStores =
            new ConcurrentDictionary<string, TablePageStore>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ObservableCollection<GridBandItem>> _bandCache =
            new Dictionary<string, ObservableCollection<GridBandItem>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, HashSet<string>> _configuredFieldsByTable =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly DispatcherTimer _selectedTableRefreshTimer;

        private Guid _currentSessionId = Guid.Empty;
        private string _selectedTableName;
        private PagedAsyncSource _items;
        private bool _selectedTableDirty;

        public ModelTreeViewModel ModelTreeViewModel { get; private set; }

        /// <summary>
        /// 현재 선택된 테이블의 밴드 정의입니다.
        /// 현재 XAML 호환을 위해 유지합니다.
        /// </summary>
        public ObservableCollection<GridBandItem> Bands
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedTableName))
                {
                    return null;
                }

                return _bandCache.TryGetValue(_selectedTableName, out var bands) ? bands : null;
            }
        }

        /// <summary>
        /// GridControl ItemsSource에 직접 바인딩하는 가상 데이터 소스입니다.
        /// 선택된 테이블이 바뀔 때마다 새 source로 교체합니다.
        /// </summary>
        public PagedAsyncSource Items
        {
            get => _items;
            private set
            {
                if (ReferenceEquals(_items, value))
                {
                    return;
                }

                _items = value;
                OnPropertyChanged(nameof(Items));
            }
        }

        public string SelectedTableName
        {
            get => _selectedTableName;
            set
            {
                if (string.Equals(_selectedTableName, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedTableName = value;
                OnPropertyChanged(nameof(SelectedTableName));
                OnPropertyChanged(nameof(Bands));
                SwitchSelectedTableSource();
            }
        }

        public ResultTableAnalyViewModel()
        {
            ModelTreeViewModel = new ModelTreeViewModel();

            // PagedAsyncSource refresh를 너무 자주 호출하지 않도록 선택 테이블만 throttle 합니다.
            _selectedTableRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            _selectedTableRefreshTimer.Interval = TimeSpan.FromMilliseconds(100);
            _selectedTableRefreshTimer.Tick += OnSelectedTableRefreshTimerTick;
            _selectedTableRefreshTimer.Start();

            SubscribeEvents();
        }

        private void SubscribeEvents()
        {
            ModelTreeViewModel.OnSelectedModelTreeData += ModelTreeViewModel_OnSelectedModelTreeData;
            SimulationContext.Instance.OnSessionStarted += HandleSessionStarted;
            SharedFrameRepository.Instance.OnFramesAdded += HandleFramesAdded;

            // 실제 SW에서 사용 중인 시나리오 설정 이벤트를 그대로 가정합니다.
            AddSIMManagerHandler.GetInstance().OnScenarioSetupCompleted += ResultTableAnalyViewModel_OnScenarioSetupCompleted;
        }

        private void ResultTableAnalyViewModel_OnScenarioSetupCompleted(object sender, EventArgs e)
        {
            SelectedTableName = null;

            var currentScenario = AddSIMManagerHandler.GetInstance().GetCurrentScenario();
            if (currentScenario == null)
            {
                return;
            }

            var tableConfigs = new List<TableConfig>();
            var journalingNodes = ScenarioQueries.For(currentScenario).AttributeQuery().JournalingData(true).ToLeavesByObject();

            foreach (var node in journalingNodes)
            {
                if (!node.Value.Any())
                {
                    continue;
                }

                var tableConfig = new TableConfig(node.Key.playerObjectName);

                foreach (var leafNode in node.Value)
                {
                    var tempBand = new BandConfig(string.Empty);
                    var (_, attributeName) = AttributeInfoParser.DeconstructFullPath(leafNode);
                    var (_, attributeLabel) = AttributeInfoParser.DeconstructFullLabel(leafNode);

                    tempBand.Columns.Add(new ColumnConfig(attributeName, attributeLabel));
                    tableConfig.Bands.Add(tempBand);
                }

                tableConfigs.Add(tableConfig);
            }

            UpdateTableConfig(tableConfigs);
        }

        private void ModelTreeViewModel_OnSelectedModelTreeData(object sender, ModelTreeData e)
        {
            SelectedTableName = e.ObjectName;
        }

        private void HandleSessionStarted(Guid sessionId)
        {
            _currentSessionId = sessionId;
            _selectedTableDirty = false;

            foreach (var store in _tableStores.Values)
            {
                store.Clear();
            }

            SwitchSelectedTableSource();
        }

        /// <summary>
        /// 모든 테이블 데이터는 메모리 저장소에 유지하되, UI 갱신은 선택된 테이블 source만 표시합니다.
        /// </summary>
        private void HandleFramesAdded(List<SimulationFrame> frames, Guid sessionId)
        {
            if (_currentSessionId != sessionId || frames == null || frames.Count == 0)
            {
                return;
            }

            foreach (var frame in frames)
            {
                foreach (var tableName in _configuredFieldsByTable.Keys)
                {
                    var tableData = frame.GetTable(tableName);
                    if (tableData == null)
                    {
                        continue;
                    }

                    var row = CreateStoredRow(frame, tableName, tableData);
                    if (row == null)
                    {
                        continue;
                    }

                    var store = _tableStores.GetOrAdd(tableName, _ => new TablePageStore());
                    store.Append(row);

                    if (string.Equals(tableName, _selectedTableName, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedTableDirty = true;
                    }
                }
            }
        }

        private void OnSelectedTableRefreshTimerTick(object sender, EventArgs e)
        {
            if (!_selectedTableDirty || Items == null)
            {
                return;
            }

            _selectedTableDirty = false;
            Items.RefreshRows();
        }

        /// <summary>
        /// 선택된 테이블이 바뀌면 해당 테이블 전용 PagedAsyncSource를 새로 만듭니다.
        /// 숨은 테이블은 더 이상 UI 컬렉션을 유지하지 않습니다.
        /// </summary>
        private void SwitchSelectedTableSource()
        {
            DisposeCurrentSource();

            if (string.IsNullOrEmpty(_selectedTableName))
            {
                Items = null;
                return;
            }

            Items = CreatePagedSource(_selectedTableName);
            _selectedTableDirty = false;
        }

        private PagedAsyncSource CreatePagedSource(string tableName)
        {
            var source = new PagedAsyncSource
            {
                ElementType = typeof(ExpandoObject),
                PageNavigationMode = PageNavigationMode.ArbitraryWithTotalPageCount
            };

            source.FetchPage += (s, e) =>
            {
                e.Result = FetchPageAsync(tableName, e);
            };

            return source;
        }

        private Task FetchPageAsync(string tableName, FetchPageAsyncEventArgs e)
        {
            var store = _tableStores.GetOrAdd(tableName, _ => new TablePageStore());

            var rows = store.GetPage(e.Skip, e.Take)
                .Select(CreateExpandoRow)
                .Cast<object>()
                .ToArray();

            var hasMoreRows = e.Skip + rows.Length < store.Count;
            return Task.FromResult(new FetchRowsResult(rows, hasMoreRows));
        }

        private void UpdateTableConfig(List<TableConfig> tableConfigs)
        {
            if (tableConfigs == null)
            {
                return;
            }

            _bandCache.Clear();
            _configuredFieldsByTable.Clear();
            _tableStores.Clear();

            foreach (var table in tableConfigs)
            {
                if (table == null || string.IsNullOrEmpty(table.TableObjectName))
                {
                    continue;
                }

                var configuredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var timeColumn = new GridColumnItem
                {
                    FieldName = "Time",
                    Header = "Time"
                };

                var timeBand = new GridBandItem
                {
                    Header = "Time",
                    ColumnItems = new ObservableCollection<GridColumnItem> { timeColumn }
                };

                var bands = new ObservableCollection<GridBandItem> { timeBand };

                foreach (var band in table.Bands)
                {
                    var addBand = new GridBandItem
                    {
                        Header = band.Header
                    };

                    foreach (var column in band.Columns)
                    {
                        addBand.ColumnItems.Add(new GridColumnItem
                        {
                            FieldName = column.FieldName,
                            Header = column.Header
                        });

                        configuredFields.Add(column.FieldName);
                    }

                    bands.Add(addBand);
                }

                _bandCache[table.TableObjectName] = bands;
                _configuredFieldsByTable[table.TableObjectName] = configuredFields;
                _tableStores[table.TableObjectName] = new TablePageStore();
            }

            OnPropertyChanged(nameof(Bands));
            SwitchSelectedTableSource();
        }

        private StoredRow CreateStoredRow(SimulationFrame frame, string tableName, SimulationTable tableData)
        {
            if (!_configuredFieldsByTable.TryGetValue(tableName, out var configuredFields))
            {
                return null;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var fieldName in configuredFields)
            {
                var value = tableData[fieldName];
                if (value != null)
                {
                    values[fieldName] = value;
                }
            }

            return new StoredRow(frame.Time, values);
        }

        private ExpandoObject CreateExpandoRow(StoredRow storedRow)
        {
            var row = new ExpandoObject();
            var dict = (IDictionary<string, object>)row;

            dict["Time"] = storedRow.Time;

            foreach (var pair in storedRow.Values)
            {
                dict[pair.Key] = pair.Value;
            }

            return row;
        }

        private void DisposeCurrentSource()
        {
            if (Items == null)
            {
                return;
            }

            Items.Dispose();
            Items = null;
        }

        public void Dispose()
        {
            _selectedTableRefreshTimer.Stop();
            _selectedTableRefreshTimer.Tick -= OnSelectedTableRefreshTimerTick;

            if (ModelTreeViewModel != null)
            {
                ModelTreeViewModel.OnSelectedModelTreeData -= ModelTreeViewModel_OnSelectedModelTreeData;
            }

            SimulationContext.Instance.OnSessionStarted -= HandleSessionStarted;
            SharedFrameRepository.Instance.OnFramesAdded -= HandleFramesAdded;
            AddSIMManagerHandler.GetInstance().OnScenarioSetupCompleted -= ResultTableAnalyViewModel_OnScenarioSetupCompleted;

            DisposeCurrentSource();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class TablePageStore
        {
            private readonly object _sync = new object();
            private readonly List<StoredRow> _rows = new List<StoredRow>();

            public int Count
            {
                get
                {
                    lock (_sync)
                    {
                        return _rows.Count;
                    }
                }
            }

            public void Clear()
            {
                lock (_sync)
                {
                    _rows.Clear();
                }
            }

            public void Append(StoredRow row)
            {
                lock (_sync)
                {
                    _rows.Add(row);
                }
            }

            public IReadOnlyList<StoredRow> GetPage(int skip, int take)
            {
                lock (_sync)
                {
                    return _rows.Skip(skip).Take(take).ToList();
                }
            }
        }

        private sealed class StoredRow
        {
            public StoredRow(double time, Dictionary<string, object> values)
            {
                Time = time;
                Values = values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            public double Time { get; }

            public Dictionary<string, object> Values { get; }
        }
    }
}
