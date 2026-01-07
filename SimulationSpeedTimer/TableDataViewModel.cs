using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Threading;
using System.Linq;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시나리오 데이터를 관리하고 UI에 바인딩하는 ViewModel
    /// - 시나리오 스키마와 실제 DB 스키마를 비교하여 유효한 데이터만 바인딩
    /// - Lazy Initialization 사용
    /// </summary>
    public class TableDataViewModel
    {
        // --- Fields ---
        private Guid _currentSessionId = Guid.Empty;
        private readonly SynchronizationContext _syncContext;
        
        private string _selectedTableName;
        private bool _isMappingInitialized = false;
        private List<string> _cachedMappings = null; // 유효한 컬럼 이름 리스트
        
        // 시나리오 원본 스키마 정보 (XML 로드 시점의 전체 테이블 명세)
        private SimulationSchema _scenarioSchema;

        // --- Properties ---

        /// <summary>
        /// 사용자가 선택한 테이블 이름
        /// </summary>
        public string SelectedTableName
        {
            get => _selectedTableName;
            set
            {
                if (_selectedTableName != value)
                {
                    _selectedTableName = value;
                    OnTableSelectionChanged();
                }
            }
        }

        /// <summary>
        /// 동적으로 생성된 컬럼 헤더 목록 (UI 바인딩용)
        /// </summary>
        public ObservableCollection<string> Columns { get; } = new ObservableCollection<string>();

        /// <summary>
        /// 데이터 행 (ExpandoObject)
        /// </summary>
        public ObservableCollection<ExpandoObject> Rows { get; } = new ObservableCollection<ExpandoObject>();

        // --- Constructor ---

        public TableDataViewModel()
        {
            _syncContext = SynchronizationContext.Current;

            // Context 라이프사이클 구독
            SimulationContext.Instance.OnSessionStarted += InitializeSession;
            SimulationContext.Instance.OnSessionStopped += CleanupSession;
        }

        // --- Event Handlers & Methods ---

        /// <summary>
        /// 시나리오(XML) 로드 이벤트 핸들러
        /// </summary>
        public void OnScenarioLoaded(SimulationSchema loadedSchema)
        {
            if (loadedSchema == null) return;
            _scenarioSchema = loadedSchema;
            int count = _scenarioSchema.Tables != null ? _scenarioSchema.Tables.Count() : 0;
            Console.WriteLine($"[TableDataViewModel] Scenario information saved. Total Tables: {count}");
        }

        private void InitializeSession(Guid sessionId)
        {
            // 새 세션 시작 시 매핑 초기화 필요
            _isMappingInitialized = false;
            _cachedMappings = null;
            _currentSessionId = sessionId;
            
            // 데이터 수신 구독 시작 (중복 방지)
            SharedFrameRepository.Instance.OnFramesAdded -= HandleNewFrames;
            SharedFrameRepository.Instance.OnFramesAdded += HandleNewFrames;
            
            PostToUi(() => 
            {
                Rows.Clear();
                Columns.Clear();
            });

            Console.WriteLine($"[TableDataViewModel] Session Started: {sessionId}");
        }

        private void CleanupSession()
        {
            // 데이터 수신 구독 해제
            SharedFrameRepository.Instance.OnFramesAdded -= HandleNewFrames;
            Console.WriteLine("[TableDataViewModel] Session Stopped.");
        }

        private void OnTableSelectionChanged()
        {
            // 테이블이 바뀌면 초기화 상태 리셋 -> 다음 프레임 수신 시 다시 검사하게 됨
            _isMappingInitialized = false;
            _cachedMappings = null;

            PostToUi(() =>
            {
                Rows.Clear();
                Columns.Clear();
            });
        }

        private void HandleNewFrames(List<SimulationFrame> frames, Guid sessionId)
        {
            // 세션 ID 검증 (현재 세션 데이터만 처리)
            if (_currentSessionId != sessionId) return;

            // 1. 첫 회 검사 (Lazy Initialization)
            if (!_isMappingInitialized)
            {
                InitializeMapping();
            }

            // 2. 유효하지 않은 테이블이면 스킵 (시나리오에 없거나 DB에 없음)
            if (_cachedMappings == null) return;

            var newRows = new List<dynamic>();

            // 3. 고속 데이터 변환
            var tableInfo = SharedFrameRepository.Instance.Schema?.GetTable(_selectedTableName) 
                            ?? SharedFrameRepository.Instance.Schema?.GetTableByObject(_selectedTableName);
            
            if (tableInfo == null) return; // 방어 코드

            foreach (var frame in frames)
            {
                var tableData = frame.GetTable(tableInfo.TableName);
                if (tableData == null) continue;

                dynamic row = new ExpandoObject();
                var rowDict = (IDictionary<string, object>)row;

                rowDict["Time"] = frame.Time;

                // 캐싱된 유효 컬럼들만 순회
                foreach (var colAttr in _cachedMappings)
                {
                    if (tableInfo.ColumnsByAttributeName.TryGetValue(colAttr, out var colInfo))
                    {
                        var val = tableData[colInfo.ColumnName];
                        if (val != null) rowDict[colAttr] = val;
                    }
                }
                newRows.Add(row);
            }

            if (newRows.Count > 0)
            {
                PostToUi(() => { foreach (var r in newRows) Rows.Add(r); });
            }
        }

        /// <summary>
        /// 첫 프레임 수신 시 딱 한 번 실행되어 유효성을 검증하고 매핑을 생성함.
        /// </summary>
        private void InitializeMapping()
        {
            _isMappingInitialized = true; // 시도 했음을 표시

            if (string.IsNullOrEmpty(_selectedTableName)) return;
            if (_scenarioSchema == null) return; // 시나리오 정보가 아직 없으면 기다림? or 무시? 정책 결정 필요. 일단 무시.

            // 1. 시나리오 검사: 사용자가 선택한 테이블이 시나리오에 정의되어 있는가?
            var scenarioTable = _scenarioSchema.GetTable(_selectedTableName) ?? _scenarioSchema.GetTableByObject(_selectedTableName);
            if (scenarioTable == null)
            {
                Console.WriteLine($"[TableDataViewModel] '{_selectedTableName}' is NOT defined in Scenario XML. Ignoring.");
                return;
            }

            // 2. DB 검사: 실제 DB에 저널링되어 스키마가 존재하는가?
            // (데이터가 들어오기 시작했다는 건 DB 스키마가 로드되었다는 뜻)
            var dbSchema = SharedFrameRepository.Instance.Schema;
            if (dbSchema == null) return; // 아직 준비 안됨

            var dbTable = dbSchema.GetTable(_selectedTableName) ?? dbSchema.GetTableByObject(_selectedTableName);
            if (dbTable == null)
            {
                Console.WriteLine($"[TableDataViewModel] '{_selectedTableName}' is defined in Scenario but NOT found in DB (Not Journaled).");
                return;
            }

            // 3. 매핑 확정 (UI 컬럼 생성)
            // 시나리오에 있는 컬럼 중 DB에도 있는 것만 추림 (교집합)
            var validColumns = new List<string>();
            foreach (var col in scenarioTable.Columns)
            {
                // 속성명 기준 매칭
                if (dbTable.ColumnsByAttributeName.ContainsKey(col.AttributeName))
                {
                    validColumns.Add(col.AttributeName);
                }
            }

            _cachedMappings = validColumns;
            
            // UI 업데이트
            var colsToShow = new List<string> { "Time" };
            colsToShow.AddRange(_cachedMappings);

            PostToUi(() =>
            {
                Columns.Clear();
                foreach (var c in colsToShow) Columns.Add(c);
            });

            Console.WriteLine($"[TableDataViewModel] Mapping Initialized for '{_selectedTableName}'. {validColumns.Count} valid columns.");
        }

        private void PostToUi(Action action)
        {
            if (_syncContext != null) _syncContext.Post(_ => action(), null);
            else action();
        }
    }
}
