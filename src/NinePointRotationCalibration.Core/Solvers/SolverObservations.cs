namespace NinePointRotationCalibration
{
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
        public double NormalizedColumn;
        public double NormalizedRow;
    }

    internal sealed class RotationAngleFit
    {
        public int Direction;
        public double Offset;
        public double Slope;
        public double Span;
        public double Rms;
        public double MaxResidual;
        public int InlierCount;
        public bool[] Inliers;
        public double[] Residuals;
    }
}
