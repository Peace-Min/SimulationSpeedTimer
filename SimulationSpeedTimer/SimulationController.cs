using System;
using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    public struct STime
    {
        public uint Sec;
        public uint Usec;
    }

    /// <summary>
    /// 시뮬레이션 전체를 관장하는 컨트롤러 (인스턴스)
    /// </summary>
    public class SimulationController : IDisposable
    {
        private Dictionary<string, DatabaseQueryService> _services = new Dictionary<string, DatabaseQueryService>();
        private TimeSpan? _maxSimulationTime;
        private double _queryInterval = 1.0;
        private double _nextCheckpoint = 1.0;

        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// 매개변수: (ServiceId, ChartDataPoint)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataReceived;

        /// <summary>
        /// 서비스 추가 (동적)
        /// </summary>
        public void AddService(string id, DatabaseQueryConfig config)
        {
            if (_services.ContainsKey(id))
            {
                RemoveService(id);
            }

            var service = new DatabaseQueryService(id, config);
            service.OnDataQueried += (serviceId, data) => OnDataReceived?.Invoke(serviceId, data);
            _services[id] = service;
            Console.WriteLine($"[Controller] Service added: {id}");
        }

        /// <summary>
        /// 서비스 제거 (동적)
        /// </summary>
        public void RemoveService(string id)
        {
            if (_services.TryGetValue(id, out var service))
            {
                service.Stop();
                service.Dispose();
                _services.Remove(id);
                Console.WriteLine($"[Controller] Service removed: {id}");
            }
        }

        public void Initialize(List<(string Id, DatabaseQueryConfig Config)> chartConfigs)
        {
            foreach (var config in chartConfigs)
            {
                AddService(config.Id, config.Config);
            }
        }

        /// <summary>
        /// 시뮬레이션 종료 시간 설정 (외부 API 연동)
        /// </summary>
        public void SetSimulationEndTime(TimeSpan endTime)
        {
            _maxSimulationTime = endTime;
            Console.WriteLine($"[Controller] Simulation end time set to: {endTime.TotalSeconds:F2}s");
        }

        /// <summary>
        /// 시뮬레이션 시작
        /// </summary>
        /// <param name="queryInterval">DB 쿼리 간격 (초)</param>
        /// <param name="speed">배속 (기본 1.0)</param>
        public void Start(double queryInterval = 1.0, double speed = 1.0)
        {
            if (_services.Values.Count == 0)
            {
                return;
            }

            _maxSimulationTime = null; // 초기화 시에는 null (SetSimulationEndTime으로 설정)

            _queryInterval = queryInterval;
            _nextCheckpoint = queryInterval; // Start from first interval

            // 1. 모든 쿼리 서비스(워커) 시작
            foreach (var service in _services.Values)
            {
                service.Start();
            }
        }

        /// <summary>
        /// 시뮬레이션 일시 정지 (진행 시간 유지)
        /// 시뮬레이션 일시정지 (타이머만 멈춤)
        /// 쿼리 서비스는 대기 상태로 전환됨
        /// </summary>
        public void Pause()
        {
            Console.WriteLine("[Controller] Pausing Simulation...");
        }

        /// <summary>
        /// 외부 시간 수신 핸들러 (STime 구조체)
        /// </summary>
        public void OnTimeReceived(STime sTime)
        {
            // 초 단위 시간 변환 (Sec + MicroSec)
            double currentTotalSeconds = sTime.Sec + (sTime.Usec / 1_000_000.0);
            var timeSpan = TimeSpan.FromSeconds(currentTotalSeconds);

            // 현재 시간이 다음 체크포인트를 넘어섰는지 확인 (중복 수신, 미세 시간 전진 등은 자동 무시됨)
            while (currentTotalSeconds >= _nextCheckpoint)
            {
                // 쿼리 범위 결정 (이전 체크포인트 ~ 현재 체크포인트)
                double rangeStart = _nextCheckpoint - _queryInterval;
                double rangeEnd = _nextCheckpoint;

                // 모든 서비스에 쿼리 요청
                foreach (var service in _services.Values)
                {
                    service.EnqueueRangeQuery(rangeStart, rangeEnd);
                }

                // 다음 체크포인트 갱신
                _nextCheckpoint += _queryInterval;
            }
        }

        /// <summary>
        /// 시뮬레이션 완전 정지 (초기화 상태 유지, 재시작 가능)
        /// </summary>
        public void Stop()
        {
            Console.WriteLine("[Controller] Stopping Simulation...");
            // 모든 서비스 정지
            StopAll();
        }

        /// <summary>
        /// 모든 서비스 정지
        /// </summary>
        private void StopAll()
        {
            foreach (var service in _services.Values)
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

            foreach (var service in _services.Values)
            {
                service.Dispose();
            }
            _services.Clear();

            // 이벤트 핸들러 초기화
            OnDataReceived = null;
        }
    }
}
