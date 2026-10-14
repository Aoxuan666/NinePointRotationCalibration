using System;
using System.Collections.Generic;
using System.Linq;

namespace NinePointRotationCalibration.Station.Models
{
    public sealed class CalibrationPlanPoint
    {
        public int PointNumber { get; set; }
        public CalibrationSampleKind Kind { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetR { get; set; }
        public string Tag { get; set; }
        public bool IsSharedOrigin { get; set; }
    }

    public static class CalibrationPlan
    {
        public static IReadOnlyList<CalibrationPlanPoint> Create(double stepX, double stepY, double stepR)
        {
            if (stepX <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepY));
            if (stepR <= 0.0) throw new ArgumentOutOfRangeException(nameof(stepR));

            List<CalibrationPlanPoint> result = new List<CalibrationPlanPoint>();
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

        public static CalibrationPlanPoint Find(IReadOnlyList<CalibrationPlanPoint> points, int pointNumber)
        {
            CalibrationPlanPoint point = points == null ? null : points.FirstOrDefault(item => item.PointNumber == pointNumber);
            if (point == null) throw new ArgumentOutOfRangeException(nameof(pointNumber), "Unknown calibration point.");
            return point;
        }
    }
}
