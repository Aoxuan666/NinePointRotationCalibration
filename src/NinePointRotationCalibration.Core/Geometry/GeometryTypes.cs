using System;

namespace NinePointRotationCalibration
{
    /// <summary>通用二维笛卡尔坐标点。</summary>
    [Serializable]
    public struct Point2D : IEquatable<Point2D>
    {
        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }
        public bool IsFinite { get { return Numeric.IsFinite(X) && Numeric.IsFinite(Y); } }

        /// <summary>计算到另一点的欧氏距离。</summary>
        public double DistanceTo(Point2D other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        public bool Equals(Point2D other) { return X.Equals(other.X) && Y.Equals(other.Y); }
        public override bool Equals(object obj) { return obj is Point2D && Equals((Point2D)obj); }
        public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); } }
        public override string ToString() { return string.Format("({0:0.###}, {1:0.###})", X, Y); }
    }

    /// <summary>二维向量。</summary>
    [Serializable]
    public struct Vector2D
    {
        public Vector2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }
        public double Length { get { return Math.Sqrt((X * X) + (Y * Y)); } }
        public bool IsFinite { get { return Numeric.IsFinite(X) && Numeric.IsFinite(Y); } }

        /// <summary>返回单位长度向量；零向量返回默认零值。</summary>
        public Vector2D Normalize()
        {
            double length = Length;
            return length <= 1e-15 ? new Vector2D() : new Vector2D(X / length, Y / length);
        }

        public override string ToString() { return string.Format("<{0:0.###}, {1:0.###}>", X, Y); }
    }

    /// <summary>图像像素坐标点（行 Row 对应 Y，列 Column 对应 X）。</summary>
    [Serializable]
    public struct ImagePoint : IEquatable<ImagePoint>
    {
        public ImagePoint(double row, double column)
        {
            Row = row;
            Column = column;
        }

        public double Row { get; set; }
        public double Column { get; set; }
        public bool IsFinite { get { return Numeric.IsFinite(Row) && Numeric.IsFinite(Column); } }
        /// <summary>转换为笛卡尔坐标点（X=Column，Y=Row）。</summary>
        public Point2D ToCartesian() { return new Point2D(Column, Row); }
        /// <summary>由笛卡尔坐标点还原图像像素点（Row=Y，Column=X）。</summary>
        public static ImagePoint FromCartesian(Point2D point) { return new ImagePoint(point.Y, point.X); }

        /// <summary>计算到另一图像点的像素欧氏距离。</summary>
        public double DistanceTo(ImagePoint other)
        {
            double dr = Row - other.Row;
            double dc = Column - other.Column;
            return Math.Sqrt((dr * dr) + (dc * dc));
        }

        public bool Equals(ImagePoint other) { return Row.Equals(other.Row) && Column.Equals(other.Column); }
        public override bool Equals(object obj) { return obj is ImagePoint && Equals((ImagePoint)obj); }
        public override int GetHashCode() { unchecked { return (Row.GetHashCode() * 397) ^ Column.GetHashCode(); } }
        public override string ToString() { return string.Format("(Row={0:0.###}, Column={1:0.###})", Row, Column); }
    }

    /// <summary>机械平台坐标点（线性单位与任务配置一致）。</summary>
    [Serializable]
    public struct StagePoint : IEquatable<StagePoint>
    {
        public StagePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }
        public bool IsFinite { get { return Numeric.IsFinite(X) && Numeric.IsFinite(Y); } }
        /// <summary>转换为二维笛卡尔坐标点。</summary>
        public Point2D ToCartesian() { return new Point2D(X, Y); }
        /// <summary>由二维笛卡尔坐标点构造机械坐标点。</summary>
        public static StagePoint FromCartesian(Point2D point) { return new StagePoint(point.X, point.Y); }

        /// <summary>计算到另一机械坐标点的欧氏距离。</summary>
        public double DistanceTo(StagePoint other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        public bool Equals(StagePoint other) { return X.Equals(other.X) && Y.Equals(other.Y); }
        public override bool Equals(object obj) { return obj is StagePoint && Equals((StagePoint)obj); }
        public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); } }
        public override string ToString() { return string.Format("(X={0:0.###}, Y={1:0.###})", X, Y); }
    }

    /// <summary>内部数值有效性检查与限幅辅助类。</summary>
    internal static class Numeric
    {
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
