using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 저장된 DB 파일에서 전체 이력을 로드하여 확인하기 위한 테스트용 ViewModel입니다.
    /// TableDataViewModel과 유사한 구조를 가지지만, 실시간 스트리밍 대신 일괄 로드 방식을 사용합니다.
    /// </summary>
    public class HistoryPlaybackViewModel : INotifyPropertyChanged, IDisposable
    {
        private void OnPropertyChanged(string v)
        {
            throw new NotImplementedException();
        }

        // [UI 바인딩 소스]
        public ObservableCollection<string> TableNames { get; } = new ObservableCollection<string>();

        private string _selectedTableName;
        public string SelectedTableName
        {
            get => _selectedTableName;
            set
            {
                if (_selectedTableName != value)
                {
                    _selectedTableName = value;
                    OnPropertyChanged(nameof(SelectedTableName));
                    OnPropertyChanged(nameof(Columns));
                    OnPropertyChanged(nameof(Items));
                }
            }
        }

        public ObservableCollection<GridColumnItem> Columns
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _columnCache.ContainsKey(_selectedTableName)
                ? _columnCache[_selectedTableName]
                : null;
        }

        public ObservableCollection<ExpandoObject> Items
        {
            get => !string.IsNullOrEmpty(_selectedTableName) && _rowCache.ContainsKey(_selectedTableName)
                ? _rowCache[_selectedTableName]
                : null;
        }

        // 내부 데이터 저장소
        private readonly Dictionary<string, ObservableCollection<GridColumnItem>> _columnCache 
            = new Dictionary<string, ObservableCollection<GridColumnItem>>();

        private readonly Dictionary<string, ObservableCollection<ExpandoObject>> _rowCache
            = new Dictionary<string, ObservableCollection<ExpandoObject>>();

        private readonly object _collectionLock = new object();
        private List<TableConfig> _configs;

        private bool _isLoadingActive = false; // 플래그 추가

        // [UI 최적화] 백그라운드 로딩 및 스로틀링을 위한 구성요소
        // 튜플로 테이블 이름과 데이터를 함께 저장하여 라우팅 처리
        private readonly ConcurrentQueue<(string TableName, ExpandoObject Row)> _pendingBuffer 
            = new ConcurrentQueue<(string TableName, ExpandoObject Row)>();
        private readonly DispatcherTimer _uiRefreshTimer;
        private CancellationTokenSource _loadCts;

        public event PropertyChangedEventHandler PropertyChanged;

        public HistoryPlaybackViewModel(List<TableConfig> configs)
        {
            _configs = configs ?? new List<TableConfig>();
            
            // UI 갱신 타이머 설정
            // 33ms Interval = 약 30fps 목표
            // Background 우선순위로 UI 반응성 확보
            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(33); 
            _uiRefreshTimer.Tick += OnUIRefreshTimerTick;
            
            InitializeStructure();
        }

        private void InitializeStructure()
        {
            TableNames.Clear();
            _columnCache.Clear();
            _rowCache.Clear();

            foreach (var cfg in _configs)
            {
                TableNames.Add(cfg.TableName);

                // 1. 행 데이터 컬렉션 생성
                var rows = new ObservableCollection<ExpandoObject>();
                // BindingOperations.EnableCollectionSynchronization은
                // 백그라운드 스레드에서 직접 Add할 때 필수지만,
                // 여기서는 DispatcherTimer(UI 스레드)에서 Add하므로 엄밀히는 선택사항.
                // 하지만 안전을 위해 유지합니다.
                BindingOperations.EnableCollectionSynchronization(rows, _collectionLock);
                _rowCache[cfg.TableName] = rows;

                // 2. 컬럼 구성
                var cols = new List<GridColumnItem> { new GridColumnItem { FieldName = "Time", HeaderText = "Time" } };
                if (cfg.Columns != null)
                {
                    cols.AddRange(cfg.Columns.Select(c => new GridColumnItem { FieldName = c.FieldName, HeaderText = c.Header }));
                }
                _columnCache[cfg.TableName] = new ObservableCollection<GridColumnItem>(cols);
            }

            if (TableNames.Count > 0)
                SelectedTableName = TableNames[0];
        }

        /// <summary>
        /// 비동기 방식으로 DB 데이터를 스트리밍 로드합니다. (Non-blocking)
        /// * Design Policy: 이 ViewModel은 일회용(One-Shot)으로 사용되는 것을 권장합니다.
        /// * 새로운 설정(Config)이 필요하다면 새로운 ViewModel 인스턴스를 생성하십시오.
        /// </summary>
        /// <param name="dbPath">SQLite DB 경로</param>
        public async Task LoadAsync(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return;

            // 1. 취소 토큰 생성 (Dispose 시 작업을 중단하기 위함)
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            // 2. 상태 초기화 (UI 스레드)
            // 인스턴스를 새로 만들면 불필요하지만, 만에 하나 재사용 시 안전장치
            foreach (var rows in _rowCache.Values) rows.Clear();
            
            // 큐 비우기
            lock(_pendingBuffer) { while (_pendingBuffer.TryDequeue(out _)); }

            // Consumer 시작
            _isLoadingActive = true;
            _uiRefreshTimer.Start();

            // 캡처된 설정 사용
            var currentConfigs = _configs;

            try
            {
                // 3. (Step 1) DB 로드 & 전처리 (Background)
                Dictionary<double, SimulationFrame> frames = null;
                List<double> sortedTimes = null;

                await Task.Run(() =>
                {
                    var historyService = new SimulationHistoryService();
                    frames = historyService.LoadFullHistory(dbPath, 0, double.MaxValue);

                    if (token.IsCancellationRequested) return;

                    // 정렬만 미리 수행
                    sortedTimes = frames.Keys.OrderBy(t => t).ToList();
                }, token);

                if (token.IsCancellationRequested || frames == null) return;

                // 4. (Step 2) 큐잉 작업 (Background)
                await Task.Run(() =>
                {
                    foreach (var time in sortedTimes)
                    {
                        if (token.IsCancellationRequested) break;
                        var frame = frames[time];

                        foreach (var cfg in currentConfigs)
                        {
                            var tableName = cfg.TableName;
                            var tableData = frame.GetTable(tableName);

                            if (tableData != null)
                            {
                                var row = CreateExpandoRow(frame.Time, tableData);
                                _pendingBuffer.Enqueue((tableName, row));
                            }
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"LoadAsync Error: {ex.Message}");
            }
            finally
            {
                _isLoadingActive = false;
            }
        }

        private void OnUIRefreshTimerTick(object sender, EventArgs e)
        {
            if (_pendingBuffer.IsEmpty) return;

            // 1. 테이블별 배치 데이터 바구니 (Dictionary로 그룹화)
            var batchData = new Dictionary<string, List<ExpandoObject>>();
    
            // [성능 튜닝] AddRange를 쓰므로 한 틱당 2,000~3,000개도 충분히 가볍습니다.
            int maxProcessPerTick = 2500; 
            int processed = 0;

            // 2. 큐에서 데이터 추출 및 그룹화
            while (processed < maxProcessPerTick && _pendingBuffer.TryDequeue(out var item))
            {
                if (!batchData.ContainsKey(item.TableName))
                    batchData[item.TableName] = new List<ExpandoObject>();
                
                batchData[item.TableName].Add(item.Row);
                processed++;
            }

            // 3. [최적화 핵심] AddRange를 사용하여 테이블별로 '단 한 번의 UI 통지' 발생
            foreach (var kvp in batchData)
            {
                if (_rowCache.TryGetValue(kvp.Key, out var targetCollection))
                {
                    // ObservableCollectionCore의 AddRange 활용
                    targetCollection.AddRange(kvp.Value); 
                }
            }

            // 모든 처리가 완료되었고, 더 이상 들어올 데이터가 없다면 타이머 중지
            if (_pendingBuffer.IsEmpty && !_isLoadingActive) 
            {
                _uiRefreshTimer.Stop();
            }
        }

        public void Dispose()
        {
            // 리소스 정리 (MainViewModel에서 인스턴스 교체 시 호출)
            _uiRefreshTimer?.Stop();
            _loadCts?.Cancel();
            _loadCts?.Dispose();

            if (_uiRefreshTimer != null)
            {
                _uiRefreshTimer.Stop();
                _uiRefreshTimer.Tick -= OnUIRefreshTimerTick; // [필수] 이벤트 핸들러 해제
            }

            // [중요] 대용량 컬렉션 명시적 클리어
            // GC에게 이 메모리 블록들이 이제 필요 없음을 명확히 알립니다.
            _rowCache.Clear();
            _columnCache.Clear();
        }
    }
}
