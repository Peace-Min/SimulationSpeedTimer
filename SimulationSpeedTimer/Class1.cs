using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    public struct ChartPoint3D
    {
        public double X, Y, Z;

        public ChartPoint3D(double x, double y, double z) => (X, Y, Z) = (x, y, z);
    }

    // 2. 정적 매퍼 클래스
    public static class ChartPointMapper
    {
        /// <summary>
        /// LightningChart 3D 전용 모델로 변환 (새 배열 생성)
        /// </summary>
        public static SeriesPoint3D[] ToSeriesPoints3D(this ChartPoint3D[] dtoData)
        {
            if (dtoData == null) return Array.Empty<SeriesPoint3D>();

            int count = dtoData.Length;
            var points = new SeriesPoint3D[count];

            for (int i = 0; i < count; i++)
            {
                points[i].X = dtoData[i].X;
                points[i].Y = dtoData[i].Y;
                points[i].Z = dtoData[i].Z;
            }
            return points;
        }

        /// <summary>
        /// 기존 배열(Buffer)에 값을 복사하여 GC 부하 최소화 (재사용 패턴)
        /// </summary>
        public static void MapToBuffer(ChartPoint3D[] source, SeriesPoint3D[] target)
        {
            int count = Math.Min(source.Length, target.Length);
            for (int i = 0; i < count; i++)
            {
                target[i].X = source[i].X;
                target[i].Y = source[i].Y;
                target[i].Z = source[i].Z;
            }
        }
    }
}
