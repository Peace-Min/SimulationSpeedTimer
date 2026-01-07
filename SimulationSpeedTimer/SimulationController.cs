using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    public class SimulationController
    {
        // Singleton Instance
        private static SimulationController _instance;
        public static SimulationController Instance => _instance ?? (_instance = new SimulationController());

        private Guid _currentSessionId;
        private List<ResolvedQueryInfo> _resolvedQueries = new List<ResolvedQueryInfo>();
        private List<DatabaseQueryConfig> _configs = new List<DatabaseQueryConfig>();
        private bool _isResolved = false;

        // UI 갱신 델리게이트 (외부에서 설정)
        public Action<double, double, double> OnDataUpdated; 

        private SimulationController()
        {
            // Context 라이프사이클 구독
            SimulationContext.Instance.OnSessionStarted += OnSessionStarted;
            SimulationContext.Instance.OnSessionStopped += OnSessionStopped;
            // Repository 구독은 OnSessionStarted에서 동적으로 수행
        }

        public void AddConfig(DatabaseQueryConfig config)
        {
            _configs.Add(config);
        }

        private void OnSessionStarted(Guid sessionId)
        {
            _currentSessionId = sessionId;
            _resolvedQueries.Clear();
            _isResolved = false;
            // _processedCount = 0; // These variables are not defined in the provided code.
            // _lastTime = 0; // These variables are not defined in the provided code.
            Console.WriteLine($"[SimulationController] Session Started: {_currentSessionId}");

            // 동적 구독 시작
            SharedFrameRepository.Instance.OnFramesAdded -= HandleNewFrames; // 중복 방지
            SharedFrameRepository.Instance.OnFramesAdded += HandleNewFrames;
        }

        private void OnSessionStopped()
        {
            // 동적 구독 해제
            SharedFrameRepository.Instance.OnFramesAdded -= HandleNewFrames;
            Console.WriteLine("[SimulationController] Session Stopped. Detached from Repository.");
            _currentSessionId = Guid.Empty;
            // 필요한 경우 내부 자원 정리
        }

        // OnTimeReceived 제거됨: 시간 공급은 외부에서 GlobalDataService.Instance.EnqueueTime()으로 직접 수행

        private void HandleNewFrames(List<SimulationFrame> frames, Guid sessionId)
        {
            // 1. 세션 ID 검증 (Lifecycle Isolation)
            if (sessionId != _currentSessionId) return;

            // 2. 메타데이터 해석 (Lazy Load)
            if (!_isResolved)
            {
                if (SharedFrameRepository.Instance.Schema != null)
                {
                    ResolveMetadata();
                    _isResolved = true;
                }
                else return; 
            }

            // 3. 데이터 처리 및 UI 업데이트
            foreach (var frame in frames)
            {
                ProcessFrame(frame);
            }
        }

        private void ResolveMetadata()
        {
            var schema = SharedFrameRepository.Instance.Schema;
            _resolvedQueries.Clear();

            Console.WriteLine("[SimulationController] Resolving Metadata...");

            foreach (var config in _configs)
            {
                // X축 매핑
                var tableX = schema.Tables.FirstOrDefault(t => t.ObjectName == config.XAxisObjectName);
                string colX = FindColumn(tableX, config.XAxisAttributeName);

                // Y축 매핑
                var tableY = schema.Tables.FirstOrDefault(t => t.ObjectName == config.YAxisObjectName);
                string colY = FindColumn(tableY, config.YAxisAttributeName);

                if (colX != null && colY != null)
                {
                    _resolvedQueries.Add(new ResolvedQueryInfo
                    {
                        XTableName = tableX.TableName,
                        XColumnName = colX,
                        YTableName = tableY.TableName,
                        YColumnName = colY,
                        OriginalConfig = config
                    });
                    Console.WriteLine($"[Mapped] {config.XAxisObjectName}.{config.XAxisAttributeName}({tableX.TableName}.{colX}) & {config.YAxisObjectName}.{config.YAxisAttributeName}({tableY.TableName}.{colY})");
                }
            }
        }

        private string FindColumn(SchemaTableInfo table, string logicalAttr)
        {
            if (table == null) return null;
            var col = table.Columns.FirstOrDefault(c => c.AttributeName == logicalAttr);
            if (col == null)
            {
                Console.WriteLine($"[Warn] Attribute '{logicalAttr}' not found in table '{table.TableName}'.");
                return null;
            }
            return col.ColumnName;
        }

        private void ProcessFrame(SimulationFrame frame)
        {
            // SimulationFrame은 특정 시간 T에 대한 모든 테이블의 데이터 스냅샷입니다.
            // 따라서 frame 안에 데이터가 존재한다는 것 자체가 시간 동기화(Join by Time)가 보장됨을 의미합니다.

            foreach (var query in _resolvedQueries)
            {
                // Inner Join Logic:
                // X축 데이터와 Y축 데이터가 *모두* 존재해야만 유효한 시뮬레이션 데이터로 간주합니다.
                // 서로 다른 테이블에 있더라도 SimulationFrame 구조상 같은 Time Key를 공유합니다.

                double? xVal = GetValue(frame, query.XTableName, query.XColumnName);
                double? yVal = GetValue(frame, query.YTableName, query.YColumnName);

                // 둘 중 하나라도 없으면 과감히 Skip (Partial Data 무시)
                if (xVal.HasValue && yVal.HasValue)
                {
                    OnDataUpdated?.Invoke(frame.Time, xVal.Value, yVal.Value);
                }
                // else: 이 시간 프레임에는 해당 객체 데이터가 불완전함 (ex: 센서 A는 있는데 B는 없음)
            }
        }

        private double? GetValue(SimulationFrame frame, string tableName, string colName)
        {
            var table = frame.GetTable(tableName);
            if (table != null)
            {
                object val = table[colName];
                if (val != null)
                {
                    try { return Convert.ToDouble(val); } catch { }
                }
            }
            return null;
        }
    }
}
