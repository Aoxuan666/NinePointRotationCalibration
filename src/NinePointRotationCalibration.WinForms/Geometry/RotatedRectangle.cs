using System;
using System.Drawing;

namespace NinePointRotationCalibration.WinForms.Geometry
{
    [Serializable]
    public sealed class RotatedRectangle
    {
        public RotatedRectangle()
            : this(0.0, 0.0, 50.0, 50.0, 0.0)
        {
        }

        public RotatedRectangle(
            double centerRow,
            double centerColumn,
            double halfWidth,
            double halfHeight,
            double angleDegrees)
        {
            CenterRow = centerRow;
            CenterColumn = centerColumn;
            HalfWidth = Math.Max(1.0, halfWidth);
            HalfHeight = Math.Max(1.0, halfHeight);
            AngleDegrees = NormalizeAngle(angleDegrees);
        }

        public double CenterRow { get; set; }

        public double CenterColumn { get; set; }

        public double HalfWidth { get; set; }

        public double HalfHeight { get; set; }

        public double AngleDegrees { get; set; }

        public RotatedRectangle DeepClone()
        {
            return new RotatedRectangle(CenterRow, CenterColumn, HalfWidth, HalfHeight, AngleDegrees);
        }

        public ImageCoordinate LocalToImage(double localColumn, double localRow)
        {
            double radians = AngleDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            return new ImageCoordinate(
                CenterRow + (localColumn * sine) + (localRow * cosine),
                CenterColumn + (localColumn * cosine) - (localRow * sine));
        }

        public PointF ImageToLocal(ImageCoordinate point)
        {
            double radians = AngleDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            double deltaColumn = point.Column - CenterColumn;
            double deltaRow = point.Row - CenterRow;
            return new PointF(
                (float)((deltaColumn * cosine) + (deltaRow * sine)),
                (float)((-deltaColumn * sine) + (deltaRow * cosine)));
        }

        public ImageCoordinate[] GetCorners()
        {
            return new[]
            {
                LocalToImage(-HalfWidth, -HalfHeight),
                LocalToImage(HalfWidth, -HalfHeight),
                LocalToImage(HalfWidth, HalfHeight),
                LocalToImage(-HalfWidth, HalfHeight)
            };
        }

        public bool Contains(ImageCoordinate point)
        {
            PointF local = ImageToLocal(point);
            return Math.Abs(local.X) <= HalfWidth && Math.Abs(local.Y) <= HalfHeight;
        }

        public RectangleF GetAxisAlignedBounds()
        {
            ImageCoordinate[] corners = GetCorners();
            double minRow = corners[0].Row;
            double maxRow = corners[0].Row;
            double minColumn = corners[0].Column;
            double maxColumn = corners[0].Column;
            for (int index = 1; index < corners.Length; index++)
            {
                minRow = Math.Min(minRow, corners[index].Row);
                maxRow = Math.Max(maxRow, corners[index].Row);
                minColumn = Math.Min(minColumn, corners[index].Column);
                maxColumn = Math.Max(maxColumn, corners[index].Column);
            }

            return RectangleF.FromLTRB(
                (float)minColumn,
                (float)minRow,
                (float)maxColumn,
                (float)maxRow);
        }

        public void Normalize()
        {
            HalfWidth = Math.Max(1.0, Math.Abs(HalfWidth));
            HalfHeight = Math.Max(1.0, Math.Abs(HalfHeight));
            AngleDegrees = NormalizeAngle(AngleDegrees);
        }

        public static double NormalizeAngle(double angleDegrees)
        {
            double value = angleDegrees % 360.0;
            if (value <= -180.0)
            {
                value += 360.0;
            }
            else if (value > 180.0)
            {
                value -= 360.0;
            }

            return value;
        }
    }
}
