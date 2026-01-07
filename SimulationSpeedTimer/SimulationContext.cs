using System;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 시뮬레이션 전체의 생명주기(Lifecycle)와 세션 상태를 중앙 관리하는 컨텍스트
    /// </summary>
    public class SimulationContext
    {
        public static SimulationContext Instance { get; } = new SimulationContext();

        /// <summary>
        /// 현재 활성 세션 ID. 실행 중이 아닐 때는 Guid.Empty
        /// </summary>
        public Guid CurrentSessionId { get; private set; } = Guid.Empty;

        /// <summary>
        /// 시뮬레이션 시작 시 발생 (세션 ID 전달)
        /// </summary>
        public event Action<Guid> OnSessionStarted;

        /// <summary>
        /// 시뮬레이션 종료 시 발생
        /// </summary>
        public event Action OnSessionStopped;

        private SimulationContext() { }

        /// <summary>
        /// 시뮬레이션 세션을 시작합니다.
        /// GlobalDataService를 구동하고 참여자들에게 세션 ID를 전파합니다.
        /// </summary>
        /// <summary>
        /// 시뮬레이션 세션을 시작합니다.
        /// (주의: 이 메서드 호출 후 GlobalDataService.Start()를 명시적으로 호출해야 데이터가 흐릅니다.)
        /// </summary>
        public void Start()
        {
            Console.WriteLine("[SimulationContext] Initializing Session...");

            // 1. Context가 주도적으로 ID 생성
            var newSessionId = Guid.NewGuid();
            CurrentSessionId = newSessionId;

            // 2. Repository 초기화 (Passive)
            SharedFrameRepository.Instance.StartNewSession(newSessionId);
            
            Console.WriteLine($"[SimulationContext] Session Started: {CurrentSessionId}");

            // 3. 구독자(Controller, VM)들에게 알림
            OnSessionStarted?.Invoke(CurrentSessionId);
        }

        /// <summary>
        /// 시뮬레이션 세션을 종료합니다.
        /// (주의: GlobalDataService.Stop()을 먼저 호출하는 것을 권장합니다.)
        /// </summary>
        public void Stop()
        {
            Console.WriteLine("[SimulationContext] Stopping Session...");

            // 1. 종료 알림 (UI 등 정리)
            OnSessionStopped?.Invoke();
            
            CurrentSessionId = Guid.Empty;
            Console.WriteLine("[SimulationContext] Session Stopped.");
        }
    }
}
