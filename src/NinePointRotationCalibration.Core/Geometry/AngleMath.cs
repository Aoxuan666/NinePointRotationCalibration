using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>提供角度/弧度转换、周期归一化、连续解包裹与圆均值计算工具。</summary>
    public static class AngleMath
    {
        /// <summary>角度转弧度系数。</summary>
        public const double DegreesToRadiansFactor = Math.PI / 180.0;
        /// <summary>弧度转角度系数。</summary>
        public const double RadiansToDegreesFactor = 180.0 / Math.PI;

        /// <summary>将角度从度转换为弧度。</summary>
        public static double DegreesToRadians(double degrees) { return degrees * DegreesToRadiansFactor; }
        /// <summary>将角度从弧度转换为度。</summary>
        public static double RadiansToDegrees(double radians) { return radians * RadiansToDegreesFactor; }

        /// <summary>将角度归一化到 [-180, 180) 区间（度）。</summary>
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

        /// <summary>将弧度归一化到 [-pi, pi) 区间。</summary>
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

        /// <summary>计算从起始角到目标角的最短有符号角度差（度）。</summary>
        public static double SignedDeltaDegrees(double fromDegrees, double toDegrees)
        {
            return NormalizeDegrees(toDegrees - fromDegrees);
        }

        /// <summary>对周期性角度序列进行连续解包裹，消除跨 ±180 度跳变。</summary>
        public static double[] UnwrapDegrees(IList<double> angles)
        {
            if (angles == null) throw new ArgumentNullException("angles");
            double[] unwrapped = new double[angles.Count];
            if (angles.Count == 0) return unwrapped;
            unwrapped[0] = angles[0];
            for (int i = 1; i < angles.Count; i++)
            {
                // 基于相邻采样间的最短有符号角差逐步累加，保持序列连续性。
                unwrapped[i] = unwrapped[i - 1] + SignedDeltaDegrees(angles[i - 1], angles[i]);
            }

            return unwrapped;
        }

        /// <summary>按单位圆矢量加权平均计算一组角度的圆均值（度）。</summary>
        public static double CircularMeanDegrees(IList<double> angles, IList<double> weights = null)
        {
            if (angles == null) throw new ArgumentNullException("angles");
            if (weights != null && weights.Count != angles.Count) throw new ArgumentException("Weight count must match angle count.", "weights");
            if (angles.Count == 0) return double.NaN;

            // 将角度投影到单位圆的 (cos, sin) 分量上求和，避免 ±180 度边界算术平均失真。
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
