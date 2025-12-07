namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB 조회 설정 정보
    /// </summary>
    public class DatabaseQueryConfig
    {
        /// <summary>
        /// SQLite DB 파일 경로
        /// </summary>
        public string DatabasePath { get; set; }

        /// <summary>
        /// 조회할 테이블명
        /// </summary>
        public string TableName { get; set; }

        /// <summary>
        /// X축으로 사용할 컬럼명 (사용자 선택)
        /// </summary>
        public string XAxisColumnName { get; set; }

        /// <summary>
        /// Y축으로 사용할 컬럼명 (사용자 선택)
        /// </summary>
        public string YAxisColumnName { get; set; }

        /// <summary>
        /// 시간 컬럼명 (기본키, WHERE 조건에 사용)
        /// 기본값: "Time"
        /// </summary>
        public string TimeColumnName { get; set; } = "Time";

        /// <summary>
        /// 데이터가 없을 때 재시도 횟수
        /// 기본값: 3
        /// </summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// 재시도 간격 (밀리초)
        /// 기본값: 10ms
        /// </summary>
        public int RetryIntervalMs { get; set; } = 10;
    }
}
