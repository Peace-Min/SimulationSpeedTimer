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
        private List<DatabaseQueryService> _services = new List<DatabaseQueryService>();
        private TimeSpan? _maxSimulationTime;
        private bool _isInitialized = false;

        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// 매개변수: (ServiceId, ChartDataPoint)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataReceived;

        /// <summary>
        /// 시뮬레이션 초기화 및 설정
        /// </summary>
        /// <param name="chartConfigs">차트 ID와 설정 목록</param>
        public void Initialize(List<(string Id, DatabaseQueryConfig Config)> chartConfigs)
        {
            if (_isInitialized)
            {
                // 재초기화 시 기존 서비스 정리
                StopAll();
                foreach (var service in _services)
                {
                    service.Dispose();
                }
                _services.Clear();
            }

            foreach (var (id, config) in chartConfigs)
            {
                var service = new DatabaseQueryService(id, config);
                service.OnDataQueried += (serviceId, data) => OnDataReceived?.Invoke(serviceId, data);
                _services.Add(service);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 시뮬레이션 시작
        /// </summary>
        /// <param name="speed">배속 (기본 1.0)</param>
        /// <param name="maxSimulationTime">최대 시뮬레이션 시간 (null이면 무제한)</param>
        public void Start(double speed = 1.0, TimeSpan? maxSimulationTime = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Initialize() must be called before Start().");

            _maxSimulationTime = maxSimulationTime;

            Console.WriteLine("[Controller] Starting Simulation...");

            // 1. 모든 쿼리 서비스(워커) 시작
            foreach (var service in _services)
            {
                service.Start();
            }

            // 2. 타이머 틱 이벤트 연결
            SimulationTimer.OnTick += OnTimerTick;

            // 3. 타이머 시작
            SimulationTimer.Start(speed);
        }

        /// <summary>
        /// 타이머 틱 이벤트 핸들러
        /// </summary>
        private void OnTimerTick(TimeSpan time)
        {
            // 모든 서비스에 쿼리 요청
            foreach (var service in _services)
            {
                service.EnqueueQuery(time);
            }

            // 최대 시간 체크 - 초과하면 자동 종료
            if (_maxSimulationTime.HasValue && time >= _maxSimulationTime.Value)
            {
                Console.WriteLine($"[Controller] Max simulation time reached: {time.TotalSeconds:F2}s");
                Stop();
            }
        }

        /// <summary>
        /// 시뮬레이션 완전 정지
        /// </summary>
        public void Stop()
        {
            Console.WriteLine("[Controller] Stopping Simulation...");

            // 타이머 이벤트 연결 해제
            SimulationTimer.OnTick -= OnTimerTick;

            // 타이머 정지
            SimulationTimer.Stop();

            // 모든 서비스 정지
            StopAll();
        }

        /// <summary>
        /// 모든 서비스 정지
        /// </summary>
        private void StopAll()
        {
            foreach (var service in _services)
            {
                service.Stop();
            }
        }

        /// <summary>
        /// 리소스 정리
        /// </summary>
        public void Dispose()
        {
            Stop();

            foreach (var service in _services)
            {
                service.Dispose();
            }
            _services.Clear();

            // 이벤트 핸들러 초기화
            OnDataReceived = null;
        }
    }
}
