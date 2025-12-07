using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
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

        private static void TickLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TimeSpan current = CurrentTime;

                    // Execute all pending ticks
                    // 만약 시스템 부하로 인해 시간이 많이 지났다면, 빠진 틱을 모두 처리할지(Catch-up), 
                    // 아니면 스킵할지는 선택사항이나, 시뮬레이션은 보통 순차 처리가 중요하므로 Catch-up으로 구현합니다.
                    // 다만 무한루프 방지를 위해 한 번 루프당 처리 한도 등을 둘 수도 있습니다.

                    if (current >= _nextTick)
                    {
                        OnTick?.Invoke(_nextTick);
                        _nextTick += TimeSpan.FromMilliseconds(TickIntervalMs);

                        // 틱 처리를 하고 나서도 시간이 남았다면 루프를 바로 다시 돕니다 (Spin)
                        continue;
                    }

                    // 다음 틱까지 남은 시뮬레이션 시간
                    TimeSpan simTimeUntilNextTick = _nextTick - current;

                    if (simTimeUntilNextTick <= TimeSpan.Zero) continue;

                    // 실제 대기해야 할 시간 계산 (시뮬레이션 시간 / 배속)
                    double realWaitMs = simTimeUntilNextTick.TotalMilliseconds / _speedMultiplier;

                    // 대기 시간이 너무 길면 Sleep으로 CPU 양보, 짧으면 SpinWait
                    // 윈도우 기본 Timer resolution은 15ms 내외이므로 정밀 제어를 위해 
                    // 15ms 이상 남았을 때만 Sleep 사용
                    if (realWaitMs > 16)
                    {
                        // 약간 덜 자고 일어나서 SpinWait로 정밀도 맞춤
                        Thread.Sleep((int)(realWaitMs - 5));
                    }
                    else
                    {
                        // 짧은 시간은 스핀 대기 (busy wait) -> CPU 사용량은 높지만 정밀함
                        // 하지만 너무 과도한 점유를 막기 위해 0일때는 yield
                        if (realWaitMs > 1)
                            Thread.SpinWait(100);
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
