using System;
using System.Drawing;

namespace NinePointRotationCalibration.WinForms.Geometry
{
    /// <summary>
    /// 表示图像坐标系中的二维点（以像素为单位的行/列坐标）。
    /// </summary>
    [Serializable]
    public struct ImageCoordinate : IEquatable<ImageCoordinate>
    {
        public ImageCoordinate(double row, double column)
        {
            Row = row;
            Column = column;
        }

        public double Row { get; set; }

        public double Column { get; set; }

        /// <summary>
        /// 转换为 GDI+ 绘制使用的 PointF（X 对应 Column，Y 对应 Row）。
        /// </summary>
        public PointF ToPointF()
        {
            return new PointF((float)Column, (float)Row);
        }

        public bool Equals(ImageCoordinate other)
        {
            return Row.Equals(other.Row) && Column.Equals(other.Column);
        }

        public override bool Equals(object obj)
        {
            return obj is ImageCoordinate && Equals((ImageCoordinate)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Row.GetHashCode() * 397) ^ Column.GetHashCode();
            }
        }

        public override string ToString()
        {
            return string.Format("R {0:0.###}, C {1:0.###}", Row, Column);
        }

        public static bool operator ==(ImageCoordinate left, ImageCoordinate right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ImageCoordinate left, ImageCoordinate right)
        {
            return !left.Equals(right);
        }
    }
}
