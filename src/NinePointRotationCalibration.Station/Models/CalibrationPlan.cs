using System;
using System.Collections.Generic;
using System.Linq;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>标定计划中的单个目标点位（含平移或旋转偏移）。</summary>
    public sealed class CalibrationPlanPoint
    {
        public int PointNumber { get; set; }
        public CalibrationSampleKind Kind { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetR { get; set; }
        public string Tag { get; set; }
        /// <summary>是否同时作为九点平移与五点旋转的共享中心原点。</summary>
        public bool IsSharedOrigin { get; set; }
    }

    /// <summary>生成并查询标准九点平移加五点旋转标定计划的辅助类。</summary>
    public static class CalibrationPlan
    {
        /// <summary>创建九个平移点和五个旋转点组成的标准标定计划。</summary>
        public static IReadOnlyList<CalibrationPlanPoint> Create(double stepX, double stepY, double stepR)
        {
            if (stepX <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepY));
            if (stepR <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepR));

            List<CalibrationPlanPoint> result = new List<CalibrationPlanPoint>();
            // 九宫格平移点按蛇形路径遍历以缩短机台行程，索引 4 为共享中心原点
            double[,] xy =
            {
                { -stepX, -stepY }, { 0.0, -stepY }, { stepX, -stepY },
                { stepX, 0.0 }, { 0.0, 0.0 }, { -stepX, 0.0 },
                { -stepX, stepY }, { 0.0, stepY }, { stepX, stepY }
            };

            for (int index = 0; index < 9; index++)
            {
                result.Add(new CalibrationPlanPoint
                {
                    PointNumber = index + 1,
                    Kind = CalibrationSampleKind.Translation,
                    OffsetX = xy[index, 0],
                    OffsetY = xy[index, 1],
                    Tag = "P" + (index + 1),
                    IsSharedOrigin = index == 4
                });
            }

            // 五点旋转采样本，索引 2 为 0 度共享中心原点
            double[] rotations = { -2.0 * stepR, -stepR, 0.0, stepR, 2.0 * stepR };
            for (int index = 0; index < rotations.Length; index++)
            {
                result.Add(new CalibrationPlanPoint
                {
                    PointNumber = index + 10,
                    Kind = CalibrationSampleKind.Rotation,
                    OffsetR = rotations[index],
                    Tag = "R" + (index + 1),
                    IsSharedOrigin = index == 2
                });
            }

            return result;
        }

        /// <summary>按点号查找计划点；点号不存在时抛出参数异常。</summary>
        public static CalibrationPlanPoint Find(IReadOnlyList<CalibrationPlanPoint> points, int pointNumber)
        {
            CalibrationPlanPoint point = points == null ? null : points.FirstOrDefault(item => item.PointNumber == pointNumber);
            if (point == null) throw new ArgumentOutOfRangeException(nameof(pointNumber), "Unknown calibration point.");
            return point;
        }
    }
}
