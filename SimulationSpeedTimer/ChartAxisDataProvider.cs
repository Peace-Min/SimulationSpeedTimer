using System;
using System.Collections.Generic;
using System.Linq;

namespace SimulationSpeedTimer
{
    public class ChartAxisDataProvider
    {
        // Singleton Instance
        private static ChartAxisDataProvider _instance;
        public static ChartAxisDataProvider Instance => _instance ?? (_instance = new ChartAxisDataProvider());

        private Guid _currentSessionId;
        private List<DatabaseQueryConfig> _configs = new List<DatabaseQueryConfig>();

        // UI 갱신 델리게이트 (외부에서 설정)
        // Time, X-Value, Y-Value, Z-Value (Optional)
        // 2D인 경우 Z는 null이 됩니다.
        public Action<double, double, double, double?> OnDataUpdated;

        private ChartAxisDataProvider()
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

        private void HandleNewFrames(List<SimulationFrame> frames, Guid sessionId)
        {
            // 1. 세션 ID 검증 (Lifecycle Isolation)
            if (sessionId != _currentSessionId) return;

            // 3. 데이터 처리 및 UI 업데이트
            foreach (var frame in frames)
            {
                ProcessFrame(frame);
            }
        }

        private void ProcessFrame(SimulationFrame frame)
        {
            foreach (var config in _configs)
            {
                // 1. Fetching (Raw Data) - 있는 그대로 가져옴
                double? xVal = null;
                if (config.IsXAxisTime)
                {
                    xVal = frame.Time;
                }
                else
                {
                    xVal = GetValue(frame, config.XColumn.ObjectName, config.XColumn.AttributeName);
                }

                double? yVal = GetValue(frame, config.YColumn.ObjectName, config.YColumn.AttributeName);

                double? zVal = null;
                if (config.Is3DMode)
                {
                    zVal = GetValue(frame, config.ZColumn.ObjectName, config.ZColumn.AttributeName);
                }

                // 2. Policy Application (Business Logic) - 여기서 결정함
                if (config.IsXAxisTime)
                {
                    // 정책: 시간축이면 관대하게 처리 (데이터 없으면 NaN으로라도 진행)
                    if (!yVal.HasValue) yVal = double.NaN;

                    // 3D 모드이면서 Z값이 없는 경우에도 NaN 처리
                    if (config.Is3DMode && !zVal.HasValue) zVal = double.NaN;
                }
                else
                {
                    // 정책: 데이터축이면 엄격하게 처리 (데이터 없으면 스킵)
                    if (!xVal.HasValue) continue;
                    if (!yVal.HasValue) continue;
                    if (config.Is3DMode && !zVal.HasValue) continue;
                }

                // X값이 여전히 없다면 스킵 (xVal은 위에서 Fallback 처리되지 않으므로 필수)
                if (!xVal.HasValue) continue;

                // 3. Dispatch
                OnDataUpdated?.Invoke(frame.Time, xVal.Value, yVal.Value, zVal);
            }
        }

        private double? GetValue(SimulationFrame frame, string tableName, string colName)
        {
            if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(colName)) return null;

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
