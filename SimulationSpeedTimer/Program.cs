using System;

namespace SimulationSpeedTimer
{
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Architecture Validation & Backtest ===");

            // [New Architecture] Validator 실행 (필수 격리 테스트)
            SimulationSpeedTimer.Tests.ArchitectureValidator.Run();
        }
    }
}
