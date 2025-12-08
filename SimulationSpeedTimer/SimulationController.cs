using System;
using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시뮬레이션 전체를 관장하는 정적 컨트롤러
    /// </summary>
    public static class SimulationController
    {
        private static SimulationQueryManager _queryManager;
        private static bool _isInitialized = false;

        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// 정적 이벤트이므로 Stop() 시에도 null로 초기화하면 안 됩니다!
        /// </summary>
        public static event Action<string, ChartDataPoint> OnDataReceived;

        /// <summary>
        /// 정적 생성자
        /// </summary>
        static SimulationController()
        {
            // 타이머 틱 이벤트는 한 번만 연결
            SimulationTimer.OnTick += HandleTimerTick;
        }

        /// <summary>
        /// 타이머 틱 핸들러
        /// </summary>
        private static void HandleTimerTick(TimeSpan time)
        {
            // 매니저가 살아있을 때만 쿼리 요청
            if (_isInitialized && _queryManager != null)
            {
                _queryManager.EnqueueAll(time);
            }
        }

        /// <summary>
        /// 시뮬레이션 초기화 및 설정
        /// </summary>
        public static void Initialize(List<(string Id, DatabaseQueryConfig Config)> chartConfigs)
        {
            // 기존 매니저가 있다면 정리
            CleanupManager();

            _queryManager = new SimulationQueryManager();
            
            // 내부 이벤트 연결 (매니저 -> 컨트롤러)
            _queryManager.OnDataReceived += (id, data) => OnDataReceived?.Invoke(id, data);

            foreach (var item in chartConfigs)
            {
                _queryManager.AddChart(item.Id, item.Config);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 시뮬레이션 시작
        /// </summary>
        public static void Start(double speed = 1.0)
        {
            if (!_isInitialized || _queryManager == null)
                throw new InvalidOperationException("Initialize() must be called before Start().");

            Console.WriteLine("[Controller] Starting Simulation...");
            
            _queryManager.StartAll();
            SimulationTimer.Start(speed);
        }

        /// <summary>
        /// 시뮬레이션 일시정지
        /// </summary>
        public static void Pause()
        {
            Console.WriteLine("[Controller] Pausing Simulation...");
            SimulationTimer.Stop();
        }

        /// <summary>
        /// 시뮬레이션 완전 정지 (초기화 상태 유지, 재시작 가능)
        /// </summary>
        public static void Stop()
        {
            Console.WriteLine("[Controller] Stopping Simulation...");
            SimulationTimer.Stop();
            
            // 매니저 정지 (리소스 정리는 하지만 설정은 유지하고 싶다면 StopAll만 호출)
            if (_queryManager != null)
            {
                _queryManager.StopAll();
            }
            
            // 주의: 여기서 OnDataReceived = null을 하면 안 됩니다!
            // 사용자가 다시 Start를 누를 수 있기 때문입니다.
        }

        /// <summary>
        /// 내부 매니저 정리 (설정 변경 시 호출)
        /// </summary>
        private static void CleanupManager()
        {
            if (_queryManager != null)
            {
                _queryManager.StopAll();
                _queryManager.Dispose();
                _queryManager = null;
            }
        }
    }
}
