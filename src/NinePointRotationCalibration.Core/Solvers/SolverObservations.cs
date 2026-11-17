namespace NinePointRotationCalibration
{
    /// <summary>平移仿射求解器内部使用的样本观测记录。</summary>
    internal sealed class AffineObservation
    {
        public CalibrationSample Sample;
        public CalibrationSampleDiagnostic Diagnostic;
        public int Index;
        public double X;
        public double Y;
        public double Column;
        public double Row;
        public double Weight;
        public bool IsInlier;
        public double Residual;
    }

    /// <summary>旋转标定求解器内部使用的样本观测记录。</summary>
    internal sealed class RotationObservation
    {
        public CalibrationSample Sample;
        public CalibrationSampleDiagnostic Diagnostic;
        public int Index;
        public double MachineAngle;
        public double ImageAngle;
        public double Column;
        public double Row;
        public double StageX;
        public double StageY;
        public double Weight;
        public bool AngleInlier;
        public bool CenterInlier;
        public double AngleResidual;
        public double CenterResidual;
        // 扣除机械台面相对参考位置的平移投影后，保留下来的纯旋转等效像素坐标。
        public double NormalizedColumn;
        public double NormalizedRow;
    }

    /// <summary>固定方向（+1 或 -1）下旋转角度线性关系的拟合中间结果。</summary>
    internal sealed class RotationAngleFit
    {
        public int Direction;
        public double Offset;
        // 无约束加权线性斜率，用于诊断真实比例是否偏离 ±1。
        public double Slope;
        public double Span;
        public double Rms;
        public double MaxResidual;
        public int InlierCount;
        public bool[] Inliers;
        public double[] Residuals;
    }
}
