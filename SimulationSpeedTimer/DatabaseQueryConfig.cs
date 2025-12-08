namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB 조회를 위한 메타데이터 설정
    /// Object_Info, Column_Info 테이블에서 실제 테이블명/컬럼명을 조회하기 위한 정보만 포함
    /// </summary>
    public class DatabaseQueryConfig
    {
        /// <summary>
        /// SQLite DB 파일 경로
        /// </summary>
        public string DatabasePath { get; set; }

        /// <summary>
        /// X축 데이터의 Object 이름 (Object_Info.object_name)
        /// </summary>
        public string XAxisObjectName { get; set; }

        /// <summary>
        /// X축 데이터의 Attribute 이름 (Column_Info.attribute_name)
        /// </summary>
        public string XAxisAttributeName { get; set; }

        /// <summary>
        /// Y축 데이터의 Object 이름 (Object_Info.object_name)
        /// </summary>
        public string YAxisObjectName { get; set; }

        /// <summary>
        /// Y축 데이터의 Attribute 이름 (Column_Info.attribute_name)
        /// </summary>
        public string YAxisAttributeName { get; set; }

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
