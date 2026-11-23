using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    // One instance per immutable locked frame. Each inspection owns its native image reference.
    internal sealed class LockedFrameGrayCache : IDisposable
    {
        private readonly object _sync = new object();
        private HalconGrayImage _gray;
        private bool _disposed;

        internal HalconGrayImage Acquire(Bitmap snapshot)
        {
            lock (_sync)
            {
                if (_disposed) return HalconGrayImage.FromBitmap(snapshot);
                if (_gray != null) return _gray.Share();
                using (HalconGrayImage first = HalconGrayImage.FromBitmap(snapshot))
                {
                    _gray = first.Share();
                }
                return _gray.Share();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                if (_gray != null) _gray.Dispose();
                _gray = null;
            }
        }
    }

    /// <summary>负责 PCB 多板模板定位、ROI Blob 标记检测及双吸嘴目标位姿映射的检测服务。</summary>
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

        /// <summary>单标定兼容入口：定位板件、分析配置的 Blob，并将示教目标映射为机械坐标。</summary>
        public InspectionResult Inspect(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult calibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentR)
        {
            return Inspect(
                image,
                positioningJob,
                calibration,
                calibration,
                teaching,
                profile,
                expectedCount,
                currentR,
                currentR);
        }

        /// <summary>双 R 轴独立入口：使用左右吸嘴各自标定与当前 R1/R2 角度定位板件、分析 Blob 并映射目标坐标。</summary>
        public InspectionResult Inspect(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult leftCalibration,
            CalibrationResult rightCalibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentRLeft,
            double currentRRight)
        {
            return InspectCore(
                image,
                positioningJob,
                leftCalibration,
                rightCalibration,
                teaching,
                profile,
                expectedCount,
                currentRLeft,
                currentRRight,
                true);
        }

        /// <summary>执行视觉和 Blob 分析，不要求已有标定结果或目标映射数据。</summary>
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
                null,
                profile,
                expectedCount,
                0.0,
                0.0,
                false);
        }

        internal InspectionResult InspectCore(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult calibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentR,
            bool mapTargets,
            LockedFrameGrayCache frameCache = null)
        {
            return InspectCore(
                image,
                positioningJob,
                calibration,
                calibration,
                teaching,
                profile,
                expectedCount,
                currentR,
                currentR,
                mapTargets,
                frameCache);
        }

        internal InspectionResult InspectCore(
            Bitmap image,
            CalibrationJob positioningJob,
            CalibrationResult leftCalibration,
            CalibrationResult rightCalibration,
            TeachingData teaching,
            DetectionProfile profile,
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            bool mapTargets,
            LockedFrameGrayCache frameCache = null)
        {
            Stopwatch watch = Stopwatch.StartNew();
            InspectionResult result = new InspectionResult
            {
                ExpectedCount = expectedCount,
                CurrentRLeft = currentRLeft,
                CurrentRRight = currentRRight,
                CurrentR = currentRLeft
            };

            HalconGrayImage gray = null;
            try
            {
                if (image == null) return Failure(result, "IMAGE_MISSING", "No image was captured.");
                if (profile == null)
                    return Failure(result, "DETECTION_PROFILE_INVALID", "Detection profile is not configured.");

                bool useTemplateMatching = profile.TemplateMatchingEnabled;
                if (useTemplateMatching &&
                    (positioningJob == null ||
                     positioningJob.Template == null ||
                     positioningJob.Template.ModelData == null ||
                     positioningJob.Template.ModelData.Length == 0))
                    return Failure(result, ProtocolErrorCodes.PositioningTemplateMissing, "Positioning template is not configured.");
                if (useTemplateMatching && mapTargets && !IsCalibrationValid(leftCalibration))
                    return Failure(result, "CALIBRATION_MISSING", "A valid Left (R1) 9+5 calibration result is required.");
                if (useTemplateMatching && mapTargets && !IsCalibrationValid(rightCalibration))
                    return Failure(result, "CALIBRATION_MISSING", "A valid Right (R2) 9+5 calibration result is required.");
                if (useTemplateMatching && mapTargets &&
                    (!IsUsable(teaching == null ? null : teaching.Left) ||
                    !IsUsable(teaching == null ? null : teaching.Right)))
                {
                    return Failure(result, "TEACHING_MISSING",
                        "Valid left and right teaching data are required.");
                }
                if (useTemplateMatching && mapTargets && (!IsFinite(currentRLeft) || !IsFinite(currentRRight)))
                    return Failure(result, "CURRENT_R_INVALID", "Finite currentRLeft and currentRRight values are required for mapping.");
                if (useTemplateMatching &&
                    positioningJob.Template.DetectionRoi == null &&
                    positioningJob.Template.TemplateRoi == null &&
                    (positioningJob.Template.ReferenceImageWidth <= 0 ||
                     positioningJob.Template.ReferenceImageHeight <= 0))
                {
                    return Failure(result, "DETECTION_ROI_MISSING",
                        "定位模板尚未配置模板 ROI 或检测 ROI，请先在定位模板编辑器中框选区域。");
                }
                if (expectedCount <= 0)
                    return Failure(result, "EXPECTED_COUNT_INVALID", "Expected board count must be greater than zero.");

                LocalDebugLog.Write(
                    "INFO",
                    "inspect.start",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "image={0}x{1}, expected={2}, currentRLeft={3:0.###}, currentRRight={4:0.###}, mapTargets={5}, {6}",
                        image.Width,
                        image.Height,
                        expectedCount,
                        currentRLeft,
                        currentRRight,
                        mapTargets,
                        StationLogFormatter.FormatDetectionProfile(profile)));

                result.Executed = true;
                if (!useTemplateMatching)
                {
                    var directConv = Stopwatch.StartNew();
                    gray = frameCache == null ? HalconGrayImage.FromBitmap(image) : frameCache.Acquire(image);
                    result.ImageConversionElapsedMilliseconds += directConv.Elapsed.TotalMilliseconds;
                    using (BlobAnalysisResult analysis = _blobAnalyzer.AnalyzeDirectDetailed(
                        gray,
                        positioningJob == null ? null : positioningJob.Template,
                        profile,
                        false,
                        CancellationToken.None))
                    {
                        BoardInspectionItem item = new BoardInspectionItem
                        {
                            BoardIndex = 1,
                            Valid = false,
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
                        LogBoardBlobDetail(1, analysis, item.HasManualMark);
                    }
                    result.ActualCount = 1;
                }
                else
                {
                    // 仅需修改模板匹配上限；使用深拷贝以避免污染外部持有的定位 Job
                    TemplateDefinition locateTemplate = positioningJob.Template.DeepClone();
                    // 多允许 1 个匹配候选，以检测超板数 NG 并避免 HALCON 返回无界结果集
                    locateTemplate.MatchParameters.NumMatches = expectedCount == int.MaxValue
                        ? expectedCount
                        : expectedCount + 1;
                    // 整帧仅转换一次灰度图，供模板匹配与后续多 ROI Blob 分析共享
                    var conversion = Stopwatch.StartNew();
                    gray = frameCache == null ? HalconGrayImage.FromBitmap(image) : frameCache.Acquire(image);
                    result.ImageConversionElapsedMilliseconds += conversion.Elapsed.TotalMilliseconds;
                    Stopwatch matchWatch = Stopwatch.StartNew();
                    TemplateMatchCollectionResult matches = _sdk.LocateAll(image, gray, locateTemplate, true);
                    matchWatch.Stop();
                    result.MatchElapsedMilliseconds = matchWatch.Elapsed.TotalMilliseconds;
                    if (matches == null || (!matches.Success && !matches.IsNoMatch))
                    {
                        string message = matches == null ? "Positioning returned no result." : matches.Message;
                        return Failure(result, "POSITIONING_FAILED", message);
                    }

                    // 匹配引擎运行正常但视野内无任何达标目标 -> 正常空料工况（NO_MATERIAL），不报警停机
                    if (matches.IsNoMatch || matches.Matches == null || matches.Matches.Count == 0)
                    {
                        result.ActualCount = 0;
                        result.CountMatchesExpected = false;
                        result.HasManualMarks = false;
                        result.CoordinateMappingApplied = false;
                        result.Success = true;
                        result.Pass = true;
                        result.ErrorCode = ProtocolErrorCodes.NoMaterial;
                        result.Message = "视野内未检测到 PCB 来料（空料）";
                        LocalDebugLog.Write(
                            "INFO",
                            "inspect.match",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "No material detected (0 matches) in {0:0.0}ms (imgConv={1:0.0}ms)",
                                result.MatchElapsedMilliseconds,
                                result.ImageConversionElapsedMilliseconds));
                        return result;
                    }

                    List<TemplateMatchResult> orderedMatches = OrderMatches(
                        matches.Matches,
                        locateTemplate,
                        profile);
                    if (orderedMatches.Count != matches.Matches.Count)
                    {
                        return Failure(result, "POSITIONING_FAILED", "Positioning returned an invalid board pose.");
                    }

                    LocalDebugLog.Write(
                        "INFO",
                        "inspect.match",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Located {0} boards (limit={1}) in {2:0.0}ms (imgConv={3:0.0}ms)",
                            orderedMatches.Count,
                            locateTemplate.MatchParameters.NumMatches,
                            result.MatchElapsedMilliseconds,
                            result.ImageConversionElapsedMilliseconds));

                    result.ActualCount = orderedMatches.Count;
                    List<DetectionRoi> detectionRois = orderedMatches
                        .Select(match => _blobAnalyzer.ResolveDetectionRoi(
                            locateTemplate,
                            match,
                            profile))
                        .ToList();
                    List<BlobAnalysisResult> analyses = _blobAnalyzer.AnalyzeRoisDetailed(
                        gray,
                        detectionRois,
                        profile,
                        false,
                        CancellationToken.None);
                    try
                    {
                        if (analyses == null || analyses.Count != orderedMatches.Count)
                        {
                            return Failure(result, "BLOB_ANALYSIS_FAILED",
                                "Blob 分析没有为每个定位到的板件返回结果。");
                        }
                        for (int index = 0; index < orderedMatches.Count; index++)
                        {
                            TemplateMatchResult match = orderedMatches[index];
                            BoardInspectionItem item = new BoardInspectionItem
                            {
                                BoardIndex = index + 1,
                                Valid = mapTargets,
                                ImageRow = match.Row,
                                ImageColumn = match.Column,
                                ImageAngleDegrees = match.AngleDegrees,
                                MatchScore = match.Score
                            };
                            CopyContourSegments(match, item);
                            BlobAnalysisResult analysis = analyses[index];
                            item.DetectionRegion = analysis.Roi;
                            item.Blobs.AddRange(analysis.Findings);
                            result.BlobElapsedMilliseconds += analysis.ElapsedMilliseconds;
                            AccumulateAnalysisTiming(result, analysis);
                            item.HasManualMark = item.Blobs.Exists(blob => !blob.RejectedAsRectangle);
                            LogBoardBlobDetail(item.BoardIndex, analysis, item.HasManualMark);
                            if (mapTargets)
                            {
                                try
                                {
                                    List<MappedTargetPose> mapped = _poseMapper.Map(
                                        leftCalibration,
                                        rightCalibration,
                                        teaching,
                                        match,
                                        currentRLeft,
                                        currentRRight);
                                    bool hasLeft = mapped.Exists(t => t != null && t.Side == TeachingSide.Left);
                                    bool hasRight = mapped.Exists(t => t != null && t.Side == TeachingSide.Right);
                                    if (hasLeft && hasRight)
                                    {
                                        item.Targets.AddRange(mapped);
                                    }
                                    else
                                    {
                                        item.Valid = false;
                                        item.Targets.Clear();
                                        LocalDebugLog.Write(
                                            "WARN",
                                            "inspect.map",
                                            "Board #" + item.BoardIndex + " failed to produce complete Left/Right targets; marked valid=false.");
                                    }
                                }
                                catch (Exception mapException)
                                {
                                    item.Valid = false;
                                    item.Targets.Clear();
                                    LocalDebugLog.WriteException(
                                        "WARN",
                                        "inspect.map",
                                        "Board #" + item.BoardIndex + " pose mapping threw an exception; marked valid=false.",
                                        mapException);
                                }
                            }
                            result.Items.Add(item);
                        }
                    }
                    finally
                    {
                        if (analyses != null)
                        {
                            foreach (BlobAnalysisResult analysis in analyses)
                            {
                                if (analysis != null) analysis.Dispose();
                            }
                        }
                    }
                    result.CoordinateMappingApplied = mapTargets;
                }

                // 直接整图检测模式不校验目标板数；模板匹配模式按 5 级优先级判定错误码与放行状态：
                // 1. BOARD_COUNT_MISMATCH > 2. COORDINATE_NOT_MAPPED > 3. COORDINATE_MAPPING_FAILED > 4. MANUAL_MARK_FOUND > 5. NONE
                result.CountMatchesExpected = !useTemplateMatching || result.ActualCount == result.ExpectedCount;
                result.HasManualMarks = result.Items.Exists(item => item.HasManualMark);
                bool hasInvalidBoard = result.Items.Exists(item => !item.Valid);
                int ngCount = result.Items.Count(item => item.HasManualMark);
                int okCount = result.Items.Count - ngCount;
                int invalidCount = result.Items.Count(item => !item.Valid);

                if (!result.CountMatchesExpected)
                {
                    result.Success = false;
                    result.Pass = false;
                    result.ErrorCode = ProtocolErrorCodes.BoardCountMismatch;
                    result.Message = string.Format(
                        "板数不符（目标 {0} / 实际 {1}，OK {2} / NG {3}）",
                        result.ExpectedCount, result.ActualCount, okCount, ngCount);
                }
                else if (!result.CoordinateMappingApplied)
                {
                    result.Success = false;
                    result.Pass = false;
                    result.ErrorCode = ProtocolErrorCodes.CoordinateNotMapped;
                    result.Message = string.Format(
                        "检测已完成，但标定未启用或未就绪，未生成取料坐标（OK {0} / NG {1}）",
                        okCount, ngCount);
                }
                else if (hasInvalidBoard)
                {
                    result.Success = false;
                    result.Pass = false;
                    result.ErrorCode = ProtocolErrorCodes.CoordinateMappingFailed;
                    result.Message = string.Format(
                        "存在 {0} 块板坐标映射异常，未生成完整取料坐标（OK {1} / NG {2}）",
                        invalidCount, okCount, ngCount);
                }
                else if (result.HasManualMarks)
                {
                    result.Success = true;
                    result.Pass = false;
                    result.ErrorCode = ProtocolErrorCodes.ManualMarkFound;
                    result.Message = string.Format("检出不良标记（OK {0} / NG {1}）", okCount, ngCount);
                }
                else
                {
                    result.Success = true;
                    result.Pass = true;
                    result.ErrorCode = ProtocolErrorCodes.None;
                    result.Message = string.Format("检测通过（OK {0} / NG 0）", okCount);
                }
                return result;
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("ERROR", "inspect.core", "Unhandled exception in InspectCore", exception);
                return Failure(result, "INSPECTION_EXCEPTION", exception.Message);
            }
            finally
            {
                if (gray != null) gray.Dispose();
                watch.Stop();
                result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
                LocalDebugLog.Write(
                    result.Success && result.Pass ? "INFO" : "WARN",
                    "inspect.done",
                    StationLogFormatter.FormatInspectionResult(result));
            }
        }

        private static InspectionResult Failure(InspectionResult result, string code, string message)
        {
            result.Success = false;
            result.Pass = false;
            result.ErrorCode = code;
            result.Message = message;
            LocalDebugLog.Write("WARN", "inspect.fail", "code=" + code + ", message='" + message + "'");
            return result;
        }

        private static void LogBoardBlobDetail(int boardIndex, BlobAnalysisResult analysis, bool hasManualMark)
        {
            if (analysis == null) return;
            DetectionRoi roi = analysis.Roi;
            string roiText = roi == null
                ? "none"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "rc=({0:0.##},{1:0.##}),phi={2:0.##}deg,hw={3:0.##},hh={4:0.##}",
                    roi.ReferenceCenterRow,
                    roi.ReferenceCenterColumn,
                    roi.PhiDegrees,
                    roi.HalfWidth,
                    roi.HalfHeight);
            string findingsText = analysis.Findings == null || analysis.Findings.Count == 0
                ? string.Empty
                : string.Join(";", analysis.Findings.Where(f => f != null).Select(f => string.Format(
                    CultureInfo.InvariantCulture,
                    "(rc=({0:0.#},{1:0.#}),area={2:0.#},rect={3:0.###},filtered={4})",
                    f.Row,
                    f.Column,
                    f.Area,
                    f.Rectangularity,
                    f.RejectedAsRectangle)));
            LocalDebugLog.Write(
                "DEBUG",
                "inspect.blob",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "B{0}: roi=[{1}], effThresh={2}, activePx={3}, mark={4}, ct={5:0.0}ms, findings=[{6}]",
                    boardIndex,
                    roiText,
                    analysis.EffectiveThreshold,
                    analysis.ActivePixelCount,
                    hasManualMark,
                    analysis.ElapsedMilliseconds,
                    findingsText));
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

        private static bool IsCalibrationValid(CalibrationResult calibration)
        {
            return calibration != null && calibration.TranslationSuccess &&
                calibration.Rotation != null && calibration.Rotation.Success &&
                calibration.Rotation.IsAvailable &&
                Math.Abs(calibration.Rotation.DirectionMultiplier) >= 0.5;
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

        /// <summary>按行高容差聚类后从上到下、从左到右稳定排序多板匹配结果，保证板号与基准一致。</summary>
        internal static List<TemplateMatchResult> OrderMatches(
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
