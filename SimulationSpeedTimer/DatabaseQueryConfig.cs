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
        /// X축 데이터 설정 (SeriesItem 구조 사용)
        /// </summary>
        public SeriesItem XAxisSeries { get; set; } = new SeriesItem();

        /// <summary>
        /// Y축 다중 시리즈 설정 (SeriesItem 리스트)
        /// </summary>
        public List<SeriesItem> YAxisSeries { get; set; } = new List<SeriesItem>();

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
            var clone = new DatabaseQueryConfig
            {
                DatabasePath = this.DatabasePath,
                XAxisSeries = this.XAxisSeries?.Clone(),
                RetryCount = this.RetryCount,
                RetryIntervalMs = this.RetryIntervalMs
            };

            if (this.YAxisSeries != null)
            {
                foreach (var s in this.YAxisSeries)
                {
                    clone.YAxisSeries.Add(s.Clone());
                }
            }

            return clone;
        }
    }
}
