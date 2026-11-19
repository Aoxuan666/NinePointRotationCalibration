using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Communication;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>将工作站配置、协议报文、标定、示教与检测结果格式化为可读的单行诊断日志文本。</summary>
    internal static class StationLogFormatter
    {
        public static string FormatOptions(StationOptions options)
        {
            if (options == null) return "<null options>";
            int featureCount = options.CameraFeatures == null ? 0 : options.CameraFeatures.Count;
            return string.Format(
                CultureInfo.InvariantCulture,
                "ver={0}, camEn={1}, calEn={2}, liveAcq={3}, archive={4}, tcp={5}:{6} (maxBytes={7}, motionTimeout={8}ms), provider={9}, sn='{10}', exp={11:0.##}us, gain={12:0.###}, gamma={13:0.###}, trig={14}, camTimeout={15}ms, previewMs={16}, features={17}, step=({18:0.###},{19:0.###},{20:0.###}deg), expectedCount={21}, configDir='{22}', calJob='{23}', posJob='{24}', teach='{25}', profile='{26}', archiveDir='{27}'",
                options.ConfigurationVersion,
                options.CameraEnabled,
                options.CalibrationEnabled,
                options.LiveAcquisitionEnabled,
                options.ArchiveImages,
                options.TcpBindAddress,
                options.TcpPort,
                options.TcpMaxMessageBytes,
                options.MotionTimeoutMilliseconds,
                options.CameraProvider,
                options.CameraSerialNumber ?? string.Empty,
                options.CameraExposureMicroseconds,
                options.CameraGain,
                options.CameraGamma,
                options.CameraTriggerMode,
                options.CameraTimeoutMilliseconds,
                options.PreviewIntervalMilliseconds,
                featureCount,
                options.TranslationStepX,
                options.TranslationStepY,
                options.RotationStepDegrees,
                options.ExpectedBoardCount,
                options.GetUnifiedConfigDirectory(),
                options.CalibrationJobPath,
                options.PositioningJobPath,
                options.TeachingPath,
                options.DetectionProfilePath,
                options.ImageArchiveDirectory);
        }

        public static string FormatJob(string role, CalibrationJob job)
        {
            if (job == null) return role + "=<null>";
            TemplateDefinition tpl = job.Template;
            int sampleCount = job.Samples == null ? 0 : job.Samples.Count;
            if (tpl == null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}(name='{1}', samples={2}, template=<null>)",
                    role,
                    job.Name,
                    sampleCount);
            }

            MatchParameters match = tpl.MatchParameters;
            string matchText = match == null
                ? "match=<null>"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "minScore={0:0.###}, num={1}, angle=[{2:0.#},{3:0.#}]deg, overlap={4:0.###}, greed={5:0.###}, subPixel={6}",
                    match.MinScore,
                    match.NumMatches,
                    match.AngleStartRad * 180.0 / Math.PI,
                    (match.AngleStartRad + match.AngleExtentRad) * 180.0 / Math.PI,
                    match.MaxOverlap,
                    match.Greediness,
                    match.SubPixel);
            ShapeModelParameters shape = tpl.ShapeParameters;
            ShapeFeatureExtractionSettings extraction = shape == null ? null : shape.FeatureExtraction;
            string threshText = shape == null
                ? "shape=<null>"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "autoThr={0}/{1}, contrast={2}/{3}, mode={4}, canny={5:0.#}/{6:0.#}",
                    shape.AutoThreshold,
                    extraction != null && extraction.AutoThreshold,
                    shape.Contrast,
                    shape.MinContrast,
                    extraction == null ? "Legacy" : extraction.Mode.ToString(),
                    extraction == null ? 0.0 : extraction.EdgeLowThreshold,
                    extraction == null ? 0.0 : extraction.EdgeHighThreshold);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}(name='{1}', samples={2}, type={3}, usable={4}, dirty={5}, rev={6}, anchor=({7:0.##},{8:0.##}), tplRoi={9}, searchRoi={10}, detRoi={11}, {12}, {13})",
                role,
                job.Name,
                sampleCount,
                tpl.ModelType,
                tpl.HasUsableModel,
                tpl.IsModelDirty,
                tpl.Revision,
                tpl.ReferenceAnchor.Row,
                tpl.ReferenceAnchor.Column,
                FormatRegion(tpl.TemplateRoi),
                FormatRegion(tpl.SearchRegion),
                FormatRegion(tpl.DetectionRoi),
                threshText,
                matchText);
        }

        public static string FormatRegion(RegionDefinition region)
        {
            if (region == null) return "none";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}(r={1:0.#},c={2:0.#},phi={3:0.#}deg,l1={4:0.#},l2={5:0.#},rad={6:0.#})",
                region.Kind,
                region.CenterRow,
                region.CenterColumn,
                region.PhiRadians * 180.0 / Math.PI,
                region.Length1,
                region.Length2,
                region.Radius);
        }

        public static string FormatCalibrationResult(CalibrationResult result)
        {
            if (result == null) return "calResult=<null>";
            CalibrationRmsAssessment rms = result.EvaluateRms();
            string rotText = result.Rotation == null
                ? "rot=<null>"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "rot(avail={0},ok={1},dir={2},inliers={3}/{4},center=({5:0.###},{6:0.###}),centerRms={7:0.###}px,angRms={8:0.###}deg)",
                    result.Rotation.IsAvailable,
                    result.Rotation.Success,
                    result.Rotation.Direction,
                    result.Rotation.InlierCount,
                    result.Rotation.CandidateCount,
                    result.Rotation.RotationCenterStage.X,
                    result.Rotation.RotationCenterStage.Y,
                    result.Rotation.CenterRmsResidualPixels,
                    result.Rotation.AngleRmsDegrees);
            string outliers = result.Diagnostics == null
                ? string.Empty
                : string.Join(",", result.Diagnostics
                    .Where(d => d != null && d.IsEligible && !d.IsInlier)
                    .Select(d => (string.IsNullOrWhiteSpace(d.Tag) ? d.SampleId : d.Tag) + "(" + d.ResidualPixels.ToString("0.###", CultureInfo.InvariantCulture) + "px)"));
            return string.Format(
                CultureInfo.InvariantCulture,
                "success={0}, transOk={1}, code={2}, inliers={3}/{4}, resX={5:0.######}, resY={6:0.######}, axisAngle={7:0.###}deg, cond={8:0.##}, xyRms={9:0.###}px({10:0.####}mm), maxRes={11:0.###}px, grade={12}({13}), {14}, outliers=[{15}], msg='{16}'",
                result.Success,
                result.TranslationSuccess,
                result.ErrorCode,
                result.InlierCount,
                result.CandidateCount,
                result.ResolutionX,
                result.ResolutionY,
                result.AxisAngleDegrees,
                result.ConditionNumber,
                result.RmsResidualPixels,
                result.RmsResidualStage,
                result.MaxResidualPixels,
                rms.GradeLabel,
                rms.Score,
                rotText,
                outliers,
                result.Message);
        }

        public static string FormatDetectionProfile(DetectionProfile profile)
        {
            if (profile == null) return "profile=<null>";
            return string.Format(
                CultureInfo.InvariantCulture,
                "tplMatch={0}, followTpl={1}, autoThresh={2}, darkFg={3}, thresh={4}, area=[{5:0.#},{6:0.#}], rectFilter={7}, rectThresh={8:0.###}, fillHoles={9}, morphR={10}",
                profile.TemplateMatchingEnabled,
                profile.FollowTemplateTransform,
                profile.AutoThreshold,
                profile.DetectDarkForeground,
                profile.BrightThreshold,
                profile.MinimumBlobArea,
                profile.MaximumBlobArea,
                profile.RectangleFilterEnabled,
                profile.RectangleFillThreshold,
                profile.FillHoles,
                profile.MorphologyRadius);
        }

        public static string FormatTeachingData(TeachingData data)
        {
            if (data == null) return "teaching=<null>";
            return string.Format(
                CultureInfo.InvariantCulture,
                "Left=[{0}], Right=[{1}]",
                FormatTeachingPose(data.Left),
                FormatTeachingPose(data.Right));
        }

        public static string FormatTeachingPose(TeachingPose pose)
        {
            if (pose == null) return "<null>";
            return string.Format(
                CultureInfo.InvariantCulture,
                "side={0}, valid={1}, img=({2:0.###},{3:0.###},{4:0.###}deg), mach=({5:0.###},{6:0.###},{7:0.###}deg)",
                pose.Side,
                pose.IsValid,
                pose.ImageRow,
                pose.ImageColumn,
                pose.ImageAngleDegrees,
                pose.MachineX,
                pose.MachineY,
                pose.MachineR);
        }

        public static string FormatCameraParameters(CameraParameterSet parameters)
        {
            if (parameters == null) return "camParams=<null>";
            int count = parameters.Features == null ? 0 : parameters.Features.Count;
            string features = parameters.Features == null
                ? string.Empty
                : string.Join("; ", parameters.Features
                    .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name))
                    .Take(20)
                    .Select(f => f.Name + "=" + (f.Value ?? string.Empty)));
            return string.Format(
                CultureInfo.InvariantCulture,
                "exp={0:0.##}us, gain={1:0.###}, gamma={2:0.###}, trig={3}, features({4})=[{5}]",
                parameters.ExposureMicroseconds,
                parameters.Gain,
                parameters.Gamma,
                parameters.TriggerMode,
                count,
                features);
        }

        public static string FormatEnvelope(ProtocolEnvelope envelope, ProtocolJsonCodec codec)
        {
            if (envelope == null) return "<null envelope>";
            try
            {
                if (codec != null)
                {
                    return codec.SerializeToString(envelope);
                }
            }
            catch
            {
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "type={0}, msgId={1}, corrId={2}, dataKeys={3}",
                envelope.Type,
                envelope.MessageId,
                envelope.CorrelationId,
                envelope.Data == null ? 0 : envelope.Data.Count);
        }

        public static string FormatInspectionResult(InspectionResult result)
        {
            if (result == null) return "inspection=<null>";
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "exec={0}, ok={1}, pass={2}, err={3}, count={4}/{5}, marks={6}, curR={7:0.###}, ct={8:0.0}ms (match={9:0.0}, blob={10:0.0}, conv={11:0.0}, thr={12:0.0}, morph={13:0.0}, hole={14:0.0}, comp={15:0.0}), msg='{16}'",
                result.Executed,
                result.Success,
                result.Pass,
                result.ErrorCode,
                result.ActualCount,
                result.ExpectedCount,
                result.HasManualMarks,
                result.CurrentR,
                result.ElapsedMilliseconds,
                result.MatchElapsedMilliseconds,
                result.BlobElapsedMilliseconds,
                result.ImageConversionElapsedMilliseconds,
                result.ThresholdElapsedMilliseconds,
                result.MorphologyElapsedMilliseconds,
                result.HoleFillElapsedMilliseconds,
                result.ComponentElapsedMilliseconds,
                result.Message);

            if (result.Items != null && result.Items.Count > 0)
            {
                sb.Append(" | items=[");
                for (int i = 0; i < result.Items.Count; i++)
                {
                    if (i > 0) sb.Append("; ");
                    BoardInspectionItem item = result.Items[i];
                    if (item == null) continue;
                    string targets = item.Targets == null || item.Targets.Count == 0
                        ? string.Empty
                        : string.Join(",", item.Targets.Where(t => t != null).Select(t => string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:({1:0.###},{2:0.###},rOff={3:0.###},tgtR={4:0.###},pickR={5:0.###},placeR={6:0.###})",
                            t.Side,
                            t.X,
                            t.Y,
                            t.ROffset,
                            t.TargetR,
                            t.PickR,
                            t.BlindPlaceR)));
                    int blobCount = item.Blobs == null ? 0 : item.Blobs.Count;
                    int activeBlobs = item.Blobs == null ? 0 : item.Blobs.Count(b => b != null && !b.RejectedAsRectangle);
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "B{0}(rc=({1:0.##},{2:0.##}),ang={3:0.##},score={4:0.###},mark={5},blobs={6}/{7},targets=[{8}])",
                        item.BoardIndex,
                        item.ImageRow,
                        item.ImageColumn,
                        item.ImageAngleDegrees,
                        item.MatchScore,
                        item.HasManualMark,
                        activeBlobs,
                        blobCount,
                        targets);
                }
                sb.Append("]");
            }
            return sb.ToString();
        }
    }
}
