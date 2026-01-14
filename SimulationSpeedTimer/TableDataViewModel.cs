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

            // [데이터 변환 및 분배 로직]
            // 수신된 프레임을 테이블별로 쪼개서 각 _rowCache에 넣음
            foreach (var frame in frames)
            {
                // 현재 관리 중인 모든 테이블에 대해 반복
                foreach (var kvp in _rowCache) 
                {
                    string tableName = kvp.Key;
                    var rowCollection = kvp.Value;

                    // 해당 프레임에 이 테이블 데이터가 있나?
                    var tableData = frame.GetTable(tableName);
                    
                    // 데이터가 없어도 Time은 찍어줘야 한다면 로직 변경 필요. 
                    // 여기서는 데이터가 있을 때만 행 추가.
                    if (tableData != null)
                    {
                        var row = new System.Dynamic.ExpandoObject();
                        var dict = (IDictionary<string, object>)row;

                        // 1. Time (기본)
                        dict["Time"] = frame.Time;

                        // 2. 컬럼 데이터 매핑
                        foreach (var colName in tableData.ColumnNames)
                        {
                            dict[colName] = tableData[colName];
                        }

                        // 3. 컬렉션 추가 (Lock에 의해 스레드 안전)
                        lock (_collectionLock)
                        {
                            rowCollection.Add(row);
                        }
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
