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
        // Time, X-Value, Y-Values (Key: SeriesName)
        public Action<double, double, Dictionary<string, double>> OnDataUpdated; 

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
                // 1. X축 데이터 조회 (필수)
                // X축 SeriesItem 사용
                double? xVal = GetValue(frame, config.XAxisSeries.ObjectName, config.XAxisSeries.AttributeName);

                if (!xVal.HasValue) continue; // X값이 없으면 해당 시점은 Skip

                // 2. Y축 다중 시리즈 조회
                var yValues = new Dictionary<string, double>();
                
                // X축이 시간(s_time)을 나타내는지 확인 (DB 스키마 상 보통 s_time이 시간 컬럼)
                // 혹은 사용자가 config에 's_time'이라고 명시했을 경우를 상정
                bool isXAxisTime = config.XAxisSeries.AttributeName.Equals("s_time", StringComparison.OrdinalIgnoreCase) 
                                   || config.XAxisSeries.AttributeName.Equals("Time", StringComparison.OrdinalIgnoreCase);

                foreach(var series in config.YAxisSeries)
                {
                    // 키 생성: SeriesName이 있으면 사용하고, 없으면 Obj.Attr 형식으로 생성
                    string key = !string.IsNullOrEmpty(series.SeriesName) 
                        ? series.SeriesName 
                        : $"{series.ObjectName}.{series.AttributeName}";

                    double? yVal = GetValue(frame, series.ObjectName, series.AttributeName);
                    
                    if (yVal.HasValue)
                    {
                        yValues[key] = yVal.Value;
                    }
                    else if (isXAxisTime)
                    {
                        // [요청사항 반영]
                        // 데이터가 없는데(else), X축이 시간축(s_time)이라면 
                        // 연속성을 위해 NaN으로라도 채워서 보냅니다.
                        yValues[key] = double.NaN;
                    }
                }

                // Inner Join Logic Variant:
                // X축은 필수이고, Y축 데이터가 *하나라도* 존재(NaN 포함)하면 업데이트를 발생시킵니다.
                if (yValues.Count > 0)
                {
                    OnDataUpdated?.Invoke(frame.Time, xVal.Value, yValues);
                }
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
