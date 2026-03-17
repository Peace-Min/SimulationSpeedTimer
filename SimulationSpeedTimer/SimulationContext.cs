using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    public enum SimulationLifecycleState
    {
        Idle,
        Starting,
        Running,
        Stopping
    }

    /// <summary>
    /// 시뮬레이션의 전체 생명주기와 세션 상태를 중앙에서 관리하는 컨텍스트
    /// </summary>
    public class SimulationContext
    {
        public static SimulationContext Instance { get; } = new SimulationContext();

        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 현재 활성 세션 ID. 실행 중이 아니면 Guid.Empty
        /// </summary>
        public Guid CurrentSessionId { get; private set; } = Guid.Empty;

        public SimulationLifecycleState CurrentState { get; private set; } = SimulationLifecycleState.Idle;

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
        /// 새 세션을 시작하고 GlobalDataService까지 함께 구동합니다.
        /// </summary>
        public async Task StartAsync(GlobalDataService.GlobalDataServiceConfig config, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopActiveSessionCoreAsync(notify: true, cancellationToken).ConfigureAwait(false);

                CurrentState = SimulationLifecycleState.Starting;

                var newSessionId = BeginNewSession();
                await GlobalDataService.Instance.StartSessionAsync(newSessionId, config, cancellationToken).ConfigureAwait(false);

                CurrentState = SimulationLifecycleState.Running;
                OnSessionStarted?.Invoke(CurrentSessionId);
            }
            catch
            {
                if (CurrentState == SimulationLifecycleState.Starting)
                {
                    CurrentSessionId = Guid.Empty;
                    CurrentState = SimulationLifecycleState.Idle;
                }

                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 레거시 호출부 호환용. 세션 ID와 repository만 갱신합니다.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopActiveSessionCoreAsync(notify: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private Guid BeginNewSession()
        {
            var newSessionId = Guid.NewGuid();
            CurrentSessionId = newSessionId;
            SharedFrameRepository.Instance.StartNewSession(newSessionId);
            return newSessionId;
        }

        private async Task StopActiveSessionCoreAsync(bool notify, CancellationToken cancellationToken)
        {
            var hadActiveSession = CurrentSessionId != Guid.Empty || GlobalDataService.Instance.HasActiveSession;
            if (!hadActiveSession)
            {
                CurrentState = SimulationLifecycleState.Idle;
                return;
            }

            CurrentState = SimulationLifecycleState.Stopping;

            await GlobalDataService.Instance.StopAsync(cancellationToken).ConfigureAwait(false);

            CurrentSessionId = Guid.Empty;
            SharedFrameRepository.Instance.ClearSessionSchema();
            CurrentState = SimulationLifecycleState.Idle;

            if (notify)
            {
                OnSessionStopped?.Invoke();
            }
        }
    }
}
