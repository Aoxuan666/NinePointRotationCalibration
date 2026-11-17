using System;

namespace NinePointRotationCalibration
{
    /// <summary>机械平台位姿，包含 X、Y 位置与机械轴角度（度）。</summary>
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
        /// <summary>以弧度表示的机械轴角度（内部与 ThetaDegrees 同步换算）。</summary>
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
        /// <summary>创建机械位姿的深拷贝。</summary>
        public MachinePose DeepClone() { return new MachinePose(X, Y, ThetaDegrees); }
    }

    /// <summary>图像空间位姿，包含像素行列位置与模板角度（度）。</summary>
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
        /// <summary>以弧度表示的图像角度（内部与 AngleDegrees 同步换算）。</summary>
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
        /// <summary>创建图像位姿的深拷贝。</summary>
        public ImagePose DeepClone() { return new ImagePose(Row, Column, AngleDegrees); }
    }
}
