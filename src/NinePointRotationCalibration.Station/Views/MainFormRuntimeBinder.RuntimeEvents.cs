using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Views
{
    /// <summary>运行时事件响应：日志转发、状态栏同步、帧分发、标定/示教更新与检测结果展示。</summary>
    public sealed partial class MainFormRuntimeBinder
    {
        private void Runtime_Log(object sender, LogEventArgs e)
        {
            if (e == null) return;
            bool error = string.Equals(e.Level, "ERROR", StringComparison.OrdinalIgnoreCase);
            string level = (e.Level ?? "INFO").Trim().ToUpperInvariant();
            string prefix = (level == "WARN" || level == "WARNING") ? "[WARN] " : string.Empty;
            _view.AppendLog(prefix + (e.Message ?? string.Empty), error);
        }

        private void Runtime_StateChanged(object sender, RuntimeStateEventArgs e)
        {
            if (e == null) return;
            if (string.Equals(e.Component, "camera", StringComparison.OrdinalIgnoreCase))
            {
                ApplyCameraState(e.Available, e.Detail);
            }
            else if (string.Equals(e.Component, "host", StringComparison.OrdinalIgnoreCase))
            {
                _view.SetTcpStatus(e.Available || _runtime.IsStarted, e.Available ? "上位机 " + e.Detail : "等待上位机连接");
            }
            else if (string.Equals(e.Component, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Available && _runtime.IsHostConnected) return;
                _view.SetTcpStatus(e.Available, e.Detail);
            }
        }

        private void ApplyCameraState(bool connected, string detail)
        {
            string normalized = detail ?? string.Empty;
            if (!_runtime.IsCameraEnabled ||
                string.Equals(normalized, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                _view.SetCameraStatus(CameraUiState.Disabled, string.Empty);
                return;
            }

            if (connected)
            {
                _view.SetCameraStatus(CameraUiState.Connected, normalized);
                return;
            }

            bool ordinaryDisconnect = string.IsNullOrWhiteSpace(normalized) ||
                                      string.Equals(normalized, "Disconnected", StringComparison.OrdinalIgnoreCase);
            _view.SetCameraStatus(
                ordinaryDisconnect ? CameraUiState.Disconnected : CameraUiState.Error,
                ordinaryDisconnect ? string.Empty : normalized);
        }

        private void Runtime_FrameReady(object sender, FrameEventArgs e)
        {
            if (e == null || e.Image == null) return;
            string source = e.Source ?? "frame";
            if (string.Equals(source, "preview", StringComparison.OrdinalIgnoreCase))
            {
                lock (_displaySync)
                {
                    if (_displayMode != DisplayMode.LivePreview || _disposed) return;
                    // 视口已有待绘制帧时在复制前直接丢弃新预览帧，防止相机高帧率在 UI 线程堆积内存
                    if (_view.Viewport.TrySetPreviewImage(e.Image))
                    {
                        if (_view.IsDetectionDualViewActive)
                        {
                            _view.EffectViewport.TrySetPreviewImage(e.Image);
                            _view.SetEffectViewportInformation(string.Format(
                                CultureInfo.InvariantCulture,
                                "{0} x {1}   实时预览同步原图   {2:HH:mm:ss.fff}",
                                e.Image.Width,
                                e.Image.Height,
                                e.CapturedAtUtc.ToLocalTime()));
                        }
                        _view.SetImageInformation(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} x {1}   {2}   {3:HH:mm:ss.fff}",
                            e.Image.Width,
                            e.Image.Height,
                            source,
                            e.CapturedAtUtc.ToLocalTime()));
                    }
                }
                return;
            }

            // 非实时预览帧（单次拍照、标定、示教及在线/离线检测）会锁定主画面，防止被后续预览流覆盖
            Bitmap copy = (Bitmap)e.Image.Clone();
            PinOwnedDisplayImage(
                copy,
                source,
                string.Equals(source, "inspection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "offline-inspection", StringComparison.OrdinalIgnoreCase),
                e.CapturedAtUtc);
        }

        private void Runtime_CalibrationPointChanged(object sender, CalibrationPointEventArgs e)
        {
            if (e == null || e.Record == null) return;
            ApplyCalibrationRecord(e.Record);
            _view.SelectCalibrationPoint(e.Record.PointNumber);
        }

        private void ApplyCalibrationRecord(CalibrationPointRecord record)
        {
            if (record == null) return;
            bool hasSample = record.CapturedAtUtc.HasValue || record.Status == CalibrationPointStatus.Completed;
            _view.UpdateCalibrationSample(
                record.PointNumber,
                hasSample ? (double?)record.MachineX : null,
                hasSample ? (double?)record.MachineY : null,
                hasSample ? (double?)record.MachineR : null,
                hasSample ? (double?)record.ImageRow : null,
                hasSample ? (double?)record.ImageColumn : null,
                hasSample ? (double?)record.ImageAngle : null,
                FormatCalibrationStatus(record.Status));
        }

        private void Runtime_CalibrationCompleted(object sender, CalibrationResultEventArgs e)
        {
            CalibrationResult result = e == null ? null : e.Result;
            if (result == null)
            {
                _view.AppendLog("标定计算未返回结果。", true);
                return;
            }

            CalibrationRmsAssessment rms = result.EvaluateRms();
            _view.SetCalibrationRmsSummary(result);
            _view.ApplyCalibrationDiagnostics(result);
            _view.AppendLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "标定{0}：{1}｜{2}。",
                    result.Success ? "成功" : "失败",
                    result.Message,
                    rms.Summary),
                !result.Success);
            _view.SetOperationStatus(
                result.Success ? "标定完成 · " + rms.GradeLabel + "(" + rms.Score + "分)" : "标定失败",
                false,
                !result.Success);
        }

        private void Runtime_TeachingChanged(object sender, TeachingEventArgs e)
        {
            ApplyTeachingToView(e == null ? null : e.Pose);
        }

        private void ApplyTeachingToView(TeachingPose pose)
        {
            if (pose == null) return;
            _view.UpdateTeaching(
                pose.Side == TeachingSide.Left ? "left" : "right",
                pose.ImageRow,
                pose.ImageColumn,
                pose.ImageAngleDegrees,
                pose.MachineX,
                pose.MachineY,
                pose.MachineR,
                pose.IsValid);
        }

        private void Runtime_InspectionCompleted(object sender, InspectionEventArgs e)
        {
            InspectionResult result = e == null ? null : e.Result;
            if (result == null) return;

            List<object[]> rows = new List<object[]>();
            List<ViewportPointOverlay> points = new List<ViewportPointOverlay>();
            List<ViewportRoiOverlay> regions = new List<ViewportRoiOverlay>();
            List<ViewportContourOverlay> contours = new List<ViewportContourOverlay>();
            foreach (BoardInspectionItem item in result.Items ?? new List<BoardInspectionItem>())
            {
                MappedTargetPose leftTarget = item.Targets == null
                    ? null
                    : item.Targets.FirstOrDefault(target => target != null && target.Side == TeachingSide.Left);
                MappedTargetPose rightTarget = item.Targets == null
                    ? null
                    : item.Targets.FirstOrDefault(target => target != null && target.Side == TeachingSide.Right);
                string leftText = leftTarget == null
                    ? "--"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.##}, {1:0.##}, {2:0.##}°",
                        leftTarget.X,
                        leftTarget.Y,
                        leftTarget.TargetR);
                string rightText = rightTarget == null
                    ? "--"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.##}, {1:0.##}, {2:0.##}°",
                        rightTarget.X,
                        rightTarget.Y,
                        rightTarget.TargetR);
                rows.Add(new object[]
                {
                    item.BoardIndex,
                    item.MatchScore.ToString("0.000", CultureInfo.InvariantCulture),
                    item.ImageColumn.ToString("0.###", CultureInfo.InvariantCulture),
                    item.ImageRow.ToString("0.###", CultureInfo.InvariantCulture),
                    item.ImageAngleDegrees.ToString("0.###", CultureInfo.InvariantCulture),
                    leftText,
                    rightText,
                    item.HasManualMark ? "有" : "无",
                    item.HasManualMark ? "NG" : "OK"
                });
                foreach (BlobFinding blob in item.Blobs ?? new List<BlobFinding>())
                {
                    if (blob == null || blob.RejectedAsRectangle) continue;
                    if (blob.Contour == null || blob.Contour.Count < 2) continue;
                    contours.Add(new ViewportContourOverlay
                    {
                        Points = blob.Contour.ToList(),
                        Color = Color.LimeGreen,
                        LineWidth = 2.0F
                    });
                }
                if (item.DetectionRegion != null)
                {
                    DetectionRoi region = item.DetectionRegion;
                    regions.Add(new ViewportRoiOverlay
                    {
                        ImageBounds = new RectangleF(
                            (float)(region.ReferenceCenterColumn - region.HalfWidth),
                            (float)(region.ReferenceCenterRow - region.HalfHeight),
                            (float)(region.HalfWidth * 2.0),
                            (float)(region.HalfHeight * 2.0)),
                        AngleDegrees = (float)region.PhiDegrees,
                        Label = "板 " + item.BoardIndex + " 检测区",
                        Color = item.HasManualMark ? Color.OrangeRed : Color.DeepSkyBlue
                    });
                }
            }

            _view.ReplaceDetectionResults(rows);
            lock (_displaySync)
            {
                // 仅当锁定帧仍对应本次检测的代次时才缓存并绘制叠加层，避免异步乱序刷新
                if (_displayMode == DisplayMode.LockedFrame &&
                    _inspectionFrameGeneration >= 0 &&
                    _inspectionFrameGeneration == _displayGeneration)
                {
                    _lastInspectionPoints = points;
                    _lastInspectionRois = regions;
                    _lastInspectionContours = contours;
                    _lastDetectionRegions = result.Items
                        .Where(item => item != null && item.DetectionRegion != null)
                        .Select(item => CloneDetectionRoi(item.DetectionRegion))
                        .ToList();
                    _lastInspectionBoardIndices = result.Items
                        .Where(item => item != null && item.DetectionRegion != null)
                        .Select(item => item.BoardIndex)
                        .ToList();
                    _lastMatchedBoardContours = new List<ViewportContourOverlay>();
                    _lastInspectionOverlayGeneration = _displayGeneration;
                    if (!_view.IsDetectionDualViewActive)
                    {
                        _view.Viewport.SetPointOverlays(null);
                        _view.Viewport.SetRoiOverlays(null);
                        _view.Viewport.SetContourOverlays(contours);
                    }
                    else
                    {
                        _view.Viewport.ClearOverlays();
                        _view.EffectViewport.SetPointOverlays(null);
                        _view.EffectViewport.SetContourOverlays(contours);
                        _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(
                            regions,
                            _lastInspectionBoardIndices,
                            _selectedInspectionBoardIndex));
                        SyncEffectViewportViewDeferred();
                    }
                }
            }
            _view.SetDetectionSummary(result.ExpectedCount, result.ActualCount, result.Success && result.Pass, result.Message);
            _view.SetInspectionHud(
                result.ElapsedMilliseconds,
                result.MatchElapsedMilliseconds,
                result.BlobElapsedMilliseconds,
                result.ImageConversionElapsedMilliseconds,
                result.ThresholdElapsedMilliseconds,
                result.MorphologyElapsedMilliseconds,
                result.HoleFillElapsedMilliseconds,
                result.ComponentElapsedMilliseconds,
                result.ExpectedCount,
                result.ActualCount,
                result.Success && result.Pass,
                result.HasManualMarks);
            // 若当前处于检测双视图，检测结果同步后立即刷新检测效果图窗口（含局部二值/原图叠加）
            if (_view.IsDetectionDualViewActive) ScheduleBinaryPreview(false);
        }
    }
}
