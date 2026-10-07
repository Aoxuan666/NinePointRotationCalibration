using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    public static class AngleMath
    {
        public const double DegreesToRadiansFactor = Math.PI / 180.0;
        public const double RadiansToDegreesFactor = 180.0 / Math.PI;

        public static double DegreesToRadians(double degrees) { return degrees * DegreesToRadiansFactor; }
        public static double RadiansToDegrees(double radians) { return radians * RadiansToDegreesFactor; }

        public static double NormalizeDegrees(double degrees)
        {
            if (!Numeric.IsFinite(degrees))
            {
                return double.NaN;
            }

            double value = degrees % 360.0;
            if (value >= 180.0) value -= 360.0;
            if (value < -180.0) value += 360.0;
            return value;
        }

        public static double NormalizeRadians(double radians)
        {
            if (!Numeric.IsFinite(radians))
            {
                return double.NaN;
            }

            double twoPi = Math.PI * 2.0;
            double value = radians % twoPi;
            if (value >= Math.PI) value -= twoPi;
            if (value < -Math.PI) value += twoPi;
            return value;
        }

        public static double SignedDeltaDegrees(double fromDegrees, double toDegrees)
        {
            return NormalizeDegrees(toDegrees - fromDegrees);
        }

        public static double[] UnwrapDegrees(IList<double> angles)
        {
            if (angles == null) throw new ArgumentNullException("angles");
            double[] unwrapped = new double[angles.Count];
            if (angles.Count == 0) return unwrapped;
            unwrapped[0] = angles[0];
            for (int i = 1; i < angles.Count; i++)
            {
                unwrapped[i] = unwrapped[i - 1] + SignedDeltaDegrees(angles[i - 1], angles[i]);
            }

            return unwrapped;
        }

        public static double CircularMeanDegrees(IList<double> angles, IList<double> weights = null)
        {
            if (angles == null) throw new ArgumentNullException("angles");
            if (weights != null && weights.Count != angles.Count) throw new ArgumentException("Weight count must match angle count.", "weights");
            if (angles.Count == 0) return double.NaN;

            double sin = 0.0;
            double cos = 0.0;
            for (int i = 0; i < angles.Count; i++)
            {
                double weight = weights == null ? 1.0 : Math.Max(0.0, weights[i]);
                double radians = DegreesToRadians(angles[i]);
                sin += weight * Math.Sin(radians);
                cos += weight * Math.Cos(radians);
            }

            if (Math.Abs(sin) + Math.Abs(cos) <= 1e-15) return NormalizeDegrees(angles[0]);
            return NormalizeDegrees(RadiansToDegrees(Math.Atan2(sin, cos)));
        }
    }
}
