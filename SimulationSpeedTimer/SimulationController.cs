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
    /// 시뮬레이션 전체를 관장하는 정적 컨트롤러
    /// </summary>
    public static class SimulationController
    {
        private Dictionary<string, DatabaseQueryService> _services = new Dictionary<string, DatabaseQueryService>();
        private TimeSpan? _maxSimulationTime;
        private double _queryInterval = 1.0;
        private double _nextCheckpoint = 1.0;

        /// <summary>
        /// 통합 데이터 수신 이벤트 (UI 갱신용)
        /// 매개변수: (ServiceId, ChartDataPoint)
        /// </summary>
        public static event Action<string, ChartDataPoint> OnDataReceived;

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
        public void Initialize(List<(string Id, DatabaseQueryConfig Config)> chartConfigs)
            if (_services.TryGetValue(id, out var service))
            {
                service.Stop();
                service.Dispose();
                _services.Remove(id);
                Console.WriteLine($"[Controller] Service removed: {id}");
            }
        }
            }

        /// <summary>
        /// 시뮬레이션 초기화 (등록된 서비스 확인)
        /// </summary>
        //public void Initialize()
        //{
        //    if (_services.Count == 0)
        //    {
        //        Console.WriteLine("[Controller] Warning: No services registered during initialization.");
        //    }

        //    _isInitialized = true;
        //    Console.WriteLine($"[Controller] Initialized with {_services.Count} services.");
        //}

        /// <summary>
        /// 시뮬레이션 시작
        /// </summary>
        /// <param name="speed">배속 (기본 1.0)</param>
        /// <param name="maxSimulationTime">최대 시뮬레이션 시간 (null이면 무제한)</param>
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
        /// <param name="queryInterval">DB 쿼리 간격 (초)</param>
        /// <param name="speed">배속 (기본 1.0)</param>
        public void Start(double queryInterval = 1.0, double speed = 1.0)
        public void Start(double speed = 1.0)
            if (_services.Values.Count == 0)
                return;
                throw new InvalidOperationException("Initialize() must be called before Start().");

            _maxSimulationTime = null; // 초기화 시에는 null (SetSimulationEndTime으로 설정)


            _queryInterval = queryInterval;
            _nextCheckpoint = queryInterval; // Start from first interval

            // 1. 모든 쿼리 서비스(워커) 시작
            foreach (var service in _services.Values)
            {
                service.Start();
            }

            // 2. 타이머 관련 로직 주석 처리 (외부 시간 사용으로 변경)
            /*
            // 타이머 틱 이벤트 연결 (중복 방지)
            SimulationTimer.OnTick -= OnTimerTick;
            SimulationTimer.OnTick += OnTimerTick;

            // 타이머 시작
            // 2. 타이머 시작
            SimulationTimer.Start(speed);
            */
        }

        /// 시뮬레이션 일시 정지 (진행 시간 유지)
        /// 시뮬레이션 일시정지 (타이머만 멈춤)
        /// 쿼리 서비스는 대기 상태로 전환됨
        /// </summary>
        public static void Pause()
        {
            Console.WriteLine("[Controller] Pausing Simulation...");
            // SimulationTimer.Pause();
        }

        /// <summary>
        /// 외부 시간 수신 핸들러 (STime 구조체)
        /// </summary>
        public void OnTimeReceived(STime sTime)
        {
            // 초 단위 시간 변환 (Sec + MicroSec)
            double currentTotalSeconds = sTime.Sec + (sTime.Usec / 1_000_000.0);
            var timeSpan = TimeSpan.FromSeconds(currentTotalSeconds);

            // 이미 종료 시간이 설정되어 있고, 그 시간을 지났다면 무시 (Stop 호출 이후 여분 데이터 방지)
            //if (_maxSimulationTime.HasValue && timeSpan > _maxSimulationTime.Value)
            //{
            //    return;
            //}

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

            // 최대 시간 체크 - 초과하면 자동 종료 및 자투리 시간 조회
            //if (_maxSimulationTime.HasValue && timeSpan >= _maxSimulationTime.Value)
            //{
            //    double maxSeconds = _maxSimulationTime.Value.TotalSeconds;

            //    // 마지막 처리된 구간의 끝점 (현재 _nextCheckpoint - _queryInterval)
            //    // 하지만 while문에서 아직 처리 안 된 구간이 있을 수 있으므로 먼저 while 루프를 다 돌림
            //    // (위의 while 루프가 이미 currentTotalSeconds 기준으로 돌았으므로, 
            //    //  currentTotalSeconds가 maxSeconds보다 크거나 같다면 
            //    //  maxSeconds 직전의 정수배 구간까지는 이미 처리되었을 것임)

            //    // 예: Interval=1.0, Max=35.67
            //    // 1. Time=35.67 수신 -> while 루프에서 34.0~35.0 처리 완료, _nextCheckpoint는 36.0이 됨.
            //    // 2. 남은 구간: 35.0 ~ 35.67

            //    double lastProcessedEnd = _nextCheckpoint - _queryInterval;

            //    // 만약 마지막 처리 구간 끝보다 최대 시간이 더 크다면, 그 사이 자투리 구간 조회
            //    if (maxSeconds > lastProcessedEnd)
            //    {
            //        double rangeStart = lastProcessedEnd;
            //        double rangeEnd = maxSeconds;

            //        foreach (var service in _services.Values)
            //        {
            //            service.EnqueueRangeQuery(rangeStart, rangeEnd);
            //        }
            //    }

            //    Console.WriteLine($"[Controller] Max simulation time reached: {timeSpan.TotalSeconds:F2}s. Final query queued.");
            //    Stop();
            //}
        }

        /*
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
        */

        /// <summary>
        /// 시뮬레이션 완전 정지 (초기화 상태 유지, 재시작 가능)
        /// </summary>
        public static void Stop()
        {
            Console.WriteLine("[Controller] Stopping Simulation...");

            // 타이머 이벤트 연결 해제
            /*
            SimulationTimer.OnTick -= OnTimerTick;

            */

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

        }

        /// <summary>
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
            // 이벤트 핸들러 초기화
            OnDataReceived = null;
        }
    }
}
