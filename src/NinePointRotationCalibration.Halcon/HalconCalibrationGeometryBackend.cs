using System.Collections.Generic;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>使用 HALCON 几何算子执行标定底层矩阵计算。</summary>
    public sealed class HalconCalibrationGeometryBackend : ICalibrationGeometryBackend
    {
        /// <summary>根据对应点拟合二维仿射矩阵。</summary>
        public bool TryFitAffine(
            IList<double> sourceX,
            IList<double> sourceY,
            IList<double> targetX,
            IList<double> targetY,
            out AffineMatrix2D matrix)
        {
            return HalconGeometry.TryFitAffine(sourceX, sourceY, targetX, targetY, out matrix);
        }

        /// <summary>计算仿射矩阵的逆矩阵。</summary>
        public bool TryInvert(AffineMatrix2D matrix, out AffineMatrix2D inverse)
        {
            return HalconGeometry.TryInvert(matrix, out inverse);
        }

        /// <summary>使用 HALCON 变换二维点。</summary>
        public Point2D TransformPoint(AffineMatrix2D matrix, Point2D point)
        {
            return HalconGeometry.TransformPoint(matrix, point);
        }

        /// <summary>使用 HALCON 变换二维向量。</summary>
        public Vector2D TransformVector(AffineMatrix2D matrix, Vector2D vector)
        {
            return HalconGeometry.TransformVector(matrix, vector);
        }

        /// <summary>使用 HALCON 矩阵算子求解线性方程组。</summary>
        public bool TrySolveLinear(double[,] coefficients, double[] right, out double[] solution)
        {
            return HalconGeometry.TrySolveLinear(coefficients, right, out solution);
        }
    }
}
