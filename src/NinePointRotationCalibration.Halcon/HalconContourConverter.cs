using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>在 HALCON XLD 轮廓与图像坐标点列表之间执行提取、变换与展平。</summary>
    internal static class HalconContourConverter
    {
        /// <summary>从 HALCON XLD 轮廓对象逐段提取有序坐标点。</summary>
        public static List<List<ImagePoint>> Extract(HXLDCont contours)
        {
            if (contours == null) throw new ArgumentNullException(nameof(contours));
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            int count = contours.CountObj();
            // HALCON 图标对象索引从 1 开始。
            for (int objectIndex = 1; objectIndex <= count; objectIndex++)
            {
                using (HXLDCont contour = contours.SelectObj(objectIndex))
                {
                    HTuple rows = null;
                    HTuple columns = null;
                    try
                    {
                        contour.GetContourXld(out rows, out columns);
                        int length = Math.Min(rows.Length, columns.Length);
                        List<ImagePoint> segment = new List<ImagePoint>(length);
                        for (int pointIndex = 0; pointIndex < length; pointIndex++)
                        {
                            segment.Add(new ImagePoint(rows[pointIndex].D, columns[pointIndex].D));
                        }

                        if (segment.Count > 0) result.Add(segment);
                    }
                    finally
                    {
                        if (rows != null) rows.Dispose();
                        if (columns != null) columns.Dispose();
                    }
                }
            }

            return result;
        }

        /// <summary>按给定中心和 HALCON 弧度角对相对轮廓点做刚体旋转平移。</summary>
        public static List<List<ImagePoint>> Transform(
            IList<List<ImagePoint>> relativeSegments,
            double row,
            double column,
            double angleRadians)
        {
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            if (relativeSegments == null) return result;

            double cosine = Math.Cos(angleRadians);
            double sine = Math.Sin(angleRadians);
            foreach (List<ImagePoint> sourceSegment in relativeSegments)
            {
                if (sourceSegment == null) continue;
                List<ImagePoint> destination = new List<ImagePoint>(sourceSegment.Count);
                foreach (ImagePoint source in sourceSegment)
                {
                    // 在 HALCON (Row, Column) 右手系中，逆时针旋转角 angleRadians 对应矩阵 [cos -sin; sin cos]。
                    double transformedRow = row + (cosine * source.Row) - (sine * source.Column);
                    double transformedColumn = column + (sine * source.Row) + (cosine * source.Column);
                    destination.Add(new ImagePoint(transformedRow, transformedColumn));
                }

                if (destination.Count > 0) result.Add(destination);
            }

            return result;
        }

        /// <summary>将多段轮廓合并展平为单一的点列表。</summary>
        public static List<ImagePoint> Flatten(IList<List<ImagePoint>> segments)
        {
            List<ImagePoint> flattened = new List<ImagePoint>();
            if (segments == null) return flattened;
            foreach (List<ImagePoint> segment in segments)
            {
                if (segment != null) flattened.AddRange(segment);
            }

            return flattened;
        }
    }
}
