namespace SimulationSpeedTimer
{
    /// <summary>
    /// Object_Info, Column_Info에서 해석된 실제 쿼리 정보
    /// 초기 조회 시점에 메타데이터를 기반으로 생성됨
    /// </summary>
    public class ResolvedQueryInfo
    {
        /// <summary>
        /// X축 데이터가 저장된 실제 테이블명
        /// </summary>
        public string XAxisTableName { get; set; }

        /// <summary>
        /// X축 데이터의 실제 컬럼명
        /// </summary>
        public string XAxisColumnName { get; set; }

        /// <summary>
        /// Y축 데이터가 저장된 실제 테이블명
        /// </summary>
        public string YAxisTableName { get; set; }

        /// <summary>
        /// Y축 데이터의 실제 컬럼명
        /// </summary>
        public string YAxisColumnName { get; set; }

        /// <summary>
        /// X축 테이블의 시간 컬럼명
        /// </summary>
        public string XAxisTimeColumnName { get; set; }

        /// <summary>
        /// Y축 테이블의 시간 컬럼명
        /// </summary>
        public string YAxisTimeColumnName { get; set; }

        /// <summary>
        /// X축과 Y축이 같은 테이블에 있는지 여부
        /// </summary>
        public bool IsSameTable => XAxisTableName == YAxisTableName;
    }
}
