using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// DB 조회를 위한 메타데이터 설정
    /// Object_Info, Column_Info 테이블에서 실제 테이블명/컬럼명을 조회하기 위한 정보만 포함
    /// </summary>
    public class SeriesItem
    {
        public string ObjectName { get; set; }
        public string AttributeName { get; set; }
        public string SeriesName { get; set; }

        public SeriesItem Clone()
        {
            return new SeriesItem
            {
                ObjectName = this.ObjectName,
                AttributeName = this.AttributeName,
                SeriesName = this.SeriesName
            };
        }
    }

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
        /// X축 데이터 설정 (단일 컬럼)
        /// </summary>
        public SeriesItem XColumn { get; set; } = new SeriesItem();

        /// <summary>
        /// Y축 데이터 설정 (단일 컬럼)
        /// 기존의 다중 Y 시리즈 리스트(YAxisSeries)는 사용하지 않음
        /// </summary>
        public SeriesItem YColumn { get; set; } = new SeriesItem();

        /// <summary>
        /// Z축 데이터 설정 (단일 컬럼, Optional)
        /// 3D 차트 사용 시 설정, 2D 차트인 경우 null 또는 무시됨
        /// </summary>
        public SeriesItem ZColumn { get; set; } = new SeriesItem();

        // [Helper Properties]
        public bool IsXAxisTime => string.IsNullOrEmpty(XColumn.AttributeName)
                                   || XColumn.AttributeName.Equals("s_time", System.StringComparison.OrdinalIgnoreCase)
                                   || XColumn.AttributeName.Equals("Time", System.StringComparison.OrdinalIgnoreCase);

        public bool Is3DMode => !string.IsNullOrEmpty(ZColumn.ObjectName) && !string.IsNullOrEmpty(ZColumn.AttributeName);

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

        /// <summary>
        /// 설정 객체를 복제(Deep Copy)합니다.
        /// 시뮬레이션 시작 시점의 스냅샷을 안전하게 보관하기 위함입니다.
        /// </summary>
        public DatabaseQueryConfig Clone()
        {
            return new DatabaseQueryConfig
            {
                DatabasePath = this.DatabasePath,
                XColumn = this.XColumn?.Clone(),
                YColumn = this.YColumn?.Clone(),
                ZColumn = this.ZColumn?.Clone(),
                RetryCount = this.RetryCount,
                RetryIntervalMs = this.RetryIntervalMs
            };
        }
    }
}
