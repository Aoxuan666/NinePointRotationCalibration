using System;
using System.Drawing;

namespace NinePointRotationCalibration.WinForms.Geometry
{
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
