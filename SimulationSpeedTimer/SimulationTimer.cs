using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시뮬레이션 타이머 (정적 클래스)
    /// 10ms 간격으로 틱을 발생시키며, 배속 조절이 가능합니다.
    /// </summary>
    public static class SimulationTimer
    {
        private static Stopwatch _stopwatch = new Stopwatch();
        private static TimeSpan _accumulatedTime = TimeSpan.Zero;
        private static double _speedMultiplier = 1.0;
        private static TimeSpan _lastCheckpoint = TimeSpan.Zero;
        private static bool _isRunning = false;
        private static Task _tickTask;
        private static CancellationTokenSource _cts;
        private static TimeSpan _nextTick = TimeSpan.FromMilliseconds(TickIntervalMs);
        private const int TickIntervalMs = 10;

        /// <summary>
        /// 시뮬레이션 시간 10ms마다 발생하는 이벤트
        /// 매개변수: 현재 시뮬레이션 진행 시간 (TimeSpan)
        /// </summary>
        public static event Action<TimeSpan> OnTick;

        /// <summary>
        /// 현재 시뮬레이션 진행 시간
        /// </summary>
        public static TimeSpan CurrentTime
        {
            get
            {
                if (_isRunning)
                {
                    // 현재 흐르고 있는 시간 계산: (현재 실제 시간 - 마지막 체크포인트) * 배속
                    TimeSpan currentElapsed = _stopwatch.Elapsed - _lastCheckpoint;
                    return _accumulatedTime + TimeSpan.FromTicks((long)(currentElapsed.Ticks * _speedMultiplier));
                }
                else
                {
                    return _accumulatedTime;
                }
            }
        }

        /// <summary>
        /// 현재 설정된 배속 (읽기 전용)
        /// </summary>
        public static double SpeedMultiplier => _speedMultiplier;

        public static bool IsRunning => _isRunning;

        /// <summary>
        /// 타이머 시작
        /// </summary>
        /// <param name="speed">시뮬레이션 배속 (기본값 1.0)</param>
        public static void Start(double speed = 1.0)
        {
            if (_isRunning) return;

            _speedMultiplier = speed;

            if (!_stopwatch.IsRunning)
                _stopwatch.Start();

            // 시작 시점의 스톱워치 시간을 체크포인트로 설정
            _lastCheckpoint = _stopwatch.Elapsed;
            _isRunning = true;

            _cts = new CancellationTokenSource();
            _tickTask = Task.Run(() => TickLoop(_cts.Token));
        }

        /// <summary>
        /// 일시 정지
        /// </summary>
        public static void Pause()
        {
            if (!_isRunning) return;

            _isRunning = false;

            // 태스크 취소
            _cts?.Cancel();

            // 태스크 종료 대기
            try
            {
                _tickTask?.Wait(1000); // 1초 타임아웃
            }
            catch (AggregateException) { }

            // Task 디스포즈
            try
            {
                _tickTask?.Dispose();
            }
            catch { }
            _tickTask = null;

            // CancellationTokenSource 디스포즈
            _cts?.Dispose();
            _cts = null;

            // 멈추는 순간까지의 시간을 누적
            SyncTime();
        }

        /// <summary>
        /// 정지 (시간 초기화)
        /// </summary>
        public static void Stop()
        {
            if (!_isRunning && _cts == null) return;

            _isRunning = false;

            // 취소 요청
            _cts?.Cancel();

            // 태스크 종료 대기
            try
            {
                _tickTask?.Wait(1000); // 1초 타임아웃
            }
            catch (AggregateException) { }

            // Task 디스포즈
            try
            {
                _tickTask?.Dispose();
            }
            catch { }
            _tickTask = null;

            // CancellationTokenSource 디스포즈
            _cts?.Dispose();
            _cts = null;

            // 상태 초기화
            _stopwatch.Reset();
            _accumulatedTime = TimeSpan.Zero;
            _lastCheckpoint = TimeSpan.Zero;
            _nextTick = TimeSpan.FromMilliseconds(TickIntervalMs);

            // 이벤트 핸들러 모두 제거 (메모리 누수 방지)
            OnTick = null;
        }

        /// <summary>
        /// 틱 루프 - 10ms마다 OnTick 이벤트 발생
        /// </summary>
        private static void TickLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TimeSpan current = CurrentTime;

                    if (current >= _nextTick)
                    {
                        OnTick?.Invoke(_nextTick);
                        _nextTick += TimeSpan.FromMilliseconds(TickIntervalMs);
                    }
                    else
                    {
                        // 다음 틱까지 대기 (1ms 간격으로 체크)
                        Thread.Sleep(1);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // 종료됨
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in TickLoop: {ex.Message}");
            }
        }

        /// <summary>
        /// 내부적으로 시간을 동기화하여 누적 변수에 저장하고 체크포인트를 갱신함
        /// </summary>
        private static void SyncTime()
        {
            TimeSpan currentElapsed = _stopwatch.Elapsed - _lastCheckpoint;
            _accumulatedTime += TimeSpan.FromTicks((long)(currentElapsed.Ticks * _speedMultiplier));
            _lastCheckpoint = _stopwatch.Elapsed;
        }
    }
}
