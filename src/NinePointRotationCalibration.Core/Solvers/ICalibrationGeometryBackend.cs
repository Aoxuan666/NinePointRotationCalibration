using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>标定求解使用的几何计算后端。</summary>
    public interface ICalibrationGeometryBackend
    {
        /// <summary>根据对应点拟合二维仿射矩阵。</summary>
        bool TryFitAffine(
            IList<double> sourceX,
            IList<double> sourceY,
            IList<double> targetX,
            IList<double> targetY,
            out AffineMatrix2D matrix);

        /// <summary>计算仿射矩阵的逆矩阵。</summary>
        bool TryInvert(AffineMatrix2D matrix, out AffineMatrix2D inverse);

        /// <summary>变换一个二维点。</summary>
        Point2D TransformPoint(AffineMatrix2D matrix, Point2D point);

        /// <summary>变换一个二维向量。</summary>
        Vector2D TransformVector(AffineMatrix2D matrix, Vector2D vector);

        /// <summary>求解线性方程组。</summary>
        bool TrySolveLinear(double[,] coefficients, double[] right, out double[] solution);
    }
}
