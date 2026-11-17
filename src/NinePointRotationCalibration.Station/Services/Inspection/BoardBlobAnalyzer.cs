using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    /// <summary>基于 HALCON 的板件检测 ROI 解析与 Blob 斑点分析器。</summary>
    public sealed class BoardBlobAnalyzer
    {
        /// <summary>对单块定位结果执行 Blob 分析并返回特征列表。</summary>
        public List<BlobFinding> Analyze(Bitmap source, TemplateDefinition template,
            TemplateMatchResult match, DetectionProfile profile)
        {
            using (BlobAnalysisResult result = AnalyzeDetailed(source, template, match,
                profile, false, CancellationToken.None))
                return result.Findings;
        } 

        /// <summary>按模板匹配位姿解析检测 ROI，并执行详细 Blob 分析（可选输出二值图）。</summary>
        public BlobAnalysisResult AnalyzeDetailed(Bitmap source, TemplateDefinition template,
            TemplateMatchResult match, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AnalyzeRoiDetailed(source, ResolveDetectionRoi(template, match, profile),
                profile, includeBinary, cancellationToken);
        }

        /// <summary>跳过模板定位，直接对整幅图像执行详细 Blob 分析。</summary>
        public BlobAnalysisResult AnalyzeDirectDetailed(Bitmap source, TemplateDefinition template,
            DetectionProfile profile, bool includeBinary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AnalyzeRoiDetailed(source, CreateFullImageRoi(source), profile,
                includeBinary, cancellationToken);
        }

        /// <summary>取消时不抛异常的整图 Blob 分析，供实时调试预览使用。</summary>
        public BlobAnalysisResult AnalyzeDirectDetailedNoThrow(Bitmap source,
            TemplateDefinition template, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            DetectionRoi roi = CreateFullImageRoi(source);
            List<BlobAnalysisResult> results = AnalyzeRoisDetailedNoThrow(
                source, new[] { roi }, profile, includeBinary, cancellationToken);
            return results.Count == 0 ? new BlobAnalysisResult { Roi = roi } : results[0];
        }

        /// <summary>对单个指定的检测 ROI 执行 Blob 分析。</summary>
        public BlobAnalysisResult AnalyzeRoiDetailed(Bitmap source, DetectionRoi roi,
            DetectionProfile profile, bool includeBinary, CancellationToken cancellationToken)
        {
            List<BlobAnalysisResult> results = AnalyzeRoisDetailed(source, new[] { roi },
                profile, includeBinary, cancellationToken);
            return results.Count == 0 ? new BlobAnalysisResult { Roi = roi } : results[0];
        }

        /// <summary>批量分析多个检测 ROI，取消时抛出 OperationCanceledException。</summary>
        public List<BlobAnalysisResult> AnalyzeRoisDetailed(Bitmap source,
            IList<DetectionRoi> rois, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            return AnalyzeRoisDetailedCore(source, null, rois, profile, includeBinary,
                cancellationToken, true);
        }

        /// <summary>批量分析多个检测 ROI，取消时直接返回空结果而不抛异常。</summary>
        public List<BlobAnalysisResult> AnalyzeRoisDetailedNoThrow(Bitmap source,
            IList<DetectionRoi> rois, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            return AnalyzeRoisDetailedCore(source, null, rois, profile, includeBinary,
                cancellationToken, false);
        }

        /// <summary>
        /// 生产路径：直接复用模板匹配阶段已转换的灰度图分析各 ROI，保证每帧触发仅转换一次图像。
        /// </summary>
        public List<BlobAnalysisResult> AnalyzeRoisDetailed(HalconGrayImage gray,
            IList<DetectionRoi> rois, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            return AnalyzeRoisDetailedCore(null, gray, rois, profile, includeBinary,
                cancellationToken, true);
        }

        private List<BlobAnalysisResult> AnalyzeRoisDetailedCore(Bitmap source,
            HalconGrayImage gray, IList<DetectionRoi> rois, DetectionProfile profile,
            bool includeBinary, CancellationToken cancellationToken, bool throwOnCancellation)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (throwOnCancellation) cancellationToken.ThrowIfCancellationRequested();
                return new List<BlobAnalysisResult>();
            }
            if ((source == null && gray == null) || profile == null || rois == null || rois.Count == 0)
                return new List<BlobAnalysisResult>();
            Size imageSize = gray != null ? gray.Size : source.Size;

            List<int> validIndexes = Enumerable.Range(0, rois.Count)
                .Where(index => IsValid(rois[index])).ToList();
            List<BlobAnalysisResult> output = rois
                .Select(roi => new BlobAnalysisResult { Roi = roi }).ToList();
            if (validIndexes.Count == 0) return output;

            List<HalconBlobRegion> regions = validIndexes.Select(index => rois[index])
                .Select(roi => new HalconBlobRegion
            {
                Row = roi.ReferenceCenterRow,
                Column = roi.ReferenceCenterColumn,
                PhiDegrees = roi.PhiDegrees,
                HalfWidth = roi.HalfWidth,
                HalfHeight = roi.HalfHeight,
                ImageBounds = GetBounds(imageSize, roi)
            }).ToList();
            HalconBlobParameters parameters = new HalconBlobParameters
            {
                AutoThreshold = profile.AutoThreshold,
                DetectDarkForeground = profile.DetectDarkForeground,
                Threshold = profile.BrightThreshold,
                MinimumArea = profile.MinimumBlobArea,
                MaximumArea = profile.MaximumBlobArea,
                RectangleFilterEnabled = profile.RectangleFilterEnabled,
                RectangleFillThreshold = profile.RectangleFillThreshold,
                FillHoles = profile.FillHoles,
                MorphologyRadius = profile.MorphologyRadius
            };

            List<HalconBlobResult> halconResults = null;
            try
            {
                halconResults = gray != null
                    ? HalconBlobEngine.Analyze(gray, regions, parameters,
                        includeBinary, cancellationToken, throwOnCancellation)
                    : HalconBlobEngine.Analyze(source, regions, parameters,
                        includeBinary, cancellationToken, throwOnCancellation);
                if (!throwOnCancellation &&
                    (halconResults == null || halconResults.Count != regions.Count))
                    return output;
                for (int index = 0; index < halconResults.Count; index++)
                {
                    int destinationIndex = validIndexes[index];
                    output[destinationIndex].Dispose();
                    output[destinationIndex] = MapResult(rois[destinationIndex], halconResults[index]);
                }
                return output;
            }
            finally
            {
                if (halconResults != null)
                    foreach (HalconBlobResult result in halconResults) result.Dispose();
            }
        }

        private static BlobAnalysisResult MapResult(DetectionRoi roi, HalconBlobResult source)
        {
            BlobAnalysisResult result = new BlobAnalysisResult
            {
                Roi = roi,
                ImageBounds = source.ImageBounds,
                BinaryImage = source.BinaryImage,
                ActivePixelCount = source.ActivePixelCount,
                EffectiveThreshold = source.EffectiveThreshold,
                ImageConversionElapsedMilliseconds = source.ImageConversionElapsedMilliseconds,
                ThresholdElapsedMilliseconds = source.ThresholdElapsedMilliseconds,
                MorphologyElapsedMilliseconds = source.MorphologyElapsedMilliseconds,
                HoleFillElapsedMilliseconds = source.HoleFillElapsedMilliseconds,
                BinaryImageElapsedMilliseconds = source.BinaryImageElapsedMilliseconds,
                ComponentElapsedMilliseconds = source.ComponentElapsedMilliseconds,
                ElapsedMilliseconds = source.ElapsedMilliseconds + source.ImageConversionElapsedMilliseconds
            };
            // 将二值图所有权移交给外部结果对象，防止 finally 块释放 halconResults 时误销毁
            source.BinaryImage = null;
            if (source.HasFinding)
            {
                result.Findings.Add(new BlobFinding
                {
                    Row = source.Row,
                    Column = source.Column,
                    Area = source.Area,
                    Rectangularity = source.Rectangularity,
                    RejectedAsRectangle = source.RejectedAsRectangle,
                    Contour = source.Contour.Select(point =>
                        new PointF((float)point.Column, (float)point.Row)).ToList()
                });
            }
            return result;
        }

        /// <summary>根据模板匹配结果与检测配置，推算当前板在图像坐标系下的检测 ROI。</summary>
        public DetectionRoi ResolveDetectionRoi(TemplateDefinition template,
            TemplateMatchResult match, DetectionProfile profile)
        {
            if (template == null || match == null || !match.Success || profile == null)
                return null;
            RegionDefinition source = profile.FollowTemplateTransform
                ? template.DetectionRoi : template.TemplateRoi;
            if (source == null && !profile.FollowTemplateTransform)
                source = CreateReferenceImageRegion(template);
            if (source == null) return null;
            DetectionRoi transformed = TransformRegion(template, match, source);
            // 未开启跟随旋转时，将变换后的 ROI 退化为轴对齐外接矩形
            if (transformed != null && !profile.FollowTemplateTransform)
                transformed = ToAxisAlignedBounds(transformed);
            return transformed;
        }

        private static DetectionRoi TransformRegion(TemplateDefinition template,
            TemplateMatchResult match, RegionDefinition source)
        {
            ImagePose reference = template.ModelReference ?? new ImagePose();
            ImagePose pose = match.ModelPose ?? new ImagePose(match.Row, match.Column, match.AngleDegrees);
            double deltaAngle = DegreesToRadians(pose.AngleDegrees - reference.AngleDegrees);
            ImagePoint center = HalconGeometry.TransformRigid(
                reference,
                pose,
                new ImagePoint(source.CenterRow, source.CenterColumn));
            double halfWidth;
            double halfHeight;
            double phi;
            GetRegionGeometry(source, out halfWidth, out halfHeight, out phi);
            if (halfWidth <= 0.0 || halfHeight <= 0.0) return null;
            return new DetectionRoi
            {
                ReferenceCenterRow = center.Row,
                ReferenceCenterColumn = center.Column,
                // HALCON 角度在 Row 向下的图像中为逆时针正方向，GDI+ 绘制旋转角为顺时针正方向，此处取反换算
                PhiDegrees = -(phi + deltaAngle) * 180.0 / Math.PI,
                HalfWidth = halfWidth,
                HalfHeight = halfHeight
            };
        }

        private static void GetRegionGeometry(RegionDefinition region,
            out double halfWidth, out double halfHeight, out double phi)
        {
            halfWidth = 0.0;
            halfHeight = 0.0;
            phi = 0.0;
            if (region == null) return;
            switch (region.Kind)
            {
                case RegionKind.Rectangle1:
                    halfWidth = region.Length1;
                    halfHeight = region.Length2;
                    break;
                case RegionKind.Rectangle2:
                    halfWidth = region.Length1;
                    halfHeight = region.Length2;
                    phi = region.PhiRadians;
                    break;
                case RegionKind.Circle:
                    halfWidth = region.Radius;
                    halfHeight = region.Radius;
                    break;
                case RegionKind.Polygon:
                case RegionKind.Freehand:
                    if (region.Points == null || region.Points.Count == 0) break;
                    halfWidth = (region.Points.Max(point => point.Column) -
                        region.Points.Min(point => point.Column)) / 2.0;
                    halfHeight = (region.Points.Max(point => point.Row) -
                        region.Points.Min(point => point.Row)) / 2.0;
                    break;
            }
        }

        private static DetectionRoi ToAxisAlignedBounds(DetectionRoi source)
        {
            double angle = DegreesToRadians(source.PhiDegrees);
            double cos = Math.Abs(Math.Cos(angle));
            double sin = Math.Abs(Math.Sin(angle));
            return new DetectionRoi
            {
                ReferenceCenterRow = source.ReferenceCenterRow,
                ReferenceCenterColumn = source.ReferenceCenterColumn,
                PhiDegrees = 0.0,
                HalfWidth = cos * source.HalfWidth + sin * source.HalfHeight,
                HalfHeight = sin * source.HalfWidth + cos * source.HalfHeight
            };
        }

        private static RegionDefinition CreateReferenceImageRegion(TemplateDefinition template)
        {
            if (template == null || template.ReferenceImageWidth <= 0 ||
                template.ReferenceImageHeight <= 0) return null;
            return new RegionDefinition
            {
                Kind = RegionKind.Rectangle1,
                CenterRow = template.ReferenceImageHeight / 2.0,
                CenterColumn = template.ReferenceImageWidth / 2.0,
                Length1 = template.ReferenceImageWidth / 2.0,
                Length2 = template.ReferenceImageHeight / 2.0
            };
        }

        private static Rectangle GetBounds(Size imageSize, DetectionRoi roi)
        {
            double angle = DegreesToRadians(roi.PhiDegrees);
            double horizontalRadius = Math.Abs(Math.Cos(angle)) * roi.HalfWidth +
                Math.Abs(Math.Sin(angle)) * roi.HalfHeight;
            double verticalRadius = Math.Abs(Math.Sin(angle)) * roi.HalfWidth +
                Math.Abs(Math.Cos(angle)) * roi.HalfHeight;
            int left = Math.Max(0, (int)Math.Floor(roi.ReferenceCenterColumn - horizontalRadius));
            int top = Math.Max(0, (int)Math.Floor(roi.ReferenceCenterRow - verticalRadius));
            int right = Math.Min(imageSize.Width, (int)Math.Ceiling(roi.ReferenceCenterColumn + horizontalRadius));
            int bottom = Math.Min(imageSize.Height, (int)Math.Ceiling(roi.ReferenceCenterRow + verticalRadius));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static DetectionRoi CreateFullImageRoi(Bitmap source)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0) return null;
            return new DetectionRoi
            {
                ReferenceCenterRow = source.Height / 2.0,
                ReferenceCenterColumn = source.Width / 2.0,
                PhiDegrees = 0.0,
                HalfWidth = source.Width / 2.0,
                HalfHeight = source.Height / 2.0
            };
        }

        private static bool IsValid(DetectionRoi roi)
        {
            return roi != null && IsFinite(roi.ReferenceCenterRow) &&
                IsFinite(roi.ReferenceCenterColumn) && IsFinite(roi.PhiDegrees) &&
                IsFinite(roi.HalfWidth) && IsFinite(roi.HalfHeight) &&
                roi.HalfWidth > 0.0 && roi.HalfHeight > 0.0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double DegreesToRadians(double value)
        {
            return value * Math.PI / 180.0;
        }
    }
}
