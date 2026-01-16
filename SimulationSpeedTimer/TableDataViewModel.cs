using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 저장소로부터 수신되는 프레임을 변환 없이 그대로 캐싱하는 ViewModel
    /// </summary>
    public class TableDataViewModel
    {
        private Guid _currentSessionId = Guid.Empty;

        // [UI 바인딩 소스] 
        // 테이블 목록 (콤보박스용)
        public System.Collections.ObjectModel.ObservableCollection<string> TableNames { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();

        // 현재 선택된 테이블 이름
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

        // [동적 바인딩 프로퍼티] 
        // 선택된 테이블의 행(Row) 컬렉션 반환
        public System.Collections.ObjectModel.ObservableCollection<GridColumnItem> Columns
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _columnCache.ContainsKey(_selectedTableName)
                ? _columnCache[_selectedTableName]
                : null;
        }

        public System.Collections.ObjectModel.ObservableCollection<System.Dynamic.ExpandoObject> Items
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _rowCache.ContainsKey(_selectedTableName)
                ? _rowCache[_selectedTableName]
                : null;
        }

        // --- 내부 캐시 저장소 ---
        private readonly Dictionary<string, System.Collections.ObjectModel.ObservableCollection<GridColumnItem>> _columnCache
            = new Dictionary<string, System.Collections.ObjectModel.ObservableCollection<GridColumnItem>>();

        // [핵심 데이터 저장소] Key: 테이블명, Value: 동적 행 데이터 리스트 (ExpandoObject)
        // 화면에 표시될 모든 데이터가 여기에 누적됨.
        private readonly Dictionary<string, System.Collections.ObjectModel.ObservableCollection<System.Dynamic.ExpandoObject>> _rowCache
            = new Dictionary<string, System.Collections.ObjectModel.ObservableCollection<System.Dynamic.ExpandoObject>>();

        // 컬렉션 동기화용 락 객체 (멀티스레드 Add 지원)
        private readonly object _collectionLock = new object();

        public TableDataViewModel()
        {
            SimulationContext.Instance.OnSessionStarted += HandleSessionStarted;
            SimulationContext.Instance.OnSessionStopped += HandleSessionStopped;

            // [이벤트 구독] 뷰모델 수명주기 내내 유지
            SharedFrameRepository.Instance.OnFramesAdded += HandleFramesAdded;

            // [UI 최적화] 렌더링 부하를 줄이기 위한 Throttling Timer (10Hz)
            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Render);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(50);
            _uiRefreshTimer.Tick += OnUIRefreshTimerTick;
            _uiRefreshTimer.Start();
        }

        // --- 외부 주입 메서드 ---

        public void InitializeTableConfig(List<TableConfig> configs)
        {
            if (configs == null) return;

            TableNames.Clear();
            _columnCache.Clear();
            _rowCache.Clear();
            _pendingBuffer.Clear(); // 설정 초기화 시 버퍼도 비움

            foreach (var cfg in configs)
            {
                TableNames.Add(cfg.TableName);

                // 1. 행 데이터 컬렉션 생성
                var rows = new System.Collections.ObjectModel.ObservableCollection<System.Dynamic.ExpandoObject>();

                // WPF 백그라운드 스레드 업데이트 지원 설정
                BindingOperations.EnableCollectionSynchronization(rows, _collectionLock);

                _rowCache[cfg.TableName] = rows;

                // 2. 컬럼 구성: Time(고정) + 설정값
                var cols = new List<GridColumnItem> { new GridColumnItem { FieldName = "Time", HeaderText = "Time" } };
                if (cfg.Columns != null)
                {
                    cols.AddRange(cfg.Columns.Select(c => new GridColumnItem { FieldName = c.FieldName, HeaderText = c.Header }));
                }
                _columnCache[cfg.TableName] = new System.Collections.ObjectModel.ObservableCollection<GridColumnItem>(cols);
            }

            if (TableNames.Count > 0) SelectedTableName = TableNames[0];
        }

        public void UpdateColumnConfig(string tableName, List<ColumnConfig> newColumns)
        {
            if (string.IsNullOrEmpty(tableName) || !_columnCache.TryGetValue(tableName, out var colCollection)) return;

            colCollection.Clear();
            colCollection.Add(new GridColumnItem { FieldName = "Time", HeaderText = "Time" });

            if (newColumns != null)
            {
                foreach (var c in newColumns)
                {
                    colCollection.Add(new GridColumnItem { FieldName = c.FieldName, HeaderText = c.Header });
                }
            }
        }

        private void HandleSessionStarted(Guid sessionId)
        {
            _currentSessionId = sessionId;

            // [UI 초기화] 새 세션 시작 시 이전 데이터 잔재(Buffer & UI)를 완벽히 소거

            // 1. 대기열(Buffer) 비우기
            foreach (var queue in _pendingBuffer.Values)
            {
                while (queue.TryDequeue(out _)) ;
            }
            _pendingBuffer.Clear();

            // 2. 화면(Grid) 비우기
            foreach (var collection in _rowCache.Values)
            {
                collection.Clear();
            }
        }

        private void HandleSessionStopped()
        {
            // [잔여 데이터 수신 허용]
            // Stop 버튼을 눌러도 GlobalDataService가 마지막 데이터를 보낼 수 있으므로
            // 여기서 이벤트 핸들러를 해제하지 않습니다. (_currentSessionId는 그대로 유지)
        }

        // [안티그래비티 패턴] 백그라운드 데이터 버퍼 & UI 갱신 타이머
        private readonly ConcurrentDictionary<string, ConcurrentQueue<System.Dynamic.ExpandoObject>> _pendingBuffer = new ConcurrentDictionary<string, ConcurrentQueue<System.Dynamic.ExpandoObject>>();
        private readonly DispatcherTimer _uiRefreshTimer;



        private void HandleFramesAdded(List<SimulationFrame> frames, Guid sessionId)
        {
            if (_currentSessionId != sessionId) return;

            // [Step 1: Background Thread] Non-blocking Output
            // UI 스레드를 괴롭히지 않고, 데이터를 메모리 큐에 적재만 합니다.
            foreach (var frame in frames)
            {
                // 현재 관리 중인 테이블에 대해서만 처리
                foreach (var tableName in _rowCache.Keys)
                {
                    var tableData = frame.GetTable(tableName);
                    if (tableData != null)
                    {
                        var row = CreateExpandoRow(frame, tableData);

                        // 큐에 안전하게 투입 (Lock-free)
                        var queue = _pendingBuffer.GetOrAdd(tableName, _ => new ConcurrentQueue<System.Dynamic.ExpandoObject>());
                        queue.Enqueue(row);
                    }
                }
            }
        }

        private System.Dynamic.ExpandoObject CreateExpandoRow(SimulationFrame frame, SimulationTable tableData)
        {
            var row = new System.Dynamic.ExpandoObject();
            var dict = (IDictionary<string, object>)row;

            dict["Time"] = frame.Time;
            foreach (var colName in tableData.ColumnNames)
            {
                dict[colName] = tableData[colName];
            }
            return row;
        }

        // [Step 2: UI Thread] Periodic Batch Update
        private void OnUIRefreshTimerTick(object sender, EventArgs e)
        {
            // 쌓인 데이터가 없으면 빠른 리턴
            if (_pendingBuffer.IsEmpty || _pendingBuffer.Values.All(q => q.IsEmpty)) return;

            foreach (var kvp in _pendingBuffer)
            {
                if (kvp.Value.IsEmpty) continue;

                if (_rowCache.TryGetValue(kvp.Key, out var targetCollection))
                {
                    // [Batch Add] 
                    // 한 번의 타이머 틱 동안 쌓인 모든 데이터를 해당 컬렉션에 털어넣습니다.
                    // UI 스레드에서 직접 수행되므로 별도의 잠금 없이도 안전하지만, 
                    // BindingOperations.EnableCollectionSynchronization을 사용 중이라면 내부적으로 잠금이 걸릴 수 있습니다.
                    while (kvp.Value.TryDequeue(out var item))
                    {
                        targetCollection.Add(item);
                    }
                }
            }
        }

        // --- INotifyPropertyChanged Implementation ---
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// [UI 전용 모델] GridControl 컬럼 바인딩용
    /// </summary>
    public class GridColumnItem
    {
        public string HeaderText { get; set; }
        public string FieldName { get; set; }
    }

    /// <summary>
    /// 외부에서 주입하는 테이블 설정 DTO
    /// </summary>
    public class TableConfig
    {
        public string TableName { get; set; }
        public List<ColumnConfig> Columns { get; set; }
    }

    /// <summary>
    /// 외부에서 주입하는 컬럼 설정 DTO
    /// </summary>
    public class ColumnConfig
    {
        public string FieldName { get; set; }  // DB 컬럼명 (데이터 매핑용)
        public string Header { get; set; }     // UI 헤더 표시용
    }
}
