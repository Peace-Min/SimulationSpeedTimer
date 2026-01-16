using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SimulationSpeedTimer.Tests
{
    public class WorkerLoopLogicTest
    {
        // 로직 모의 실험을 위한 설정
        private static double QueryInterval = 1.0;
        private static double nextCheckpoint;
        private static double lastQueryEndTime = 0.0;
        private static double lastSeenTime = 0.0;

        // 입력을 큐에 쌓아두고 하나씩 꺼내기 위한 에뮬레이터
        private static BlockingCollection<double> _timeBuffer = new BlockingCollection<double>();

        public static void RunTest()
        {
            Console.WriteLine("=== WorkerLoop Logic Verification Test ===");
            Console.WriteLine(string.Format("Config Interval: {0}", QueryInterval));

            // 초기화
            nextCheckpoint = QueryInterval;
            lastQueryEndTime = 0.0;

            // 시나리오 데이터 주입
            // 1. 정상 진행 (0 ~ 3초)
            _timeBuffer.Add(0.5); // 무시됨 (time < nextCheckpoint)
            _timeBuffer.Add(1.0); // Trigger! Range: 0.0 ~ 1.0
            _timeBuffer.Add(1.5); // 무시됨
            _timeBuffer.Add(2.0); // Trigger! Range: 1.0 ~ 2.0

            // 2. Fast-Forward 상황 (갑자기 5.5초로 점프)
            // 기대: 
            // - 일단 loop 진입부에서 Range: 2.0 ~ 3.0 처리 (Regular)
            // - Gap Check: 5.5 - 3.0 = 2.5 (> 1.0) -> Fast Forward 발동
            // - Range: 3.0 ~ 5.5 처리 (Fast-Forward)
            // - NextCheckpoint: 6.0으로 점프
            _timeBuffer.Add(5.5);

            // 3. 정상 복귀 (6.2초)
            // 기대:
            // - Trigger (6.2 >= 6.0)
            // - Range: 5.5 ~ 6.0 처리 (Regular - Start가 lastQueryEndTime(5.5)여야 함)
            // - NextCheckpoint: 7.0
            _timeBuffer.Add(6.2);

            // 4. 종료 시 잔여 처리
            // Stop 호출 시점이라 가정. timeBuffer 닫힘.
            _timeBuffer.CompleteAdding();

            // 로직 실행 (WorkerLoop 내용 복사본)
            ExecuteWorkerLoopLogic();
        }

        private static void ExecuteWorkerLoopLogic()
        {
            Console.WriteLine("\n[Start Processing]");

            foreach (var time in _timeBuffer.GetConsumingEnumerable())
            {
                lastSeenTime = time;
                // Console.WriteLine($"Received Time: {time}");

                if (time >= nextCheckpoint)
                {
                    // [Step 19] rangeStart = lastQueryEndTime
                    double rangeStart = lastQueryEndTime;
                    double rangeEnd = nextCheckpoint;

                    // [Regular Process]
                    ProcessRange("Regular", rangeStart, rangeEnd);

                    // [Step 10] lastQueryEndTime 갱신
                    lastQueryEndTime = rangeEnd;

                    double gap = time - nextCheckpoint;
                    // [Step 10, 14] Gap Check
                    if (gap > QueryInterval)
                    {
                        Console.WriteLine(string.Format("   >> Fast-Forward Detected! Gap: {0:F2}", gap));

                        double safeEndTime = time + 0.000001;
                        ProcessRange("Fast-Forward", nextCheckpoint, safeEndTime);

                        lastQueryEndTime = safeEndTime;

                        // 인덱스 이동
                        int jumps = (int)Math.Floor((time - nextCheckpoint) / QueryInterval) + 1;
                        nextCheckpoint += jumps * QueryInterval;
                        Console.WriteLine(string.Format("   >> Jumps: {0}, New NextCheckpoint: {1:F1}", jumps, nextCheckpoint));
                    }
                    else
                    {
                        // [Step 14] Normal Advance
                        nextCheckpoint += QueryInterval;
                        Console.WriteLine(string.Format("   >> Normal Advance. New NextCheckpoint: {0:F1}", nextCheckpoint));
                    }
                }
            }

            // [Step 6] 잔여 데이터 루프
            Console.WriteLine("\n[Shutdown Sequence]");
            if (lastSeenTime > lastQueryEndTime)
            {
                ProcessRange("Remaining", lastQueryEndTime, lastSeenTime + 0.000001);
            }
            else
            {
                Console.WriteLine("No remaining data to process.");
            }
        }

        private static void ProcessRange(string type, double start, double end)
        {
            Console.WriteLine(string.Format("[{0}] Processed Range: {1:F2} ~ {2:F2} (Msg Time: {3:F2})", type.PadRight(12), start, end, lastSeenTime));
        }

        public static void Main(string[] args)
        {
            try
            {
                RunTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("[FATAL ERROR] {0}", ex.Message));
            }
            // 글로벌 룰: 사용자 입력 대기 금지 & 즉시 종료
            Environment.Exit(0);
        }
    }
}
