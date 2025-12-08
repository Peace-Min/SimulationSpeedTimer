using System;
using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시뮬레이션 전체를 관장하는 컨트롤러 (Facade 패턴)
    /// 외부(UI/기존 코드)와 내부 로직(타이머, DB쿼리)을 분리합니다.
    /// </summary>
    public class SimulationController : IDisposable
    {
        private SimulationQueryManager _queryManager;
        private bool _isInitialized = false;

        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataReceived;

        public SimulationController()
        {
            _queryManager = new SimulationQueryManager();
            
            // 내부 이벤트 연결
            _queryManager.OnDataReceived += (id, data) => OnDataReceived?.Invoke(id, data);

            // 타이머와 쿼리 매니저 연결
            // 타이머가 틱을 발생시키면 -> 쿼리 매니저에게 작업 지시
            SimulationTimer.OnTick += (time) => _queryManager.EnqueueAll(time);
        }

        /// <summary>
        /// 시뮬레이션 초기화 및 설정
        /// </summary>
        /// <param name="chartConfigs">차트 ID와 설정 목록</param>
        public void Initialize(List<(string Id, DatabaseQueryConfig Config)> chartConfigs)
        {
            if (_isInitialized)
            {
                // 재초기화 시 기존 서비스 정리
                _queryManager.StopAll();
                _queryManager.Dispose();
                _queryManager = new SimulationQueryManager();
                _queryManager.OnDataReceived += (id, data) => OnDataReceived?.Invoke(id, data);
            }

            foreach (var item in chartConfigs)
            {
                _queryManager.AddChart(item.Id, item.Config);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 시뮬레이션 시작
        /// </summary>
        /// <param name="speed">배속 (기본 1.0)</param>
        public void Start(double speed = 1.0)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Initialize() must be called before Start().");

            Console.WriteLine("[Controller] Starting Simulation...");
            
            // 1. 모든 쿼리 서비스(워커) 시작
            _queryManager.StartAll();

            // 2. 타이머 시작
            SimulationTimer.Start(speed);
        }

        /// <summary>
        /// 시뮬레이션 일시정지 (타이머만 멈춤)
        /// 쿼리 서비스는 대기 상태로 전환됨
        /// </summary>
        public void Pause()
        {
            Console.WriteLine("[Controller] Pausing Simulation...");
            SimulationTimer.Stop();
        }

        /// <summary>
        /// 시뮬레이션 완전 정지
        /// </summary>
        public void Stop()
        {
            Console.WriteLine("[Controller] Stopping Simulation...");
            SimulationTimer.Stop();
            _queryManager.StopAll();
        }

        /// <summary>
        /// 리소스 정리
        /// </summary>
        public void Dispose()
        {
            Stop();
            _queryManager.Dispose();
            
            // 이벤트 핸들러 초기화
            OnDataReceived = null;
        }
    }
}
