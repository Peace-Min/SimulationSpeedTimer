using System;

namespace SimulationSpeedTimer
{
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Architecture Validation & Backtest ===");

            // [New Architecture] Validator 실행 (필수 격리 테스트)
            // SimulationSpeedTimer.Tests.ArchitectureValidator.Run();
            // [Test Switch]
            // [Validation] Independent Polling & Merging Test
            // Tests.IndependentPollingVerification.Run();
            new SimulationSpeedTimer.Tests.IndependentPollingTest().Run();

            // Tests.ShutdownSyncTest.Run();
            // Tests.StabilityCheck.Run();
        }
    }
}
