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
        public Action<double, double, double> OnDataUpdated; 

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

        // OnTimeReceived 제거됨: 시간 공급은 외부에서 GlobalDataService.Instance.EnqueueTime()으로 직접 수행

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
            // SimulationFrame은 특정 시간 T에 대한 모든 테이블의 데이터 스냅샷입니다.
            // GlobalDataService에서 이미 논리적 이름(ObjectName, AttributeName)으로 변환되어 저장되므로
            // 별도의 메타데이터 매핑 없이 설정(Config)값 그대로 조회하면 됩니다.

            foreach (var config in _configs)
            {
                // Inner Join Logic:
                // X축 데이터와 Y축 데이터가 *모두* 존재해야만 유효한 시뮬레이션 데이터로 간주합니다.
                
                // Config에 있는 논리적 이름(예: SAM001, Velocity)으로 직접 조회
                double? xVal = GetValue(frame, config.XAxisObjectName, config.XAxisAttributeName);
                double? yVal = GetValue(frame, config.YAxisObjectName, config.YAxisAttributeName);

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
