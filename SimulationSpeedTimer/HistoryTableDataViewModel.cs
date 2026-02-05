using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.IO;
using System.Threading.Tasks;
using SimulationSpeedTimer.Services;
using System.Windows;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// [사후 분석용] DB에서 전체 이력을 한 번에 로드하여 보여주는 정적 ViewModel입니다.
    /// 기존 TableDataViewModel과 동일한 프로퍼티를 제공하여 View(TableDataView)를 그대로 재사용할 수 있습니다.
    /// 실시간 데이터 수신 기능(Timer, Buffer)은 제거되었습니다.
    /// </summary>
    public class HistoryTableDataViewModel : INotifyPropertyChanged
    {
        // [UI 바인딩 소스] 
        public System.Collections.ObjectModel.ObservableCollection<string> TableNames { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();

        private string _selectedTableName;
        public string SelectedTableName
        {
            get => _selectedTableName;
            set
            {
                if (SetProperty(ref _selectedTableName, value, nameof(SelectedTableName)))
                {
                    OnPropertyChanged(nameof(Columns));
                    OnPropertyChanged(nameof(Items));
                }
            }
        }

        // 선택된 테이블의 컬럼 정보
        public System.Collections.ObjectModel.ObservableCollection<GridColumnItem> Columns
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _columnCache.ContainsKey(_selectedTableName)
                ? _columnCache[_selectedTableName]
                : null;
        }

        // 선택된 테이블의 데이터 행(Row) 리스트
        public System.Collections.ObjectModel.ObservableCollection<ExpandoObject> Items
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _rowCache.ContainsKey(_selectedTableName)
                ? _rowCache[_selectedTableName]
                : null;
        }

        // --- 내부 저장소 ---
        private readonly Dictionary<string, System.Collections.ObjectModel.ObservableCollection<GridColumnItem>> _columnCache
            = new Dictionary<string, System.Collections.ObjectModel.ObservableCollection<GridColumnItem>>();

        private readonly Dictionary<string, System.Collections.ObjectModel.ObservableCollection<ExpandoObject>> _rowCache
            = new Dictionary<string, System.Collections.ObjectModel.ObservableCollection<ExpandoObject>>();

        // 컬렉션 동기화용 락 (한 번에 로드하므로 UI 바인딩 시 충돌 방지용)
        private readonly object _collectionLock = new object();

        private readonly SimulationHistoryService _historyService;

        public HistoryTableDataViewModel()
        {
            _historyService = new SimulationHistoryService();
            InitializeCommands();
        }

        public ICommand ExportCurrentTableCommand { get; private set; }
        public ICommand ExportAllTablesCommand { get; private set; }

        private void InitializeCommands()
        {
            ExportCurrentTableCommand = new RelayCommand(async _ => await ExportCurrentTableAsync());
            ExportAllTablesCommand = new RelayCommand(async _ => await ExportAllTablesAsync());
        }

        private async Task ExportCurrentTableAsync()
        {
            if (string.IsNullOrEmpty(SelectedTableName) || Items == null || Items.Count == 0)
            {
                MessageBox.Show("No data to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var service = new CsvExportService();
            // Default filename: [TableName]_[Timestamp].csv
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{SelectedTableName}_{timestamp}.csv";
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports");
            string fullPath = Path.Combine(folder, fileName);

            try
            {
                // Explicit headers from Columns config
                var headers = Columns.Select(c => c.FieldName).ToList();

                await service.ExportAsync(Items, fullPath, headers);
                MessageBox.Show($"Exported to:\n{fullPath}", "Export Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExportAllTablesAsync()
        {
            if (_rowCache == null || _rowCache.Count == 0)
            {
                MessageBox.Show("No data loaded.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var service = new CsvExportService();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string exportFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", $"Session_{timestamp}");

            int successCount = 0;
            try
            {
                foreach (var tableName in _rowCache.Keys)
                {
                    var items = _rowCache[tableName];
                    if (items == null || items.Count == 0) continue;

                    if (!_columnCache.TryGetValue(tableName, out var colConfigs)) continue;

                    var headers = colConfigs.Select(c => c.FieldName).ToList();
                    string fileName = $"{tableName}.csv";
                    string fullPath = Path.Combine(exportFolder, fileName);

                    await service.ExportAsync(items, fullPath, headers);
                    successCount++;
                }
                MessageBox.Show($"Successfully exported {successCount} tables to:\n{exportFolder}", "Export All Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Batch export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 테이블 및 컬럼 구성을 초기화합니다.
        /// (실시간 모드와 동일한 Config 사용 가능)
        /// </summary>
        public void InitializeTableConfig(List<TableConfig> configs)
        {
            if (configs == null) return;

            TableNames.Clear();
            _columnCache.Clear();
            _rowCache.Clear();

            foreach (var cfg in configs)
            {
                TableNames.Add(cfg.TableName);

                // 1. 데이터 저장소 생성
                var rows = new System.Collections.ObjectModel.ObservableCollection<ExpandoObject>();
                BindingOperations.EnableCollectionSynchronization(rows, _collectionLock);
                _rowCache[cfg.TableName] = rows;

                // 2. 컬럼 구성: Time + 설정값
                var cols = new List<GridColumnItem> { new GridColumnItem { FieldName = "Time", HeaderText = "Time" } };
                if (cfg.Columns != null)
                {
                    cols.AddRange(cfg.Columns.Select(c => new GridColumnItem { FieldName = c.FieldName, HeaderText = c.Header }));
                }
                _columnCache[cfg.TableName] = new System.Collections.ObjectModel.ObservableCollection<GridColumnItem>(cols);
            }

            if (TableNames.Count > 0) SelectedTableName = TableNames[0];
        }

        /// <summary>
        /// 지정된 DB 파일에서 데이터를 로드하여 뷰모델에 채웁니다.
        /// </summary>
        public void LoadHistoryData(string dbPath, double startTime = 0, double endTime = double.MaxValue)
        {
            // 1. 데이터 로드 (전체 프레임)
            var frames = _historyService.LoadFullHistory(dbPath, startTime, endTime);

            // 2. 기존 데이터 클리어
            foreach (var list in _rowCache.Values)
            {
                list.Clear();
            }

            // 3. 시간 순으로 데이터 채우기
            // LoadFullHistory는 Dictionary<double, Frame>을 반환하므로 시간 순 정렬 필요
            var sortedTimes = frames.Keys.OrderBy(t => t).ToList();

            foreach (var time in sortedTimes)
            {
                var frame = frames[time];

                foreach (var tableData in frame.AllTables)
                {
                    string tName = tableData.TableName;

                    // 구성(Config)에 존재하는 테이블인 경우에만 추가
                    if (_rowCache.TryGetValue(tName, out var targetCollection))
                    {
                        var row = CreateExpandoRow(frame, tableData);
                        targetCollection.Add(row);
                    }
                }
            }

            // UI 갱신 알림
            OnPropertyChanged(nameof(Items));
        }

        private ExpandoObject CreateExpandoRow(SimulationFrame frame, SimulationTable tableData)
        {
            // ExpandoObject는 IDictionary<string, object> 인터페이스를 구현함
            var row = new ExpandoObject();
            var dict = (IDictionary<string, object>)row;

            dict["Time"] = frame.Time;
            foreach (var colName in tableData.ColumnNames)
            {
                dict[colName] = tableData[colName];
            }
            return row;
        }

        // --- INotifyPropertyChanged Implementation ---
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
