using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// Uses ordered, filtered XLD edge contours for newly-created shape templates.
    /// Existing image-domain shape templates and NCC templates retain the legacy engine behavior.
    /// </summary>
    public sealed class XldTemplateMatchingEngine : ITemplateMatchingEngine
    {
        private const string XldMetric = "ignore_local_polarity";
        private readonly HalconTemplateEngine _legacyEngine = new HalconTemplateEngine();
        private bool _disposed;

        public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            ThrowIfDisposed();
            if (definition == null || definition.ModelType != TemplateModelType.Shape)
            {
                return _legacyEngine.BuildTemplate(referenceImage, definition);
            }

            ShapeFeatureExtractionSettings extraction = definition.ShapeParameters == null
                ? null
                : definition.ShapeParameters.FeatureExtraction;
            if (extraction == null || extraction.Mode == ShapeFeatureExtractionMode.LegacyImageDomain)
            {
                return _legacyEngine.BuildTemplate(referenceImage, definition);
            }

            if (extraction.Mode != ShapeFeatureExtractionMode.EdgeContours)
            {
                return TemplateBuildResult.Failure(
                    CalibrationErrorCode.ParameterInvalid,
                    "Unsupported shape feature extraction mode: " + extraction.Mode + ".");
            }

            return BuildXldShapeTemplate(referenceImage, definition);
        }

        public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            ThrowIfDisposed();
            return _legacyEngine.Locate(image, definition, includeContour);
        }

        public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            ThrowIfDisposed();
            return _legacyEngine.LocateAll(image, definition, includeContour);
        }

        public TemplateRegionPreviewResult PreviewEffectiveRegion(
            TemplateDefinition definition,
            int imageWidth,
            int imageHeight)
        {
            ThrowIfDisposed();
            return _legacyEngine.PreviewEffectiveRegion(definition, imageWidth, imageHeight);
        }

        public void ClearCache()
        {
            ThrowIfDisposed();
            _legacyEngine.ClearCache();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _legacyEngine.Dispose();
        }

        private static TemplateBuildResult BuildXldShapeTemplate(
            Bitmap referenceImage,
            TemplateDefinition definition)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (referenceImage == null)
            {
                return Complete(
                    TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null."),
                    stopwatch);
            }

            if (definition == null)
            {
                return Complete(
                    TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null."),
                    stopwatch);
            }

            HShapeModel shapeModel = null;
            try
            {
                TemplateDefinition output = definition.DeepClone();
                if (output.MatchParameters == null)
                {
                    output.MatchParameters = new MatchParameters();
                }

                ValidateShapeParameters(output);
                ShapeFeatureExtractionSettings extraction = Normalize(output.ShapeParameters.FeatureExtraction);
                output.ShapeParameters.FeatureExtraction = extraction;
                output.ShapeParameters.Metric = XldMetric;

                using (HImage gray = HalconImageConverter.ToGrayHImage(referenceImage))
                using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(
                    output,
                    referenceImage.Width,
                    referenceImage.Height))
                using (HImage reduced = gray.ReduceDomain(effectiveRegion))
                {
                    double effectiveArea;
                    double domainRow;
                    double domainColumn;
                    HalconRegionBuilder.GetAreaCenter(
                        effectiveRegion,
                        out effectiveArea,
                        out domainRow,
                        out domainColumn);

                    bool anchorWasUninitialized = output.ReferenceImageWidth <= 0
                        && output.ReferenceImageHeight <= 0
                        && output.ReferenceAnchor.Row == 0.0
                        && output.ReferenceAnchor.Column == 0.0
                        && !output.IsReferenceAnchorLocked;

                    output.ReferenceImageWidth = referenceImage.Width;
                    output.ReferenceImageHeight = referenceImage.Height;
                    output.DomainCenter = new ImagePoint(domainRow, domainColumn);
                    // The model pose must use the user-defined ROI center.
                    // The effective-domain centroid changes when a rotated
                    // ROI is clipped or contains mask holes, which otherwise
                    // makes the feature overlay drift away from the ROI.
                    ImagePoint templateCenter = new ImagePoint(
                        output.TemplateRoi.CenterRow,
                        output.TemplateRoi.CenterColumn);
                    output.ModelReference = new ImagePose(
                        templateCenter.Row,
                        templateCenter.Column,
                        0.0);
                    if (anchorWasUninitialized)
                    {
                        output.ReferenceAnchor = templateCenter;
                    }

                    List<List<ImagePoint>> sampledContours = XldFeatureExtractor.Extract(
                        reduced,
                        extraction,
                        output.TemplateRoi);
                    using (HXLDCont xldContours = XldFeatureExtractor.CreateXld(sampledContours))
                    {
                        shapeModel = CreateShapeModel(xldContours, output.ShapeParameters);
                    }

                    SetModelOrigin(
                        shapeModel,
                        sampledContours,
                        templateCenter.Row,
                        templateCenter.Column);

                    HShapeModel writeModel = shapeModel;
                    byte[] modelData = HalconModelFile.Write(".shm", writeModel.WriteShapeModel);
                    output.SetModelData(modelData, TryGetHalconVersion());

                    return Complete(new TemplateBuildResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Message = string.Format(
                            "Template model was created from {0} ordered XLD contour segments.",
                            sampledContours.Count),
                        Template = output,
                        EffectiveArea = effectiveArea,
                        // Keep the exact sampled image-space points for the
                        // editor preview. GetShapeModelContours() returns
                        // model-space contours after HALCON has applied the
                        // shape-model origin, so using those contours here can
                        // make the visible features drift from the ROI.
                        ContourSegments = sampledContours
                    }, stopwatch);
                }
            }
            catch (Exception exception)
            {
                return Complete(
                    TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)),
                    stopwatch);
            }
            finally
            {
                if (shapeModel != null) shapeModel.Dispose();
            }
        }

        private static HShapeModel CreateShapeModel(
            HXLDCont contours,
            ShapeModelParameters parameters)
        {
            HShapeModel model = new HShapeModel();
            try
            {
                using (HTuple levels = AutoOrPositive(parameters.NumLevels))
                using (HTuple angleStep = AutoOrPositive(parameters.AngleStepRad))
                using (HTuple optimization = new HTuple(DefaultIfBlank(parameters.Optimization, "auto")))
                {
                    model.CreateShapeModelXld(
                        contours,
                        levels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        angleStep,
                        optimization,
                        parameters.Metric,
                        parameters.MinContrast);
                }

                return model;
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }

        private static void SetModelOrigin(
            HShapeModel model,
            IList<List<ImagePoint>> contours,
            double targetRow,
            double targetColumn)
        {
            bool hasPoint = false;
            double minRow = double.MaxValue;
            double maxRow = double.MinValue;
            double minColumn = double.MaxValue;
            double maxColumn = double.MinValue;

            foreach (List<ImagePoint> segment in contours ?? new List<List<ImagePoint>>())
            {
                if (segment == null) continue;
                foreach (ImagePoint point in segment)
                {
                    hasPoint = true;
                    minRow = Math.Min(minRow, point.Row);
                    maxRow = Math.Max(maxRow, point.Row);
                    minColumn = Math.Min(minColumn, point.Column);
                    maxColumn = Math.Max(maxColumn, point.Column);
                }
            }

            if (!hasPoint)
            {
                throw new XldFeatureExtractionException("No valid XLD feature points were extracted.");
            }

            double defaultOriginRow = (minRow + maxRow) * 0.5;
            double defaultOriginColumn = (minColumn + maxColumn) * 0.5;
            model.SetShapeModelOrigin(
                targetRow - defaultOriginRow,
                targetColumn - defaultOriginColumn);
        }

        private static ShapeFeatureExtractionSettings Normalize(ShapeFeatureExtractionSettings source)
        {
            ShapeFeatureExtractionSettings defaults = new ShapeFeatureExtractionSettings();
            ShapeFeatureExtractionSettings output = source == null
                ? defaults
                : source.DeepClone();

            output.Mode = ShapeFeatureExtractionMode.EdgeContours;
            if (!IsFinite(output.EdgeSigma) || output.EdgeSigma <= 0.0)
                output.EdgeSigma = defaults.EdgeSigma;
            if (!IsFinite(output.EdgeLowThreshold) || output.EdgeLowThreshold <= 0.0)
                output.EdgeLowThreshold = defaults.EdgeLowThreshold;
            if (!IsFinite(output.EdgeHighThreshold) || output.EdgeHighThreshold <= 0.0)
                output.EdgeHighThreshold = defaults.EdgeHighThreshold;
            if (output.EdgeHighThreshold <= output.EdgeLowThreshold)
            {
                throw new ArgumentException(
                    "Edge high threshold must be greater than the low threshold.",
                    "FeatureExtraction.EdgeHighThreshold");
            }
            if (!IsFinite(output.MinimumContourLengthPixels) || output.MinimumContourLengthPixels <= 0.0)
                output.MinimumContourLengthPixels = defaults.MinimumContourLengthPixels;
            if (!IsFinite(output.PointSpacingPixels) || output.PointSpacingPixels <= 0.0)
                output.PointSpacingPixels = defaults.PointSpacingPixels;
            if (output.MaximumPointCount < 4)
                output.MaximumPointCount = defaults.MaximumPointCount;
            return output;
        }

        private static void ValidateShapeParameters(TemplateDefinition definition)
        {
            if (definition.TemplateRoi == null)
                throw new ArgumentException("Template ROI is not configured.");
            if (definition.ShapeParameters == null)
                throw new ArgumentException("Shape model parameters are not configured.");

            ShapeModelParameters parameters = definition.ShapeParameters;
            ValidateAngleRange(parameters.AngleStartRad, parameters.AngleExtentRad);
            if (parameters.NumLevels < 0) throw new ArgumentOutOfRangeException("NumLevels");
            if (!IsFinite(parameters.AngleStepRad) || parameters.AngleStepRad < 0.0)
                throw new ArgumentOutOfRangeException("AngleStepRad");
            if (parameters.MinContrast < 0) throw new ArgumentOutOfRangeException("MinContrast");
        }

        private static void ValidateAngleRange(double start, double extent)
        {
            if (!IsFinite(start)) throw new ArgumentOutOfRangeException("AngleStartRad");
            if (!IsFinite(extent)
                || Math.Abs(extent) < 1e-12
                || Math.Abs(extent) > Math.PI * 2.0 + 1e-9)
            {
                throw new ArgumentOutOfRangeException(
                    "AngleExtentRad",
                    "Angle extent must be non-zero and no greater than 2 PI.");
            }
        }

        private static HTuple AutoOrPositive(int value)
        {
            return value <= 0 ? new HTuple("auto") : new HTuple(value);
        }

        private static HTuple AutoOrPositive(double value)
        {
            return value <= 0.0 ? new HTuple("auto") : new HTuple(value);
        }

        private static string DefaultIfBlank(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string TryGetHalconVersion()
        {
            try
            {
                using (HTuple version = HSystem.GetSystem("version"))
                {
                    return version.Length > 0 ? version[0].S : null;
                }
            }
            catch
            {
                return "19.11";
            }
        }

        private static CalibrationErrorCode MapErrorCode(Exception exception)
        {
            if (exception is HOperatorException) return CalibrationErrorCode.HalconRuntimeError;
            if (exception is XldFeatureExtractionException) return CalibrationErrorCode.ParameterInvalid;
            if (exception is ArgumentException) return CalibrationErrorCode.ParameterInvalid;
            if (exception is InvalidOperationException) return CalibrationErrorCode.SerializationError;
            return CalibrationErrorCode.InternalError;
        }

        private static string FormatException(Exception exception)
        {
            HOperatorException halcon = exception as HOperatorException;
            return halcon == null
                ? exception.Message
                : "HALCON error " + halcon.GetErrorCode() + ": " + halcon.GetErrorMessage();
        }

        private static TemplateBuildResult Complete(TemplateBuildResult result, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(XldTemplateMatchingEngine));
        }
    }

    internal static class XldFeatureExtractor
    {
        public static List<List<ImagePoint>> Extract(
            HImage reducedImage,
            ShapeFeatureExtractionSettings settings,
            RegionDefinition templateRoi)
        {
            if (reducedImage == null) throw new ArgumentNullException(nameof(reducedImage));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (templateRoi == null) throw new ArgumentNullException(nameof(templateRoi));

            double contourSelectionLength = Math.Max(
                settings.MinimumContourLengthPixels,
                Math.Max(6.0, settings.PointSpacingPixels * 1.5));
            using (HTuple low = new HTuple(settings.EdgeLowThreshold))
            using (HTuple high = new HTuple(settings.EdgeHighThreshold))
            using (HXLDCont edges = reducedImage.EdgesSubPix("canny", settings.EdgeSigma, low, high))
            using (HXLDCont selected = edges.SelectShapeXld(
                "contlength",
                "and",
                contourSelectionLength,
                1.0e30))
            {
                List<ContourSource> sources = ClipContoursToRoi(
                        HalconContourConverter.Extract(selected),
                        templateRoi)
                    .Select((points, index) => CreateContourSource(points, index, templateRoi))
                    .Where(item => item.Points != null
                        && item.Points.Count >= 4
                        && item.Length >= Math.Max(8.0, settings.PointSpacingPixels * 2.0))
                    .OrderByDescending(item => item.Score)
                    .ToList();

                if (sources.Count == 0)
                {
                    throw new XldFeatureExtractionException(
                        "No usable Canny XLD contours were found in the effective template ROI.");
                }

                double modelPointSpacing = Math.Max(0.5, settings.PointSpacingPixels * 0.15);
                List<List<ImagePoint>> sampled = BuildProportionalPointListFromSources(
                    sources,
                    modelPointSpacing,
                    settings.MaximumPointCount);

                if (sampled.Count == 0 || CountPoints(sampled) < 4)
                {
                    throw new XldFeatureExtractionException(
                        "Too few ordered XLD feature points remain after contour filtering.");
                }

                return sampled;
            }
        }

        private static IEnumerable<List<ImagePoint>> ClipContoursToRoi(
            IEnumerable<List<ImagePoint>> contours,
            RegionDefinition roi)
        {
            foreach (List<ImagePoint> contour in contours ?? Enumerable.Empty<List<ImagePoint>>())
            {
                if (contour == null || contour.Count < 2)
                {
                    continue;
                }

                List<ImagePoint> inside = new List<ImagePoint>();
                foreach (ImagePoint point in contour)
                {
                    if (IsInsideRoi(point, roi))
                    {
                        inside.Add(point);
                    }
                }

                // HALCON's reduced domain normally clips the contour already.
                // Keep one ordered candidate when a few samples lie just outside
                // the ROI; splitting at every boundary point can discard the
                // board edge in exactly the cases this extractor is meant to keep.
                if (inside.Count >= 2)
                {
                    yield return inside;
                }
            }
        }

        private static bool IsInsideRoi(ImagePoint point, RegionDefinition roi)
        {
            double deltaRow = point.Row - roi.CenterRow;
            double deltaColumn = point.Column - roi.CenterColumn;
            double cosine = Math.Cos(roi.PhiRadians);
            double sine = Math.Sin(roi.PhiRadians);
            double localRow = (cosine * deltaRow) + (sine * deltaColumn);
            double localColumn = (-sine * deltaRow) + (cosine * deltaColumn);
            const double tolerance = 1.5;
            return Math.Abs(localColumn) <= roi.Length1 + tolerance
                && Math.Abs(localRow) <= roi.Length2 + tolerance;
        }

        private static void GetLocalCoordinates(
            ImagePoint point,
            RegionDefinition roi,
            out double localRow,
            out double localColumn)
        {
            double deltaRow = point.Row - roi.CenterRow;
            double deltaColumn = point.Column - roi.CenterColumn;
            double cosine = Math.Cos(roi.PhiRadians);
            double sine = Math.Sin(roi.PhiRadians);
            localRow = (cosine * deltaRow) + (sine * deltaColumn);
            localColumn = (-sine * deltaRow) + (cosine * deltaColumn);
        }

        private static ContourSource CreateContourSource(
            List<ImagePoint> points,
            int index,
            RegionDefinition roi)
        {
            ContourSource source = new ContourSource
            {
                Index = index,
                Points = points,
                Length = GetLength(points)
            };
            if (points == null || points.Count == 0)
            {
                return source;
            }

            double minRow = double.PositiveInfinity;
            double maxRow = double.NegativeInfinity;
            double minColumn = double.PositiveInfinity;
            double maxColumn = double.NegativeInfinity;
            foreach (ImagePoint point in points)
            {
                double localRow;
                double localColumn;
                GetLocalCoordinates(point, roi, out localRow, out localColumn);
                minRow = Math.Min(minRow, localRow);
                maxRow = Math.Max(maxRow, localRow);
                minColumn = Math.Min(minColumn, localColumn);
                maxColumn = Math.Max(maxColumn, localColumn);
            }

            source.Coverage = Math.Max(0.0, maxRow - minRow)
                * Math.Max(0.0, maxColumn - minColumn);
            return source;
        }

        public static HXLDCont CreateXld(IList<List<ImagePoint>> segments)
        {
            HXLDCont combined = null;
            try
            {
                foreach (List<ImagePoint> segment in segments ?? new List<List<ImagePoint>>())
                {
                    if (segment == null || segment.Count < 2) continue;
                    double[] rows = segment.Select(point => point.Row).ToArray();
                    double[] columns = segment.Select(point => point.Column).ToArray();
                    using (HTuple rowTuple = new HTuple(rows))
                    using (HTuple columnTuple = new HTuple(columns))
                    using (HXLDCont contour = new HXLDCont(rowTuple, columnTuple))
                    {
                        if (combined == null)
                        {
                            combined = contour.CopyObj(1, -1);
                        }
                        else
                        {
                            HXLDCont next = combined.ConcatObj(contour);
                            combined.Dispose();
                            combined = next;
                        }
                    }
                }

                if (combined == null)
                {
                    throw new XldFeatureExtractionException(
                        "No XLD contours are available for model creation.");
                }

                return combined;
            }
            catch
            {
                if (combined != null) combined.Dispose();
                throw;
            }
        }

        internal static List<ImagePoint> ResampleOrderedContour(
            IList<ImagePoint> points,
            double spacing,
            int maximumCount = int.MaxValue)
        {
            List<ImagePoint> result = new List<ImagePoint>();
            if (points == null || points.Count == 0 || maximumCount <= 0)
            {
                return result;
            }

            double minimumDistance = Math.Max(0.0, spacing);
            double minimumDistanceSquared = minimumDistance * minimumDistance;
            result.Add(points[0]);
            for (int index = 1; index < points.Count; index++)
            {
                ImagePoint point = points[index];
                double distance = result[result.Count - 1].DistanceTo(point);
                if (distance * distance >= minimumDistanceSquared)
                {
                    result.Add(point);
                }
            }

            if (result.Count > maximumCount)
            {
                if (maximumCount == 1)
                {
                    return new List<ImagePoint> { result[0] };
                }
                List<ImagePoint> limited = new List<ImagePoint>(maximumCount);
                double step = (double)(result.Count - 1) / (maximumCount - 1);
                for (int index = 0; index < maximumCount; index++)
                {
                    limited.Add(result[(int)Math.Round(index * step)]);
                }
                result = limited;
            }

            return result;
        }

        private static List<List<ImagePoint>> BuildProportionalPointListFromSources(
            IList<ContourSource> sources,
            double spacing,
            int maximumPointCount)
        {
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            if (sources == null || sources.Count == 0 || maximumPointCount < 2)
            {
                return result;
            }

            int maximumSegmentCount = Math.Max(
                16,
                Math.Min(2000, maximumPointCount / 4));
            List<ContourSource> ordered = sources
                .Where(source => source != null
                    && source.Points != null
                    && source.Points.Count >= 2)
                .OrderByDescending(source => source.Score)
                .Take(maximumSegmentCount)
                .ToList();
            if (ordered.Count == 0)
            {
                return result;
            }

            double totalLength = ordered.Sum(source => Math.Max(1.0, source.Length));
            foreach (ContourSource source in ordered)
            {
                int maximumForSegment = Math.Max(
                    4,
                    (int)Math.Round(
                        maximumPointCount * Math.Max(1.0, source.Length) / totalLength));
                List<ImagePoint> sampled = ResampleOrderedContour(
                    source.Points,
                    spacing,
                    maximumForSegment);
                if (sampled.Count < 2)
                {
                    continue;
                }

                result.Add(sampled);
                if (CountPoints(result) >= maximumPointCount)
                {
                    break;
                }
            }

            return LimitPointCountProportionally(result, maximumPointCount);
        }

        private static List<List<ImagePoint>> LimitPointCountProportionally(
            IList<List<ImagePoint>> segments,
            int maximumPointCount)
        {
            int total = CountPoints(segments);
            if (total <= maximumPointCount)
            {
                return segments.Select(segment => new List<ImagePoint>(segment)).ToList();
            }

            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            foreach (List<ImagePoint> segment in segments)
            {
                int keep = Math.Max(
                    2,
                    (int)Math.Round((double)segment.Count * maximumPointCount / total));
                List<ImagePoint> sampled = ResampleOrderedContour(segment, 1.0, keep);
                if (sampled.Count >= 2)
                {
                    result.Add(sampled);
                }
            }

            return result;
        }

        private static double GetLength(IList<ImagePoint> points)
        {
            double length = 0.0;
            if (points == null) return length;
            for (int index = 1; index < points.Count; index++)
            {
                length += points[index - 1].DistanceTo(points[index]);
            }

            return length;
        }

        private static int CountPoints(IEnumerable<List<ImagePoint>> segments)
        {
            return segments == null ? 0 : segments.Sum(segment => segment == null ? 0 : segment.Count);
        }

        private sealed class ContourSource
        {
            public int Index { get; set; }
            public List<ImagePoint> Points { get; set; }
            public double Length { get; set; }
            public double Coverage { get; set; }
            public double Score { get { return Length + Coverage * 0.05; } }
        }
    }

    internal sealed class XldFeatureExtractionException : InvalidOperationException
    {
        public XldFeatureExtractionException(string message)
            : base(message)
        {
        }
    }
}
