using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    public sealed class BoardBlobAnalyzer
    {
        public List<BlobFinding> Analyze(Bitmap source, TemplateDefinition template,
            TemplateMatchResult match, DetectionProfile profile)
        {
            using (BlobAnalysisResult result = AnalyzeDetailed(source, template, match,
                profile, false, CancellationToken.None))
                return result.Findings;
        }

        public BlobAnalysisResult AnalyzeDetailed(Bitmap source, TemplateDefinition template,
            TemplateMatchResult match, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AnalyzeRoiDetailed(source, ResolveDetectionRoi(template, match, profile),
                profile, includeBinary, cancellationToken);
        }

        public BlobAnalysisResult AnalyzeDirectDetailed(Bitmap source, TemplateDefinition template,
            DetectionProfile profile, bool includeBinary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AnalyzeRoiDetailed(source, CreateFullImageRoi(source), profile,
                includeBinary, cancellationToken);
        }

        public BlobAnalysisResult AnalyzeRoiDetailed(Bitmap source, DetectionRoi roi,
            DetectionProfile profile, bool includeBinary, CancellationToken cancellationToken)
        {
            List<BlobAnalysisResult> results = AnalyzeRoisDetailed(source, new[] { roi },
                profile, includeBinary, cancellationToken);
            return results.Count == 0 ? new BlobAnalysisResult { Roi = roi } : results[0];
        }

        public List<BlobAnalysisResult> AnalyzeRoisDetailed(Bitmap source,
            IList<DetectionRoi> rois, DetectionProfile profile, bool includeBinary,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source == null || profile == null || rois == null || rois.Count == 0)
                return new List<BlobAnalysisResult>();

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
                ImageBounds = GetBounds(source.Size, roi)
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
                halconResults = HalconBlobEngine.Analyze(source, regions, parameters,
                    includeBinary, cancellationToken);
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
