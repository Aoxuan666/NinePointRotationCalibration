using System;

namespace NinePointRotationCalibration
{
    [Serializable]
    public sealed class MachinePose
    {
        public MachinePose()
        {
        }

        public MachinePose(double x, double y, double thetaDegrees = 0.0)
        {
            X = x;
            Y = y;
            ThetaDegrees = thetaDegrees;
        }

        public double X { get; set; }
        public double Y { get; set; }
        public double ThetaDegrees { get; set; }
        public double ThetaRadians
        {
            get { return AngleMath.DegreesToRadians(ThetaDegrees); }
            set { ThetaDegrees = AngleMath.RadiansToDegrees(value); }
        }

        public bool IsFinite
        {
            get { return Numeric.IsFinite(X) && Numeric.IsFinite(Y) && Numeric.IsFinite(ThetaDegrees); }
        }

        public StagePoint Position { get { return new StagePoint(X, Y); } }
        public MachinePose DeepClone() { return new MachinePose(X, Y, ThetaDegrees); }
    }

    [Serializable]
    public sealed class ImagePose
    {
        public ImagePose()
        {
        }

        public ImagePose(double row, double column, double angleDegrees = 0.0)
        {
            Row = row;
            Column = column;
            AngleDegrees = angleDegrees;
        }

        public double Row { get; set; }
        public double Column { get; set; }
        public double AngleDegrees { get; set; }
        public double AngleRadians
        {
            get { return AngleMath.DegreesToRadians(AngleDegrees); }
            set { AngleDegrees = AngleMath.RadiansToDegrees(value); }
        }

        public bool IsFinite
        {
            get { return Numeric.IsFinite(Row) && Numeric.IsFinite(Column) && Numeric.IsFinite(AngleDegrees); }
        }

        public ImagePoint Position { get { return new ImagePoint(Row, Column); } }
        public ImagePose DeepClone() { return new ImagePose(Row, Column, AngleDegrees); }
    }
}
