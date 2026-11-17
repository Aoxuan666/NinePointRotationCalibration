using System;
using System.Drawing;

namespace NinePointRotationCalibration.WinForms.Geometry
{
    /// <summary>
    /// 图像坐标系中的旋转矩形 ROI（以中心行列、半宽/半高及画布顺时针角度表示）。
    /// </summary>
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

        /// <summary>
        /// 画布平面内的旋转角度（单位：度；因行坐标向下递增，正值表示顺时针旋转）。
        /// </summary>
        public double AngleDegrees { get; set; }

        /// <summary>
        /// 创建当前旋转矩形的深拷贝。
        /// </summary>
        public RotatedRectangle DeepClone()
        {
            return new RotatedRectangle(CenterRow, CenterColumn, HalfWidth, HalfHeight, AngleDegrees);
        }

        /// <summary>
        /// 将矩形局部坐标（横向 localColumn、纵向 localRow）变换为图像坐标。
        /// </summary>
        public ImageCoordinate LocalToImage(double localColumn, double localRow)
        {
            double radians = AngleDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            // 图像 Row 向下为正，局部 +X 轴旋转 AngleDegrees 后在图像中指向 (cos, sin)
            return new ImageCoordinate(
                CenterRow + (localColumn * sine) + (localRow * cosine),
                CenterColumn + (localColumn * cosine) - (localRow * sine));
        }

        /// <summary>
        /// 将图像坐标逆变换为矩形局部坐标，用于命中测试与局部边拖拽缩放。
        /// </summary>
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

        /// <summary>
        /// 按左上、右上、右下、左下（局部坐标顺序）返回四个角点。
        /// </summary>
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

        /// <summary>
        /// 判断指定图像坐标是否位于旋转矩形内部（含边界）。
        /// </summary>
        public bool Contains(ImageCoordinate point)
        {
            PointF local = ImageToLocal(point);
            return Math.Abs(local.X) <= HalfWidth && Math.Abs(local.Y) <= HalfHeight;
        }

        /// <summary>
        /// 计算旋转矩形在图像坐标系中的轴对齐外接矩形。
        /// </summary>
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

        /// <summary>
        /// 规范化尺寸与角度，确保半宽半高不少于 1 像素且角度位于 (-180, 180] 度内。
        /// </summary>
        public void Normalize()
        {
            HalfWidth = Math.Max(1.0, Math.Abs(HalfWidth));
            HalfHeight = Math.Max(1.0, Math.Abs(HalfHeight));
            AngleDegrees = NormalizeAngle(AngleDegrees);
        }

        /// <summary>
        /// 将任意角度（度）归一化到 (-180, 180] 度区间。
        /// </summary>
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
