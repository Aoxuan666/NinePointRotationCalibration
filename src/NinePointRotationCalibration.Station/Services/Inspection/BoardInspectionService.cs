using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    public sealed class BoardInspectionService
    {
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly BoardBlobAnalyzer _blobAnalyzer;
        private readonly TargetPoseMapper _poseMapper;

        public BoardInspectionService(NinePointRotationCalibrationSdk sdk)
        {
            _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            _blobAnalyzer = new BoardBlobAnalyzer();
            _poseMapper = new TargetPoseMapper();
        }

        public InspectionResult Inspect(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult calibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentR)
        {
            return InspectCore(
                image,
                positioningJob,
                calibration,
                teaching,
                profile,
                expectedCount,
                currentR,
                true);
        }

        public InspectionResult InspectWithoutMapping(
            Bitmap image,
            CalibrationJob positioningJob,
            DetectionProfile profile,
            int expectedCount)
        {
            return InspectCore(
                image,
                positioningJob,
                null,
                null,
                profile,
                expectedCount,
                0.0,
                false);
        }

        private InspectionResult InspectCore(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult calibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentR,
            bool mapTargets)
        {
            Stopwatch watch = Stopwatch.StartNew();
            InspectionResult result = new InspectionResult
            {
                ExpectedCount = expectedCount,
                CurrentR = currentR
            };

            try
            {
                if (image == null) return Failure(result, "IMAGE_MISSING", "No image was captured.");
                if (profile == null)
                    return Failure(result, "DETECTION_PROFILE_INVALID", "Detection profile is not configured.");

                bool useTemplateMatching = profile.TemplateMatchingEnabled;
                if (useTemplateMatching && (positioningJob == null || positioningJob.Template == null))
                    return Failure(result, "POSITIONING_TEMPLATE_MISSING", "Positioning template is not configured.");
                if (useTemplateMatching && mapTargets &&
                    (calibration == null || !calibration.TranslationSuccess))
                    return Failure(result, "CALIBRATION_MISSING", "A valid XY calibration result is required.");
                if (useTemplateMatching && mapTargets &&
                    (calibration.Rotation == null || !calibration.Rotation.Success ||
                    !calibration.Rotation.IsAvailable ||
                    Math.Abs(calibration.Rotation.DirectionMultiplier) < 0.5))
                {
                    return Failure(result, "CALIBRATION_MISSING",
                        "A valid rotation calibration result is required for R-axis mapping.");
                }
                if (useTemplateMatching && mapTargets &&
                    (!IsUsable(teaching == null ? null : teaching.Left) ||
                    !IsUsable(teaching == null ? null : teaching.Right)))
                {
                    return Failure(result, "TEACHING_MISSING",
                        "Valid left and right teaching data are required.");
                }
                if (useTemplateMatching && mapTargets && !IsFinite(currentR))
                    return Failure(result, "CURRENT_R_INVALID", "A finite current R value is required for mapping.");
                if (useTemplateMatching && profile.FollowTemplateTransform &&
                    positioningJob.Template.DetectionRoi == null)
                {
                    return Failure(result, "DETECTION_ROI_MISSING",
                        "Create the detection ROI in the positioning template editor first.");
                }
                if (useTemplateMatching && !profile.FollowTemplateTransform &&
                    positioningJob.Template.TemplateRoi == null &&
                    (positioningJob.Template.ReferenceImageWidth <= 0 ||
                     positioningJob.Template.ReferenceImageHeight <= 0))
                {
                    return Failure(result, "BOARD_BOUNDS_MISSING",
                        "The positioning template does not contain usable board bounds.");
                }
                if (expectedCount <= 0)
                    return Failure(result, "EXPECTED_COUNT_INVALID", "Expected board count must be greater than zero.");

                result.Executed = true;
                if (!useTemplateMatching)
                {
                    using (BlobAnalysisResult analysis = _blobAnalyzer.AnalyzeDirectDetailed(
                        image,
                        positioningJob == null ? null : positioningJob.Template,
                        profile,
                        false,
                        CancellationToken.None))
                    {
                        BoardInspectionItem item = new BoardInspectionItem
                        {
                            BoardIndex = 1,
                            ImageRow = image.Height / 2.0,
                            ImageColumn = image.Width / 2.0,
                            ImageAngleDegrees = 0.0,
                            MatchScore = 0.0,
                            DetectionRegion = analysis.Roi
                        };
                        item.Blobs.AddRange(analysis.Findings);
                        result.BlobElapsedMilliseconds += analysis.ElapsedMilliseconds;
                        AccumulateAnalysisTiming(result, analysis);
                        item.HasManualMark = item.Blobs.Exists(blob => !blob.RejectedAsRectangle);
                        result.Items.Add(item);
                    }
                    result.ActualCount = 1;
                }
                else
                {
                    CalibrationJob locateJob = positioningJob.DeepClone();
                    // Keep one extra candidate so an over-count still reports NG while
                    // avoiding an unbounded HALCON result set.
                    locateJob.Template.MatchParameters.NumMatches = expectedCount == int.MaxValue
                        ? expectedCount
                        : expectedCount + 1;
                    Stopwatch matchWatch = Stopwatch.StartNew();
                    TemplateMatchCollectionResult matches = _sdk.LocateAll(image, locateJob, true);
                    matchWatch.Stop();
                    result.MatchElapsedMilliseconds = matchWatch.Elapsed.TotalMilliseconds;
                    if (matches == null || !matches.Success)
                    {
                        string message = matches == null ? "Positioning returned no result." : matches.Message;
                        return Failure(result, "POSITIONING_FAILED", message);
                    }

                    List<TemplateMatchResult> orderedMatches = OrderMatches(
                        matches.Matches,
                        locateJob.Template,
                        profile);
                    if (matches.Matches != null && orderedMatches.Count != matches.Matches.Count)
                    {
                        return Failure(result, "POSITIONING_FAILED", "Positioning returned an invalid board pose.");
                    }

                    result.ActualCount = orderedMatches.Count;
                    List<DetectionRoi> detectionRois = orderedMatches
                        .Select(match => _blobAnalyzer.ResolveDetectionRoi(
                            locateJob.Template,
                            match,
                            profile))
                        .ToList();
                    List<BlobAnalysisResult> analyses = _blobAnalyzer.AnalyzeRoisDetailed(
                        image,
                        detectionRois,
                        profile,
                        false,
                        CancellationToken.None);
                    for (int index = 0; index < orderedMatches.Count; index++)
                    {
                        TemplateMatchResult match = orderedMatches[index];
                        BoardInspectionItem item = new BoardInspectionItem
                        {
                            BoardIndex = index + 1,
                            ImageRow = match.Row,
                            ImageColumn = match.Column,
                            ImageAngleDegrees = match.AngleDegrees,
                            MatchScore = match.Score
                        };
                        CopyContourSegments(match, item);
                        using (BlobAnalysisResult analysis = analyses[index])
                        {
                            item.DetectionRegion = analysis.Roi;
                            item.Blobs.AddRange(analysis.Findings);
                            result.BlobElapsedMilliseconds += analysis.ElapsedMilliseconds;
                            AccumulateAnalysisTiming(result, analysis);
                        }
                        item.HasManualMark = item.Blobs.Exists(blob => !blob.RejectedAsRectangle);
                        if (mapTargets)
                            item.Targets.AddRange(_poseMapper.Map(calibration, teaching, match, currentR));
                        result.Items.Add(item);
                    }
                }

                result.CountMatchesExpected = result.ActualCount == result.ExpectedCount;
                result.HasManualMarks = result.Items.Exists(item => item.HasManualMark);
                result.Success = result.CountMatchesExpected;
                result.Pass = result.CountMatchesExpected && !result.HasManualMarks;
                if (!result.CountMatchesExpected)
                {
                    result.ErrorCode = "BOARD_COUNT_MISMATCH";
                    result.Message = string.Format(
                        "Expected {0} boards but located {1}.", result.ExpectedCount, result.ActualCount);
                }
                else if (result.HasManualMarks)
                {
                    result.ErrorCode = "MANUAL_MARK_FOUND";
                    result.Message = "Manual white mark detected.";
                }
                else
                {
                    result.ErrorCode = "NONE";
                    result.Message = "Inspection completed.";
                }
                return result;
            }
            catch (Exception exception)
            {
                return Failure(result, "INSPECTION_EXCEPTION", exception.Message);
            }
            finally
            {
                watch.Stop();
                result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
            }
        }

        private static InspectionResult Failure(InspectionResult result, string code, string message)
        {
            result.Success = false;
            result.Pass = false;
            result.ErrorCode = code;
            result.Message = message;
            return result;
        }

        private static void AccumulateAnalysisTiming(
            InspectionResult inspection,
            BlobAnalysisResult analysis)
        {
            if (inspection == null || analysis == null) return;
            inspection.ImageConversionElapsedMilliseconds += analysis.ImageConversionElapsedMilliseconds;
            inspection.ThresholdElapsedMilliseconds += analysis.ThresholdElapsedMilliseconds;
            inspection.MorphologyElapsedMilliseconds += analysis.MorphologyElapsedMilliseconds;
            inspection.HoleFillElapsedMilliseconds += analysis.HoleFillElapsedMilliseconds;
            inspection.ComponentElapsedMilliseconds += analysis.ComponentElapsedMilliseconds;
        }

        private static bool IsUsable(TeachingPose pose)
        {
            return pose != null && pose.IsValid &&
                IsFinite(pose.ImageRow) && IsFinite(pose.ImageColumn) &&
                IsFinite(pose.ImageAngleDegrees) && IsFinite(pose.MachineX) &&
                IsFinite(pose.MachineY) && IsFinite(pose.MachineR);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void CopyContourSegments(
            TemplateMatchResult match,
            BoardInspectionItem item)
        {
            if (match == null || item == null || match.ContourSegments == null) return;
            foreach (List<ImagePoint> segment in match.ContourSegments)
            {
                if (segment == null || segment.Count == 0) continue;
                item.MatchContourSegments.Add(new List<ImagePoint>(segment));
            }
        }

        private static List<TemplateMatchResult> OrderMatches(
            IEnumerable<TemplateMatchResult> matches,
            TemplateDefinition template,
            DetectionProfile profile)
        {
            List<TemplateMatchResult> candidates = matches == null
                ? new List<TemplateMatchResult>()
                : matches.Where(match => match != null && match.Success &&
                    IsFinite(match.Row) && IsFinite(match.Column) && IsFinite(match.AngleDegrees))
                    .OrderBy(match => match.Row)
                    .ThenBy(match => match.Column)
                    .ToList();
            if (candidates.Count < 2) return candidates;

            double tolerance = GetRowTolerance(template, profile);
            List<MatchRow> rows = new List<MatchRow>();
            foreach (TemplateMatchResult match in candidates)
            {
                MatchRow row = rows
                    .Where(candidate => Math.Abs(match.Row - candidate.CenterRow) <= tolerance)
                    .OrderBy(candidate => Math.Abs(match.Row - candidate.CenterRow))
                    .FirstOrDefault();
                if (row == null)
                {
                    row = new MatchRow();
                    rows.Add(row);
                }
                row.Add(match);
            }

            return rows
                .OrderBy(row => row.CenterRow)
                .SelectMany(row => row.Matches
                    .OrderBy(match => match.Column)
                    .ThenBy(match => match.Row)
                    .ThenByDescending(match => match.Score))
                .ToList();
        }

        private static double GetRowTolerance(TemplateDefinition template, DetectionProfile profile)
        {
            double halfHeight = 0.0;
            if (profile != null && profile.FollowTemplateTransform &&
                template != null && template.DetectionRoi != null)
            {
                RegionDefinition detectionRegion = template.DetectionRoi;
                halfHeight = detectionRegion.Kind == RegionKind.Circle
                    ? detectionRegion.Radius
                    : Math.Max(detectionRegion.Length1, detectionRegion.Length2);
            }
            RegionDefinition region = template == null ? null : template.TemplateRoi;
            if (region != null)
            {
                switch (region.Kind)
                {
                    case RegionKind.Rectangle1:
                        halfHeight = Math.Max(halfHeight, region.Length2);
                        break;
                    case RegionKind.Rectangle2:
                        halfHeight = Math.Max(
                            halfHeight,
                            Math.Abs(Math.Sin(region.PhiRadians)) * region.Length1 +
                            Math.Abs(Math.Cos(region.PhiRadians)) * region.Length2);
                        break;
                    case RegionKind.Circle:
                        halfHeight = Math.Max(halfHeight, region.Radius);
                        break;
                    case RegionKind.Polygon:
                    case RegionKind.Freehand:
                        if (region.Points != null && region.Points.Count > 0)
                        {
                            double span = region.Points.Max(point => point.Row) -
                                region.Points.Min(point => point.Row);
                            halfHeight = Math.Max(halfHeight, span / 2.0);
                        }
                        break;
                }
            }
            return Math.Max(5.0, halfHeight * 0.75);
        }

        private sealed class MatchRow
        {
            private double _rowSum;

            public MatchRow()
            {
                Matches = new List<TemplateMatchResult>();
            }

            public List<TemplateMatchResult> Matches { get; private set; }
            public double CenterRow { get { return Matches.Count == 0 ? 0.0 : _rowSum / Matches.Count; } }

            public void Add(TemplateMatchResult match)
            {
                Matches.Add(match);
                _rowSum += match.Row;
            }
        }
    }
}
