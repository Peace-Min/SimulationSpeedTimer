namespace SimulationSpeedTimer
{
    /// <summary>
    /// 메타데이터 해석 후 매핑된 쿼리 정보
    /// </summary>
    public class ResolvedQueryInfo
    {
        public string XTableName { get; set; }
        public string XColumnName { get; set; }
        
        public string YTableName { get; set; }
        public string YColumnName { get; set; }
        
        public DatabaseQueryConfig OriginalConfig { get; set; }
    }
}
