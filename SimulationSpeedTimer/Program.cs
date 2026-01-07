using System;

namespace SimulationSpeedTimer
{
    internal class MainTest
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== GlobalDataService Integration Test Runner ===");
            
            // GlobalDataService 통합 테스트 실행 (3회 반복)
            GlobalDataServiceTest.Run(1);
            
            Console.WriteLine("\n[Press ANY KEY to exit]");
            Console.ReadKey();
        }
    }
}
