using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>标定求解使用的可插拔几何计算后端（如 HALCON 矩阵算子或纯托管实现）。</summary>
    public interface ICalibrationGeometryBackend
    {
        /// <summary>根据对应点集拟合从 source 到 target 的二维仿射矩阵。</summary>
        bool TryFitAffine(
            IList<double> sourceX,
            IList<double> sourceY,
            IList<double> targetX,
            IList<double> targetY,
            out AffineMatrix2D matrix);

        /// <summary>计算仿射矩阵的逆矩阵。</summary>
        bool TryInvert(AffineMatrix2D matrix, out AffineMatrix2D inverse);

        /// <summary>对二维点执行完整仿射变换。</summary>
        Point2D TransformPoint(AffineMatrix2D matrix, Point2D point);

        /// <summary>对二维向量执行线性变换（仅线性部分，不含平移）。</summary>
        Vector2D TransformVector(AffineMatrix2D matrix, Vector2D vector);

        /// <summary>求解线性方程组 coefficients * solution = right。</summary>
        bool TrySolveLinear(double[,] coefficients, double[] right, out double[] solution);
    }
}
