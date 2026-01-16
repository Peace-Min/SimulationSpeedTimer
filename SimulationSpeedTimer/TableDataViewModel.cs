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
        }

        // --- 외부 주입 메서드 ---

        public void InitializeTableConfig(List<TableConfig> configs)
        {
            if (configs == null) return;

            TableNames.Clear();
            _columnCache.Clear();
            _rowCache.Clear();

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
            
            // 데이터 초기화 (구조는 유지)
            foreach(var kvp in _rowCache)
            {
                kvp.Value.Clear();
            }

            SharedFrameRepository.Instance.OnFramesAdded -= HandleFramesAdded;
            SharedFrameRepository.Instance.OnFramesAdded += HandleFramesAdded;
        }

        private void HandleSessionStopped()
        {
            SharedFrameRepository.Instance.OnFramesAdded -= HandleFramesAdded;
            // 락 해제 등이 필요한 경우 여기서 처리 (보통 GC에 맡김)
        }

        private void HandleFramesAdded(List<SimulationFrame> frames, Guid sessionId)
        {
            if (_currentSessionId != sessionId) return;

            // [Step 1: Background Thread] 데이터 변환 및 배치 준비
            // UI 스레드 개입 없이 순수 CPU 연산으로 데이터를 미리 가공합니다.
            // Key: TableName, Value: 추가할 Row 리스트
            var batchBuffer = new Dictionary<string, List<System.Dynamic.ExpandoObject>>();
            
            // 관리 중인 테이블 키 미리 확보 (Thread-Safe Access pattern 필요 시 복사 사용)
            var targetTables = _rowCache.Keys.ToList();
            foreach(var tb in targetTables) batchBuffer[tb] = new List<System.Dynamic.ExpandoObject>();

            foreach (var frame in frames)
            {
                foreach (var tableName in targetTables)
                {
                    var tableData = frame.GetTable(tableName);
                    if (tableData != null)
                    {
                        // Row 객체 생성 (ExpandoObject)
                        var row = new System.Dynamic.ExpandoObject();
                        var dict = (IDictionary<string, object>)row;

                        // 1. Time
                        dict["Time"] = frame.Time;

                        // 2. Columns
                        foreach (var colName in tableData.ColumnNames)
                        {
                            dict[colName] = tableData[colName];
                        }

                        // 3. 임시 버퍼에 저장 (메인 스레드 호출 아님)
                        batchBuffer[tableName].Add(row);
                    }
                }
            }

            // [Step 2: UI Thread] 일괄 업데이트 (Batch Update)
            // 데이터가 있는 경우에만, 단 한 번의 Dispatcher 호출로 처리합니다.
            // 이렇게 하면 N개의 마샬링 비용이 1로 줄어들어 UI 끊김이 사라집니다.
            bool hasData = batchBuffer.Any(x => x.Value.Count > 0);
            if (hasData)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // UI 스레드 내부 진입
                    foreach (var kvp in batchBuffer)
                    {
                        var rowsToAdd = kvp.Value;
                        if (rowsToAdd.Count == 0) continue;

                        if (_rowCache.TryGetValue(kvp.Key, out var targetCollection))
                        {
                            // 이미 UI 스레드이므로 Lock 없이도 안전하나, 
                            // EnableCollectionSynchronization과의 호환성을 위해 Lock 유지 또는 그대로 Add
                            // 성능을 위해 루프만 돕니다. (UI 스레드 로컬 작업이라 매우 빠름)
                            foreach (var item in rowsToAdd)
                            {
                                targetCollection.Add(item);
                            }
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
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
