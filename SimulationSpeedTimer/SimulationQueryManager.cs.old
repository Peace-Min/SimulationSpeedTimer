using System;
using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 여러 개의 DatabaseQueryService를 통합 관리하는 매니저 클래스
    /// 다중 차트 시뮬레이션 시 서비스 생성, 시작, 쿼리 요청, 종료를 일괄 처리합니다.
    /// </summary>
    public class SimulationQueryManager : IDisposable
    {
        private List<DatabaseQueryService> _services = new List<DatabaseQueryService>();

        /// <summary>
        /// 통합 데이터 수신 이벤트 (어떤 차트에서 데이터가 왔는지 식별 가능)
        /// 매개변수: (ServiceId, ChartDataPoint)
        /// </summary>
        public event Action<string, ChartDataPoint> OnDataReceived;

        /// <summary>
        /// 차트(쿼리 서비스) 추가
        /// </summary>
        /// <param name="chartId">차트 식별자 (예: "RadarChart")</param>
        /// <param name="config">DB 쿼리 설정</param>
        public void AddChart(string chartId, DatabaseQueryConfig config)
        {
            var service = new DatabaseQueryService(chartId, config);

            // 개별 서비스의 이벤트를 매니저의 이벤트로 통합
            service.OnDataQueried += (id, data) => OnDataReceived?.Invoke(id, data);

            _services.Add(service);
        }

        /// <summary>
        /// 모든 서비스 시작
        /// </summary>
        public void StartAll()
        {
            foreach (var service in _services)
            {
                service.Start();
            }
        }

        /// <summary>
        /// 모든 서비스 정지
        /// </summary>
        public void StopAll()
        {
            foreach (var service in _services)
            {
                service.Stop();
            }
        }

        /// <summary>
        /// 모든 서비스에 쿼리 요청 (타이머 Tick에서 호출)
        /// </summary>
        /// <param name="time">현재 시뮬레이션 시간</param>
        public void EnqueueAll(TimeSpan time)
        {
            foreach (var service in _services)
            {
                service.EnqueueQuery(time);
            }
        }

        /// <summary>
        /// 리소스 정리 (모든 서비스 Dispose)
        /// </summary>
        public void Dispose()
        {
            StopAll();
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
