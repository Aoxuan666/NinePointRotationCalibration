using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Devices;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Services.Calibration;
using NinePointRotationCalibration.Station.Services.Camera;
using NinePointRotationCalibration.Station.Services.Communication;
using NinePointRotationCalibration.Station.Services.Inspection;
using NinePointRotationCalibration.Station.Services.Persistence;
using NinePointRotationCalibration.Station.Services.Teaching;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.Station.Views;
using UiCalibrationCanvas = NinePointRotationCalibration.WinForms.Controls.CalibrationCanvas;
using UiCoreUiMapper = NinePointRotationCalibration.WinForms.Adapters.CoreUiMapper;
using UiImageCoordinate = NinePointRotationCalibration.WinForms.Geometry.ImageCoordinate;
using UiRotatedRectangle = NinePointRotationCalibration.WinForms.Geometry.RotatedRectangle;
using UiTemplateEditorForm = NinePointRotationCalibration.WinForms.Forms.TemplateEditorForm;
using UiTemplateEditorState = NinePointRotationCalibration.WinForms.Models.TemplateEditorState;
using UiCanvasRoiTarget = NinePointRotationCalibration.WinForms.Models.CanvasRoiTarget;

namespace NinePointRotationCalibration.Station.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        private static int Main()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("NPRCAL_BLOB_DIAG"), "1", StringComparison.Ordinal))
            {
                Run("Blob ROI keeps only the largest white region", TestBlobAnalysis);
                Run("Blob preprocessing fills holes and removes noise", TestBlobDetailedAnalysis);
                Run("Binary threshold supports both foreground polarities", TestBinaryThresholdPolarities);
                Run("Legacy detection profiles receive preprocessing defaults", TestLegacyDetectionProfileDefaults);
                Run("HALCON angle maps detection ROI to screen geometry", TestHalconDetectionRoiTransform);
                Run("Blob debug splitter is valid at construction", TestBlobDebugFormLayout);
                Run("Blob debug mouse wheel is clamped", TestBlobDebugMouseWheel);
                Run("Overlapping ROI clicks cycle all targets", TestOverlappingRoiSelection);
                Run("Main form exposes binary preview preprocessing controls", TestOfflineModeControls);
                Run("Locked inspection frame keeps only blob contours", TestLockedFrameDisplay);
                return _failed == 0 ? 0 : 1;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("NPRCAL_TCP_DIAG"), "1", StringComparison.Ordinal))
            {
                Console.WriteLine("[TCP-DIAG] isolated test start");
                RunAsync("Mock camera lifecycle returns owned frames", TestMockCameraAsync);
                RunAsync("Station startup cancellation reaches camera enumeration", TestStartupCancellationAsync);
                RunAsync("Preview health is debounced and reports recovery", TestPreviewHealthAsync);
                RunAsync("Station startup and TCP enforce camera/calibration enablement", TestEnablementAndProtocolAsync);
                Console.WriteLine("[TCP-DIAG] isolated test end");
                return _failed == 0 ? 0 : 1;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("NPRCAL_PREFIX"), "1", StringComparison.Ordinal))
            {
                Run("Calibration plan has 9 + 5 points and one shared origin", TestCalibrationPlan);
                Run("Protocol JSON is clean camelCase and round-trips", TestProtocolJson);
                Run("TCP framing and connection-scoped replies", CommunicationSelfTest.Run);
                RunAsync("Calibration motion replies require exact correlation", TestCalibrationCorrelationAsync);
                RunAsync("Calibration motion timeout reports a correlated failure", TestCalibrationTimeoutAsync);
                Run("Replacing a calibration job clears stale point state", TestCalibrationJobReset);
                RunAsync("Teaching binds positioning-template center to machine pose", TestTeachingCaptureAsync);
                Run("Target XY and R mapping follows calibrated direction", TestTargetPoseMapping);
                Run("Blob ROI transform and rectangle filtering", TestBlobAnalysis);
                Run("Detailed blob analysis exposes binary preview and direct mode", TestBlobDetailedAnalysis);
                Run("HALCON angle maps detection ROI to screen geometry", TestHalconDetectionRoiTransform);
                Run("Core UI mapper round-trips non-square Rectangle2 geometry", TestCoreUiMapperRectangle2RoundTrip);
                Run("Template matching defaults mirror the reference project", TestReferenceTemplateDefaults);
                Run("XLD contour sampling matches the reference project", TestXldContourSampling);
                Run("XLD extraction retains concave board contours", TestXldConcaveBoardContourRetention);
                Run("XLD shape model uses ordered filtered contours", TestXldShapeModel);
                Run("XLD engine preserves legacy Shape and NCC delegates", TestXldLegacyAndNccDelegation);
                Run("Template editor renders every extracted feature point", TestTemplateEditorContourOverlay);
                Run("Template editor preserves Shape and NCC metric semantics", TestTemplateEditorMetricModes);
                Run("Board inspection enforces count, rotation, and both teaching poses", TestBoardInspection);
                RunAsync("Mock camera lifecycle returns owned frames", TestMockCameraAsync);
                RunAsync("Station startup cancellation reaches camera enumeration", TestStartupCancellationAsync);
                RunAsync("Preview health is debounced and reports recovery", TestPreviewHealthAsync);
                RunAsync("Station startup and TCP enforce camera/calibration enablement", TestEnablementAndProtocolAsync);
                return _failed == 0 ? 0 : 1;
            }

            Run("Calibration plan has 9 + 5 points and one shared origin", TestCalibrationPlan);
            Run("Protocol JSON is clean camelCase and round-trips", TestProtocolJson);
            Run("TCP framing and connection-scoped replies", CommunicationSelfTest.Run);
            RunAsync("Calibration motion replies require exact correlation", TestCalibrationCorrelationAsync);
            RunAsync("Calibration motion timeout reports a correlated failure", TestCalibrationTimeoutAsync);
            Run("Replacing a calibration job clears stale point state", TestCalibrationJobReset);
            RunAsync("Teaching binds positioning-template center to machine pose", TestTeachingCaptureAsync);
            Run("Target XY and R mapping follows calibrated direction", TestTargetPoseMapping);
            Run("Blob ROI transform and rectangle filtering", TestBlobAnalysis);
            Run("Detailed blob analysis exposes binary preview and direct mode", TestBlobDetailedAnalysis);
            Run("Binary threshold supports both foreground polarities", TestBinaryThresholdPolarities);
            Run("Legacy detection profiles keep template matching enabled", TestLegacyDetectionProfileDefaults);
            Run("HALCON angle maps detection ROI to screen geometry", TestHalconDetectionRoiTransform);
            Run("Core UI mapper round-trips non-square Rectangle2 geometry", TestCoreUiMapperRectangle2RoundTrip);
            Run("Template matching defaults mirror the reference project", TestReferenceTemplateDefaults);
            Run("XLD contour sampling matches the reference project", TestXldContourSampling);
            Run("XLD extraction retains concave board contours", TestXldConcaveBoardContourRetention);
            Run("XLD shape model uses ordered filtered contours", TestXldShapeModel);
            Run("XLD engine preserves legacy Shape and NCC delegates", TestXldLegacyAndNccDelegation);
            Run("Template editor renders every extracted feature point", TestTemplateEditorContourOverlay);
            Run("Template editor preserves Shape and NCC metric semantics", TestTemplateEditorMetricModes);
            Run("Board inspection enforces count, rotation, and both teaching poses", TestBoardInspection);
            RunAsync("Mock camera lifecycle returns owned frames", TestMockCameraAsync);
            RunAsync("Station startup cancellation reaches camera enumeration", TestStartupCancellationAsync);
            RunAsync("Preview health is debounced and reports recovery", TestPreviewHealthAsync);
            RunAsync("Station startup and TCP enforce camera/calibration enablement", TestEnablementAndProtocolAsync);
            Run("Legacy station settings keep camera and calibration enabled", TestLegacyOptionsMigration);
            Run("Calibration-disabled inspection keeps vision results without mapped targets", TestInspectionWithoutMapping);
            Run("Main form is safe to construct without runtime", TestDesignerSafeMainForm);
            Run("Main form exposes offline controls and camera status indicator", TestOfflineModeControls);
            Run("Locked inspection frames reject preview and stale overlays", TestLockedFrameDisplay);
            Run("Empty image viewport is a plain black surface", TestEmptyViewportIsBlack);
            Run("Positioning template owns and migrates detection ROI", TestPositioningJobLoadRoi);

            Console.WriteLine();
            Console.WriteLine("Passed: {0}; Failed: {1}", _passed, _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void TestCalibrationPlan()
        {
            IReadOnlyList<CalibrationPlanPoint> plan = CalibrationPlan.Create(10.0, 20.0, 5.0);
            True(plan.Count == 14, "plan must contain fourteen points");
            True(plan.Select(point => point.PointNumber).Distinct().Count() == 14, "point numbers must be unique");
            True(plan.Select(point => point.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 14,
                "point tags must be unique");

            CalibrationPlanPoint translationOrigin = CalibrationPlan.Find(plan, 5);
            CalibrationPlanPoint rotationOrigin = CalibrationPlan.Find(plan, 12);
            True(translationOrigin.IsSharedOrigin && rotationOrigin.IsSharedOrigin,
                "points 5 and 12 must identify the shared origin");
            Near(0.0, translationOrigin.OffsetX, 0.0, "point 5 X");
            Near(0.0, translationOrigin.OffsetY, 0.0, "point 5 Y");
            Near(0.0, rotationOrigin.OffsetX, 0.0, "point 12 X");
            Near(0.0, rotationOrigin.OffsetY, 0.0, "point 12 Y");
            Near(0.0, rotationOrigin.OffsetR, 0.0, "point 12 R");
            True(plan.Take(9).All(point => point.Kind == CalibrationSampleKind.Translation),
                "points 1..9 must be translation samples");
            True(plan.Skip(9).All(point => point.Kind == CalibrationSampleKind.Rotation),
                "points 10..14 must be rotation samples");
            Near(-10.0, CalibrationPlan.Find(plan, 1).OffsetX, 0.0, "point 1 X");
            Near(-20.0, CalibrationPlan.Find(plan, 1).OffsetY, 0.0, "point 1 Y");
            Near(10.0, CalibrationPlan.Find(plan, 4).OffsetX, 0.0, "snake order point 4 X");
            Near(-10.0, CalibrationPlan.Find(plan, 6).OffsetX, 0.0, "snake order point 6 X");
        }

        private static void TestProtocolJson()
        {
            ProtocolEnvelope envelope = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionResult);
            envelope.CorrelationId = "request-1";
            envelope.Data["success"] = true;
            envelope.Data["items"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "boardIndex", 1 },
                    { "targets", new List<object>
                        {
                            new Dictionary<string, object> { { "side", "Left" }, { "x", 12.5 } }
                        }
                    }
                }
            };

            ProtocolJsonCodec codec = new ProtocolJsonCodec();
            string json = codec.SerializeToString(envelope);
            True(json.Contains("\"version\":\"1.0\""), "wire JSON must use lower camelCase names");
            True(json.Contains("\"messageId\":"), "wire JSON must contain messageId");
            True(json.Contains("\"boardIndex\":1"), "nested dictionaries must remain plain JSON objects");
            True(!json.Contains("__type"), "wire JSON must not contain CLR type metadata");
            True(!json.Contains("\"Version\""), "CLR property names must not leak onto the wire");

            ProtocolEnvelope decoded = codec.Deserialize(json);
            True(decoded.Type == ProtocolMessageTypes.InspectionResult, "type round-trip");
            True(decoded.CorrelationId == "request-1", "correlation round-trip");
            True(decoded.Data.ContainsKey("SUCCESS"), "data keys must be case-insensitive after decoding");

            ProtocolEnvelope hostMessage = codec.Deserialize(
                "{\"version\":\"1.0\",\"messageId\":\"host-1\",\"correlationId\":null," +
                "\"type\":\"system.heartbeat\",\"timestampUtc\":\"2026-10-12T00:00:00Z\",\"data\":{}}");
            True(hostMessage.MessageId == "host-1", "documented host JSON must deserialize");
        }

        private static async Task TestCalibrationCorrelationAsync()
        {
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = SuccessfulMatch(120.0, 240.0, 3.0)
            };
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                object sendSync = new object();
                List<ProtocolEnvelope> sent = new List<ProtocolEnvelope>();
                TaskCompletionSource<ProtocolEnvelope> firstMove = NewCompletion<ProtocolEnvelope>();
                TaskCompletionSource<ProtocolEnvelope> secondMove = NewCompletion<ProtocolEnvelope>();
                int moveCount = 0;

                Func<ProtocolEnvelope, CancellationToken, Task> send = (message, token) =>
                {
                    lock (sendSync) sent.Add(message);
                    if (message.Type == ProtocolMessageTypes.MoveRequest)
                    {
                        if (Interlocked.Increment(ref moveCount) == 1) firstMove.TrySetResult(message);
                        else secondMove.TrySetResult(message);
                    }
                    return Task.CompletedTask;
                };

                Func<CancellationToken, Task<CameraFrame>> capture = token => Task.FromResult(
                    new CameraFrame(new Bitmap(32, 24), 1, DateTime.UtcNow));
                StationOptions options = new StationOptions { MotionTimeoutMilliseconds = 5000 };
                using (CalibrationCoordinator coordinator = new CalibrationCoordinator(
                    sdk,
                    send,
                    capture,
                    job => Task.CompletedTask,
                    options,
                    new CalibrationJob()))
                {
                    using (CancellationTokenSource firstCancellation = new CancellationTokenSource())
                    {
                        Task<CalibrationPointRecord> firstCapture = coordinator.CapturePointAsync(5, firstCancellation.Token);
                        ProtocolEnvelope firstRequest = await WithTimeout(firstMove.Task, 2000).ConfigureAwait(false);

                        ProtocolEnvelope missingCorrelation = Arrival(null, 5, 1.0, 2.0, 3.0);
                        True(coordinator.HandleProtocolMessage(missingCorrelation),
                            "motion.arrived must be consumed even when ignored");
                        await Task.Delay(50).ConfigureAwait(false);
                        True(!firstCapture.IsCompleted, "missing correlationId must not complete a pending move");

                        firstCancellation.Cancel();
                        await ExpectCancelled(firstCapture).ConfigureAwait(false);

                        Task<CalibrationPointRecord> secondCapture = coordinator.CapturePointAsync(5, CancellationToken.None);
                        ProtocolEnvelope secondRequest = await WithTimeout(secondMove.Task, 2000).ConfigureAwait(false);

                        True(coordinator.HandleProtocolMessage(Arrival(firstRequest.MessageId, 5, 9.0, 9.0, 9.0)),
                            "late replies must be consumed");
                        await Task.Delay(50).ConfigureAwait(false);
                        True(!secondCapture.IsCompleted, "a late reply must not complete a new request for the same point");

                        True(coordinator.HandleProtocolMessage(Arrival(secondRequest.MessageId, 6, 4.0, 5.0, 6.0)),
                            "mismatched point reply must be consumed");
                        await Task.Delay(50).ConfigureAwait(false);
                        True(!secondCapture.IsCompleted, "pointNumber must match the correlated request");

                        ProtocolEnvelope missingCoordinate = Arrival(secondRequest.MessageId, 5, 4.0, 5.0, 6.0);
                        missingCoordinate.Data.Remove("x");
                        True(coordinator.HandleProtocolMessage(missingCoordinate),
                            "malformed correlated reply must be consumed");
                        await Task.Delay(50).ConfigureAwait(false);
                        True(!secondCapture.IsCompleted, "all actual X/Y/R coordinates are required");

                        True(coordinator.HandleProtocolMessage(Arrival(secondRequest.MessageId, 5, 4.0, 5.0, 6.0)),
                            "valid reply should complete the request");
                        CalibrationPointRecord record = await WithTimeout(secondCapture, 2000).ConfigureAwait(false);
                        True(record.Status == CalibrationPointStatus.Completed, "capture should complete");
                        Near(4.0, record.MachineX, 0.0, "actual machine X");
                        Near(5.0, record.MachineY, 0.0, "actual machine Y");
                        Near(6.0, record.MachineR, 0.0, "actual machine R");
                        lock (sendSync)
                        {
                            True(sent.Any(message => message.Type == ProtocolMessageTypes.CalibrationPointResult &&
                                message.CorrelationId == secondRequest.MessageId),
                                "point result must correlate to its move request");
                        }
                    }
                }
            }
        }

        private static async Task TestCalibrationTimeoutAsync()
        {
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = SuccessfulMatch(10.0, 20.0, 0.0)
            };
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                object sync = new object();
                List<ProtocolEnvelope> sent = new List<ProtocolEnvelope>();
                Func<ProtocolEnvelope, CancellationToken, Task> send = (message, token) =>
                {
                    lock (sync) sent.Add(message);
                    return Task.CompletedTask;
                };
                Func<CancellationToken, Task<CameraFrame>> capture = token =>
                    Task.FromResult(new CameraFrame(new Bitmap(8, 8), 1, DateTime.UtcNow));
                StationOptions options = new StationOptions { MotionTimeoutMilliseconds = 1000 };
                using (CalibrationCoordinator coordinator = new CalibrationCoordinator(
                    sdk,
                    send,
                    capture,
                    job => Task.CompletedTask,
                    options,
                    new CalibrationJob()))
                {
                    bool timedOut = false;
                    try
                    {
                        await coordinator.CapturePointAsync(1, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        timedOut = true;
                    }
                    True(timedOut, "missing motion.arrived must time out");

                    ProtocolEnvelope request;
                    ProtocolEnvelope failure;
                    lock (sync)
                    {
                        request = sent.Single(message => message.Type == ProtocolMessageTypes.MoveRequest);
                        failure = sent.Single(message => message.Type == ProtocolMessageTypes.CalibrationPointResult);
                    }
                    True(failure.CorrelationId == request.MessageId,
                        "timeout failure must correlate to the move request");
                    True(!(bool)failure.Data["success"], "timeout point result must be unsuccessful");
                    True(Convert.ToString(failure.Data["message"]).StartsWith("MOTION_TIMEOUT:", StringComparison.Ordinal),
                        "timeout result must carry a stable error code");
                }
            }
        }

        private static void TestCalibrationJobReset()
        {
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine();
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                CalibrationJob populated = new CalibrationJob();
                populated.AddSample(new CalibrationSample
                {
                    Tag = "P1",
                    Kind = CalibrationSampleKind.Translation,
                    MachinePose = new MachinePose(11.0, 22.0, 33.0),
                    ImagePose = new ImagePose(44.0, 55.0, 66.0),
                    MatchScore = 0.9,
                    Enabled = true
                });

                using (CalibrationCoordinator coordinator = new CalibrationCoordinator(
                    sdk,
                    (message, token) => Task.CompletedTask,
                    token => Task.FromResult(new CameraFrame(new Bitmap(1, 1), 1, DateTime.UtcNow)),
                    job => Task.CompletedTask,
                    new StationOptions(),
                    populated))
                {
                    CalibrationPointRecord loaded = coordinator.GetRecordsSnapshot().Single(item => item.PointNumber == 1);
                    True(loaded.Status == CalibrationPointStatus.Completed, "saved P1 sample should load as completed");
                    Near(11.0, loaded.MachineX, 0.0, "loaded machine X");

                    coordinator.SetJob(new CalibrationJob());
                    CalibrationPointRecord cleared = coordinator.GetRecordsSnapshot().Single(item => item.PointNumber == 1);
                    True(cleared.Status == CalibrationPointStatus.Pending, "missing P1 sample must reset status");
                    Near(0.0, cleared.MachineX, 0.0, "cleared machine X");
                    Near(0.0, cleared.ImageColumn, 0.0, "cleared image column");
                    True(cleared.CapturedAtUtc == null, "cleared capture time");
                    True(string.IsNullOrEmpty(cleared.Message), "cleared status message");
                }
            }
        }

        private static async Task TestTeachingCaptureAsync()
        {
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = SuccessfulMatch(123.25, 456.75, -7.5)
            };
            CalibrationJob positioningJob = new CalibrationJob { Name = "Positioning teaching test" };
            positioningJob.Template.SetModelData(new byte[] { 1, 2, 3 }, "test");
            positioningJob.Template.ReferenceAnchor = new ImagePoint(12.0, 34.0);

            int positioningJobRequests = 0;
            int captureCount = 0;
            TeachingData saved = null;
            Func<CancellationToken, Task<CameraFrame>> capture = token =>
            {
                captureCount++;
                return Task.FromResult(new CameraFrame(new Bitmap(32, 24), 17, DateTime.UtcNow));
            };

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            using (TeachingService service = new TeachingService(
                sdk,
                capture,
                () =>
                {
                    positioningJobRequests++;
                    return positioningJob.DeepClone();
                },
                data =>
                {
                    saved = data;
                    return Task.CompletedTask;
                },
                new TeachingData()))
            {
                MachinePose machinePose = new MachinePose(101.5, -22.75, 33.25);
                TeachingPose result = await service
                    .CaptureAsync(TeachingSide.Left, machinePose, CancellationToken.None)
                    .ConfigureAwait(false);

                True(positioningJobRequests == 1, "teaching must request the positioning job exactly once");
                True(captureCount == 1, "teaching must capture one current image");
                True(result.IsValid && result.Side == TeachingSide.Left,
                    "a successful positioning match must create valid left teaching data");
                Near(123.25, result.ImageRow, 0.0, "teaching image row comes from template match center");
                Near(456.75, result.ImageColumn, 0.0, "teaching image column comes from template match center");
                Near(-7.5, result.ImageAngleDegrees, 0.0, "teaching image angle comes from template match");
                Near(101.5, result.MachineX, 0.0, "teaching machine X comes from current machine pose");
                Near(-22.75, result.MachineY, 0.0, "teaching machine Y comes from current machine pose");
                Near(33.25, result.MachineR, 0.0, "teaching machine R comes from current machine pose");
                True(saved != null && saved.Left != null && saved.Left.IsValid,
                    "the image/machine correspondence must be persisted");
                Near(result.ImageRow, saved.Left.ImageRow, 0.0, "persisted match row");
                Near(result.MachineX, saved.Left.MachineX, 0.0, "persisted machine X");
                True(saved.Right != null && !saved.Right.IsValid,
                    "capturing left teaching must not fabricate right teaching data");
            }
        }

        private static void TestTargetPoseMapping()
        {
            CalibrationResult calibration = new CalibrationResult
            {
                TranslationSuccess = true,
                PixelToStageMatrix = new AffineMatrix2D(2.0, 0.5, 10.0, -0.25, 3.0, -4.0),
                Rotation = new RotationCalibrationResult
                {
                    IsAvailable = true,
                    Success = true,
                    Direction = RotationDirection.Same
                }
            };
            TeachingData teaching = new TeachingData
            {
                Left = new TeachingPose
                {
                    Side = TeachingSide.Left,
                    IsValid = true,
                    ImageRow = 20.0,
                    ImageColumn = 10.0,
                    ImageAngleDegrees = 10.0,
                    MachineX = 100.0,
                    MachineY = 200.0,
                    MachineR = 30.0
                },
                Right = new TeachingPose { Side = TeachingSide.Right, IsValid = false }
            };
            TemplateMatchResult match = SuccessfulMatch(25.0, 17.0, 25.0);

            TargetPoseMapper mapper = new TargetPoseMapper();
            MappedTargetPose same = mapper.Map(calibration, teaching, match, 40.0).Single();
            Near(116.5, same.X, 1e-10, "mapped X uses the full affine matrix");
            Near(213.25, same.Y, 1e-10, "mapped Y uses the full affine matrix");
            Near(5.0, same.ROffset, 1e-10, "same-direction R offset");
            Near(45.0, same.TargetR, 1e-10, "same-direction target R");

            calibration.Rotation.Direction = RotationDirection.Opposite;
            MappedTargetPose opposite = mapper.Map(calibration, teaching, match, 10.0).Single();
            Near(5.0, opposite.ROffset, 1e-10, "opposite-direction R offset");
            Near(15.0, opposite.TargetR, 1e-10, "opposite-direction target R");

            calibration.Rotation.IsAvailable = false;
            True(mapper.Map(calibration, teaching, match, 10.0).Count == 0,
                "an unavailable rotation calibration must not produce a guessed R target");
        }

        private static void TestBlobAnalysis()
        {
            using (Bitmap image = new Bitmap(160, 120))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Black);
                    graphics.FillRectangle(Brushes.White, 84, 44, 12, 12);
                    using (Pen pen = new Pen(Color.White, 3.0f))
                    {
                        graphics.DrawLine(pen, 103, 45, 103, 57);
                        graphics.DrawLine(pen, 97, 51, 109, 51);
                    }
                }

                TemplateDefinition template = new TemplateDefinition
                {
                    ModelReference = new ImagePose(60.0, 80.0, 0.0),
                    TemplateRoi = RectangleRegion(70.0, 90.0, 30.0, 10.0, 0.0),
                    DetectionRoi = RectangleRegion(50.0, 70.0, 35.0, 20.0, 0.0)
                };
                TemplateMatchResult match = SuccessfulMatch(60.0, 80.0, -90.0);
                match.ModelPose = new ImagePose(60.0, 80.0, -90.0);
                DetectionProfile profile = new DetectionProfile
                {
                    BrightThreshold = 200,
                    MinimumBlobArea = 8.0,
                    MaximumBlobArea = 1000.0,
                    RectangleFilterEnabled = true,
                    RectangleFillThreshold = 0.85,
                    MorphologyRadius = 0
                };

                List<BlobFinding> findings = new BoardBlobAnalyzer().Analyze(image, template, match, profile);
                True(findings.Count == 1, "rotated ROI should retain only the largest synthetic blob");
                True(findings.Count(item => item.RejectedAsRectangle) == 1,
                    "the largest filled rectangle should be filtered");

                BoardBlobAnalyzer analyzer = new BoardBlobAnalyzer();
                DetectionRoi customRegion = analyzer.ResolveDetectionRoi(template, match, profile);
                Near(50.0, customRegion.ReferenceCenterRow, 1e-10, "custom ROI transformed row");
                Near(90.0, customRegion.ReferenceCenterColumn, 1e-10, "custom ROI transformed column");
                Near(90.0, customRegion.PhiDegrees, 1e-10, "custom ROI transformed angle");

                profile.FollowTemplateTransform = false;
                DetectionRoi boardBounds = analyzer.ResolveDetectionRoi(template, match, profile);
                Near(70.0, boardBounds.ReferenceCenterRow, 1e-10, "board bounds row");
                Near(70.0, boardBounds.ReferenceCenterColumn, 1e-10, "board bounds column");
                Near(10.0, boardBounds.HalfWidth, 1e-10, "rotated board bounds half width");
                Near(30.0, boardBounds.HalfHeight, 1e-10, "rotated board bounds half height");
                Near(0.0, boardBounds.PhiDegrees, 1e-10, "board bounds must be axis aligned");

                profile.RectangleFilterEnabled = false;
                profile.FollowTemplateTransform = true;
                findings = new BoardBlobAnalyzer().Analyze(image, template, match, profile);
                True(findings.All(item => !item.RejectedAsRectangle),
                    "disabling rectangle filtering must retain every qualifying blob");
            }
        }

        private static void TestBlobDetailedAnalysis()
        {
            using (Bitmap image = new Bitmap(20, 12, PixelFormat.Format24bppRgb))
            {
                for (int row = 3; row < 5; row++)
                {
                    for (int column = 4; column < 7; column++)
                        image.SetPixel(column, row, Color.White);
                }

                DetectionProfile profile = new DetectionProfile
                {
                    TemplateMatchingEnabled = false,
                    BrightThreshold = 200,
                    MinimumBlobArea = 1.0,
                    MaximumBlobArea = 100.0,
                    RectangleFilterEnabled = false,
                    MorphologyRadius = 0
                };
                BoardBlobAnalyzer analyzer = new BoardBlobAnalyzer();

                using (BlobAnalysisResult direct = analyzer.AnalyzeDirectDetailed(
                    image,
                    null,
                    profile,
                    true,
                    CancellationToken.None))
                {
                    True(direct.Roi != null, "direct analysis must expose its full-image ROI");
                    True(direct.ImageBounds == new Rectangle(0, 0, image.Width, image.Height),
                        "direct analysis bounds must cover the full source image");
                    True(direct.BinaryImage != null &&
                        direct.BinaryImage.Width == image.Width &&
                        direct.BinaryImage.Height == image.Height,
                        "direct binary preview must match the full source dimensions");
                    True(direct.ActivePixelCount == 6, "direct analysis active-pixel count");
                    True(direct.Findings.Count == 1 && direct.Findings[0].Area == 6.0,
                        "direct analysis must retain the synthetic blob");
                    True(direct.Findings[0].Contour.Count >= 5 &&
                        direct.Findings[0].Contour[0] == direct.Findings[0].Contour.Last(),
                        "the retained blob must expose a closed outer contour");
                    True(direct.BinaryImage.GetPixel(4, 3).R == 255 &&
                        direct.BinaryImage.GetPixel(0, 0).R == 0,
                        "binary preview must use white active pixels on a black background");
                    True(direct.ElapsedMilliseconds >= 0.0,
                        "detailed analysis must report elapsed time");
                }

                using (Bitmap morphologyImage = new Bitmap(80, 50, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(morphologyImage))
                    {
                        graphics.Clear(Color.Black);
                        graphics.FillRectangle(Brushes.White, 10, 8, 20, 14);
                        graphics.FillRectangle(Brushes.Black, 15, 12, 5, 4);
                        graphics.FillRectangle(Brushes.White, 45, 25, 20, 14);
                        graphics.FillRectangle(Brushes.White, 70, 4, 2, 2);
                    }
                    DetectionProfile morphologyProfile = new DetectionProfile
                    {
                        TemplateMatchingEnabled = false,
                        BrightThreshold = 200,
                        MinimumBlobArea = 1.0,
                        MaximumBlobArea = 1000.0,
                        RectangleFilterEnabled = false,
                        FillHoles = true,
                        MorphologyRadius = 1
                    };
                    using (BlobAnalysisResult processed = analyzer.AnalyzeDirectDetailed(
                        morphologyImage,
                        null,
                        morphologyProfile,
                        true,
                        CancellationToken.None))
                    {
                        True(processed.Findings.Count == 1,
                            "preprocessing must produce one largest connected component");
                        True(processed.BinaryImage.GetPixel(16, 13).R == 255,
                            "hole filling must restore dark holes inside the selected white region");
                        True(processed.BinaryImage.GetPixel(50, 30).R == 255,
                            "preprocessing must preserve the dominant white target");
                    }

                    morphologyProfile.AutoThreshold = true;
                    using (BlobAnalysisResult automatic = analyzer.AnalyzeDirectDetailed(
                        morphologyImage,
                        null,
                        morphologyProfile,
                        true,
                        CancellationToken.None))
                    {
                        True(automatic.EffectiveThreshold >= 0 && automatic.EffectiveThreshold < 255,
                            "automatic thresholding must report its effective ROI threshold");
                        True(automatic.Findings.Count == 1 && automatic.Findings[0].Area > 200.0,
                            "automatic thresholding must retain the dominant bright region");
                    }

                    morphologyProfile.AutoThreshold = false;
                    morphologyProfile.FillHoles = false;
                    morphologyProfile.MorphologyRadius = 0;
                    int unprocessedArea;
                    using (BlobAnalysisResult unclosed = analyzer.AnalyzeDirectDetailed(
                        morphologyImage, null, morphologyProfile, true, CancellationToken.None))
                    {
                        unprocessedArea = unclosed.ActivePixelCount;
                        True(unclosed.BinaryImage.GetPixel(16, 13).R == 0,
                            "without fill-up the dark hole inside a white target must remain visible");
                        True(unclosed.BinaryImage.GetPixel(70, 4).R == 255,
                            "without morphology a small white hole on black must remain visible");
                    }
                    morphologyProfile.MorphologyRadius = 4;
                    using (BlobAnalysisResult opened = analyzer.AnalyzeDirectDetailed(
                        morphologyImage, null, morphologyProfile, true, CancellationToken.None))
                    {
                        True(opened.BinaryImage.GetPixel(70, 4).R == 0,
                            "increasing morphology radius must fill a small white hole with black");
                        True(opened.BinaryImage.GetPixel(50, 30).R == 255,
                            "white-hole removal must preserve a larger white target");
                        True(opened.ActivePixelCount <= unprocessedArea,
                            "white-hole removal must not enlarge the selected white region");
                    }
                }
                BlobAnalysisResult disposable = analyzer.AnalyzeDirectDetailed(
                    image,
                    null,
                    profile,
                    true,
                    CancellationToken.None);
                disposable.Dispose();
                True(disposable.BinaryImage == null,
                    "disposing a detailed result must release its binary image reference");
                True(image.GetPixel(4, 3).R == 255,
                    "24-bpp direct analysis must not dispose or mutate its source image");

                TemplateDefinition template = new TemplateDefinition
                {
                    ModelReference = new ImagePose(6.0, 10.0, 0.0),
                    DetectionRoi = RectangleRegion(4.0, 5.0, 3.0, 2.0, 0.0)
                };
                TemplateMatchResult match = SuccessfulMatch(6.0, 10.0, 0.0);
                profile.TemplateMatchingEnabled = true;
                profile.FollowTemplateTransform = true;
                using (BlobAnalysisResult detailed = analyzer.AnalyzeDetailed(
                    image,
                    template,
                    match,
                    profile,
                    true,
                    CancellationToken.None))
                {
                    True(detailed.BinaryImage != null, "matched analysis must return a binary preview");
                    True(detailed.BinaryImage.Size == detailed.ImageBounds.Size,
                        "matched binary preview must be cropped to ImageBounds");
                    True(detailed.ImageBounds.Width < image.Width &&
                        detailed.ImageBounds.Height < image.Height,
                        "matched binary preview must not allocate an unnecessary full-image bitmap");
                    True(detailed.ImageBounds.Size == new Size(6, 4),
                        "axis-aligned ROI scanning must use exact extents instead of an oversized circumcircle");
                }

                FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine();
                using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
                {
                    profile.TemplateMatchingEnabled = false;
                    BoardInspectionService service = new BoardInspectionService(sdk);
                    InspectionResult directInspection = service.InspectWithoutMapping(
                        image,
                        null,
                        profile,
                        1);
                    True(directInspection.Executed && directInspection.Success &&
                        directInspection.ActualCount == 1 && directInspection.Items.Count == 1,
                        "template-disabled inspection must produce one full-image board result");
                    True(directInspection.Items[0].Targets.Count == 0,
                        "direct inspection must not invent mapped machine targets");
                    True(engine.LocateAllCallCount == 0,
                        "template-disabled inspection must bypass template matching");
                }

                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    bool cancelled = false;
                    try
                    {
                        using (analyzer.AnalyzeDirectDetailed(
                            image,
                            null,
                            profile,
                            true,
                            cancellation.Token))
                        {
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    True(cancelled, "detailed analysis must propagate cancellation");
                }
            }
        }

        private static void TestBinaryThresholdPolarities()
        {
            using (Bitmap brightTarget = new Bitmap(24, 16, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(brightTarget))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(Brushes.White, 4, 3, 4, 3);
                graphics.FillRectangle(Brushes.White, 18, 11, 2, 2);
                DetectionProfile profile = new DetectionProfile
                {
                    TemplateMatchingEnabled = false,
                    AutoThreshold = true,
                    DetectDarkForeground = false,
                    MinimumBlobArea = 1.0,
                    MaximumBlobArea = 100.0,
                    RectangleFilterEnabled = false,
                    FillHoles = false,
                    MorphologyRadius = 0
                };
                BoardBlobAnalyzer analyzer = new BoardBlobAnalyzer();
                using (BlobAnalysisResult result = analyzer.AnalyzeDirectDetailed(
                    brightTarget, null, profile, true, CancellationToken.None))
                {
                    True(result.Findings.Count == 1 && result.Findings[0].Area == 12.0,
                        "Otsu bright-foreground mode must not classify the black background as foreground");
                    True(result.BinaryImage.GetPixel(4, 3).R == 255 &&
                        result.BinaryImage.GetPixel(0, 0).R == 0,
                        "black-background mode must display white foreground on black");
                    True(result.BinaryImage.GetPixel(18, 11).R == 255,
                        "binary preview must show the complete threshold mask, including smaller components");
                }
            }

            using (Bitmap darkTarget = new Bitmap(24, 16, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(darkTarget))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Black, 7, 5, 5, 3);
                DetectionProfile profile = new DetectionProfile
                {
                    TemplateMatchingEnabled = false,
                    AutoThreshold = true,
                    DetectDarkForeground = true,
                    MinimumBlobArea = 1.0,
                    MaximumBlobArea = 100.0,
                    RectangleFilterEnabled = false,
                    FillHoles = false,
                    MorphologyRadius = 0
                };
                using (BlobAnalysisResult result = new BoardBlobAnalyzer().AnalyzeDirectDetailed(
                    darkTarget, null, profile, true, CancellationToken.None))
                {
                    True(result.Findings.Count == 1 && result.Findings[0].Area == 15.0,
                        "white-background mode must detect the largest dark component");
                    True(result.BinaryImage.GetPixel(7, 5).R == 0 &&
                        result.BinaryImage.GetPixel(0, 0).R == 255,
                        "white-background mode must preserve black-target/white-background display polarity");
                }
            }
        }

        private static void TestBlobDebugFormLayout()
        {
            using (Bitmap image = new Bitmap(40, 30))
            using (BlobDebugForm form = new BlobDebugForm(
                image,
                null,
                new DetectionProfile { TemplateMatchingEnabled = false }))
            {
                SplitContainer split = form.Controls.Find("mainSplit", true)
                    .OfType<SplitContainer>()
                    .Single();
                True(split.SplitterDistance >= split.Panel1MinSize,
                    "debug splitter must respect Panel1MinSize");
                True(split.SplitterDistance == 326,
                    "debug splitter distance must be assigned without a construction-time exception");
            }
        }

        private static void TestBlobDebugMouseWheel()
        {
            using (Bitmap image = new Bitmap(40, 30))
            using (BlobDebugForm form = new BlobDebugForm(
                image,
                null,
                new DetectionProfile { TemplateMatchingEnabled = false }))
            {
                NumericUpDown threshold = form.Controls.Find("nudBrightThreshold", true)
                    .OfType<NumericUpDown>()
                    .Single();
                threshold.Value = threshold.Maximum;
                HandledMouseEventArgs wheel = new HandledMouseEventArgs(
                    MouseButtons.None,
                    0,
                    0,
                    0,
                    120);
                MethodInfo handler = typeof(BlobDebugForm).GetMethod(
                    "NumericInput_MouseWheel",
                    BindingFlags.Static | BindingFlags.NonPublic);
                True(handler != null, "debug form must install its bounded wheel handler");
                handler.Invoke(null, new object[] { threshold, wheel });
                True(wheel.Handled && threshold.Value == threshold.Maximum,
                    "mouse wheel input at a numeric limit must be handled without overflow");
            }
        }

        private static void TestOverlappingRoiSelection()
        {
            using (Bitmap image = new Bitmap(100, 100))
            using (UiCalibrationCanvas canvas = new UiCalibrationCanvas())
            {
                canvas.Size = new Size(300, 300);
                canvas.CreateControl();
                canvas.SetImage(image);
                UiRotatedRectangle roi = new UiRotatedRectangle(50.0, 50.0, 20.0, 15.0, 0.0);
                canvas.TemplateRoi = roi;
                canvas.SearchRoi = roi;
                canvas.DetectionRoi = roi;
                canvas.TemplateRoiLocked = true;
                canvas.SelectedRoi = UiCanvasRoiTarget.Template;
                PointF bodyPoint = canvas.ImageToClient(new UiImageCoordinate(50.0, 60.0));
                MethodInfo mouseDown = typeof(UiCalibrationCanvas).GetMethod(
                    "OnMouseDown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo mouseMove = typeof(UiCalibrationCanvas).GetMethod(
                    "OnMouseMove",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo mouseUp = typeof(UiCalibrationCanvas).GetMethod(
                    "OnMouseUp",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Action clickBody = () =>
                {
                    mouseDown.Invoke(canvas, new object[]
                    {
                        new MouseEventArgs(MouseButtons.Left, 1, (int)bodyPoint.X, (int)bodyPoint.Y, 0)
                    });
                    mouseUp.Invoke(canvas, new object[]
                    {
                        new MouseEventArgs(MouseButtons.Left, 1, (int)bodyPoint.X, (int)bodyPoint.Y, 0)
                    });
                };

                clickBody();
                True(canvas.SelectedRoi == UiCanvasRoiTarget.Detection,
                    "a locked overlapping template ROI must not block selection of the detection ROI");

                clickBody();
                True(canvas.SelectedRoi == UiCanvasRoiTarget.Search,
                    "the second repeated body click must select the search ROI");

                clickBody();
                True(canvas.SelectedRoi == UiCanvasRoiTarget.Template,
                    "the third repeated body click must return to the template ROI");

                canvas.SelectedRoi = UiCanvasRoiTarget.Detection;
                mouseDown.Invoke(canvas, new object[]
                {
                    new MouseEventArgs(MouseButtons.Left, 1, (int)bodyPoint.X, (int)bodyPoint.Y, 0)
                });
                mouseMove.Invoke(canvas, new object[]
                {
                    new MouseEventArgs(MouseButtons.Left, 0, (int)bodyPoint.X + 5, (int)bodyPoint.Y, 0)
                });
                mouseUp.Invoke(canvas, new object[]
                {
                    new MouseEventArgs(MouseButtons.Left, 1, (int)bodyPoint.X + 5, (int)bodyPoint.Y, 0)
                });
                True(canvas.SelectedRoi == UiCanvasRoiTarget.Detection,
                    "dragging a selected detection ROI must not cycle to another ROI");
                True(canvas.DetectionRoi.CenterColumn > 50.0,
                    "dragging a selected detection ROI must move that ROI");
                Near(50.0, canvas.TemplateRoi.CenterColumn, 0.0,
                    "dragging a detection ROI must not move the coincident template ROI");
            }
        }

        private static void TestLegacyDetectionProfileDefaults()
        {
            const string legacyJson = "{\"BrightThreshold\":180,\"MinimumBlobArea\":20,\"MaximumBlobArea\":100000,\"RectangleFilterEnabled\":true,\"RectangleFillThreshold\":0.92,\"FollowTemplateTransform\":true}";
            string path = Path.Combine(Path.GetTempPath(), "nprcal-legacy-profile-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, legacyJson);
                DetectionProfile profile = new AtomicJsonStore().Load<DetectionProfile>(path);
                True(profile.TemplateMatchingEnabled,
                    "profiles written before the bypass switch must keep template matching enabled");
                True(profile.FillHoles && profile.MorphologyRadius == 1,
                    "legacy profiles must receive safe morphology defaults");
                True(profile.AutoThreshold,
                    "legacy profiles must opt into robust ROI-local automatic thresholding");
                True(!profile.DetectDarkForeground,
                    "legacy profiles must preserve black-background/bright-target polarity");

                DetectionProfile direct = new DetectionProfile { TemplateMatchingEnabled = false };
                new AtomicJsonStore().Save(path, direct);
                DetectionProfile roundTrip = new AtomicJsonStore().Load<DetectionProfile>(path);
                True(!roundTrip.TemplateMatchingEnabled,
                    "an explicitly disabled template-matching switch must survive persistence");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void TestHalconDetectionRoiTransform()
        {
            const double referenceRow = 112.0;
            const double referenceColumn = 105.0;
            const double targetRow = 164.0;
            const double targetColumn = 218.0;
            const double screenAngleDegrees = 13.0;
            const double roiAngleDegrees = 7.0;
            ImagePoint sourceCenter = new ImagePoint(referenceRow - 17.0, referenceColumn + 24.0);

            using (Bitmap reference = CreateHalconConventionImage(referenceRow, referenceColumn, 0.0))
            using (Bitmap target = CreateHalconConventionImage(targetRow, targetColumn, screenAngleDegrees))
            using (HalconTemplateEngine engine = new HalconTemplateEngine())
            {
                TemplateDefinition definition = new TemplateDefinition
                {
                    ModelType = TemplateModelType.Shape,
                    TemplateRoi = RectangleRegion(referenceRow, referenceColumn, 50.0, 46.0, 0.0),
                    DetectionRoi = RectangleRegion(
                        sourceCenter.Row,
                        sourceCenter.Column,
                        28.0,
                        14.0,
                        -roiAngleDegrees)
                };
                definition.ShapeParameters.NumLevels = 4;
                definition.ShapeParameters.AngleStartRad = -30.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleExtentRad = 60.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleStepRad = Math.PI / 180.0;
                definition.ShapeParameters.Contrast = 20;
                definition.ShapeParameters.MinContrast = 8;
                definition.MatchParameters.AngleStartRad = -30.0 * Math.PI / 180.0;
                definition.MatchParameters.AngleExtentRad = 60.0 * Math.PI / 180.0;
                definition.MatchParameters.MinScore = 0.55;
                definition.MatchParameters.NumMatches = 1;
                definition.MatchParameters.MaxOverlap = 0.5;
                definition.MatchParameters.NumLevels = 0;
                definition.MatchParameters.Greediness = 0.8;

                TemplateBuildResult build = engine.BuildTemplate(reference, definition);
                True(build.Success, "HALCON template build failed: " + build.Message);
                TemplateMatchResult match = engine.Locate(target, build.Template, false);
                True(match.Success, "HALCON template locate failed: " + match.Message);
                Near(-screenAngleDegrees, match.AngleDegrees, 2.0, "HALCON match angle convention");

                DetectionRoi transformed = new BoardBlobAnalyzer().ResolveDetectionRoi(
                    build.Template,
                    match,
                    new DetectionProfile { FollowTemplateTransform = true });
                ImagePoint expectedCenter = TransformScreenPoint(
                    sourceCenter,
                    referenceRow,
                    referenceColumn,
                    targetRow,
                    targetColumn,
                    screenAngleDegrees);
                Near(expectedCenter.Row, transformed.ReferenceCenterRow, 2.5, "HALCON ROI center row");
                Near(expectedCenter.Column, transformed.ReferenceCenterColumn, 2.5, "HALCON ROI center column");
                Near(
                    roiAngleDegrees + screenAngleDegrees,
                    transformed.PhiDegrees,
                    2.0,
                    "HALCON ROI screen angle");
            }
        }

        private static void TestReferenceTemplateDefaults()
        {
            ShapeModelParameters shape = new ShapeModelParameters();
            MatchParameters match = new MatchParameters();
            UiTemplateEditorState editor = new UiTemplateEditorState();

            True(shape.NumLevels == 5, "shape pyramid levels must match the reference default");
            Near(-0.3, shape.AngleStartRad, 0.0, "shape angle start default");
            Near(0.6, shape.AngleExtentRad, 0.0, "shape angle extent default");
            True(shape.MinContrast == 20, "XLD minimum contrast must match the reference default");
            Near(1.2, shape.FeatureExtraction.EdgeSigma, 0.0, "Canny sigma default");
            Near(60.0, shape.FeatureExtraction.EdgeLowThreshold, 0.0, "Canny low threshold default");
            Near(100.0, shape.FeatureExtraction.EdgeHighThreshold, 0.0, "Canny high threshold default");
            Near(6.0, shape.FeatureExtraction.PointSpacingPixels, 0.0, "feature distance default");
            True(shape.FeatureExtraction.MaximumPointCount == 20000,
                "feature point limit must match the reference default");

            Near(-10.0 * Math.PI / 180.0, match.AngleStartRad, 0.0, "match angle start default");
            Near(20.0 * Math.PI / 180.0, match.AngleExtentRad, 0.0, "match angle extent default");
            Near(0.5, match.MinScore, 0.0, "minimum score default");
            Near(0.3, match.MaxOverlap, 0.0, "maximum overlap default");
            True(match.NumLevels == 0, "match pyramid levels default to HALCON automatic");
            Near(0.85, match.Greediness, 0.0, "match greediness default");
            True(match.SubPixel == "interpolation", "match subpixel default");

            True(editor.Parameters.NumLevels == 5, "editor pyramid levels default");
            True(editor.Parameters.MatchNumLevels == 0, "editor search pyramid defaults to automatic");
            Near(-0.3 * 180.0 / Math.PI, editor.Parameters.AngleStartDegrees, 1e-12,
                "editor angle start default");
            Near(0.6 * 180.0 / Math.PI, editor.Parameters.AngleExtentDegrees, 1e-12,
                "editor angle extent default");
            Near(-10.0, editor.Parameters.MatchAngleStartDegrees, 1e-12,
                "editor search angle start default");
            Near(20.0, editor.Parameters.MatchAngleExtentDegrees, 1e-12,
                "editor search angle extent default");
            Near(20.0, editor.Parameters.MinimumContrast, 0.0,
                "editor minimum contrast default");
            Near(0.5, editor.Parameters.MinimumScore, 0.0, "editor minimum score default");
            Near(0.3, editor.Parameters.MaximumOverlap, 0.0,
                "editor maximum overlap default");
            Near(0.85, editor.Parameters.Greediness, 0.0, "editor greediness default");
            True(editor.Parameters.SubPixel == "interpolation", "editor subpixel default");
            Near(12.0, editor.Parameters.BrushRadius, 0.0, "editor erase radius default");
        }

        private static void TestXldContourSampling()
        {
            Type extractorType = typeof(XldTemplateMatchingEngine).Assembly.GetType(
                "NinePointRotationCalibration.Halcon.XldFeatureExtractor",
                true);
            MethodInfo resample = extractorType.GetMethod(
                "ResampleOrderedContour",
                BindingFlags.Static | BindingFlags.NonPublic);
            True(resample != null, "XLD ordered contour sampler was not found");

            List<ImagePoint> originalPointSample = (List<ImagePoint>)resample.Invoke(
                null,
                new object[]
                {
                    new List<ImagePoint>
                    {
                        new ImagePoint(0.0, 0.0),
                        new ImagePoint(0.0, 1.0),
                        new ImagePoint(0.0, 2.0),
                        new ImagePoint(0.0, 10.0)
                    },
                    6.0,
                    int.MaxValue
                });
            True(originalPointSample.Count == 2,
                "sampling must retain qualifying source points without interpolation");
            Near(0.0, originalPointSample[0].Column, 0.0, "sampled first source point");
            Near(10.0, originalPointSample[1].Column, 0.0, "sampled second source point");

            List<ImagePoint> noForcedEndpointSample = (List<ImagePoint>)resample.Invoke(
                null,
                new object[]
                {
                    new List<ImagePoint>
                    {
                        new ImagePoint(0.0, 0.0),
                        new ImagePoint(0.0, 6.0),
                        new ImagePoint(0.0, 10.0)
                    },
                    6.0,
                    int.MaxValue
                });
            True(noForcedEndpointSample.Count == 2,
                "sampling must not force an endpoint that is below the configured distance");
            Near(6.0, noForcedEndpointSample[1].Column, 0.0,
                "the last qualifying source point must remain last");
        }

        private static void TestXldConcaveBoardContourRetention()
        {
            using (Bitmap board = new Bitmap(240, 240, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(board))
            using (XldTemplateMatchingEngine engine = new XldTemplateMatchingEngine())
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.FillRectangle(Brushes.Black, 40, 40, 160, 160);
                graphics.FillRectangle(Brushes.White, 110, 35, 20, 80);

                TemplateDefinition definition = new TemplateDefinition
                {
                    ModelType = TemplateModelType.Shape,
                    TemplateRoi = RectangleRegion(120.0, 120.0, 100.0, 100.0, 0.0)
                };
                TemplateBuildResult build = engine.BuildTemplate(board, definition);
                True(build.Success, "concave board XLD build failed: " + build.Message);

                List<ImagePoint> points = build.ContourSegments.SelectMany(segment => segment).ToList();
                True(points.Any(point =>
                        Math.Abs(point.Row - 115.0) <= 2.0 &&
                        point.Column >= 113.0 &&
                        point.Column <= 127.0),
                    "the inward notch edge must not be removed by an outer-envelope filter");
                True(build.ContourSegments.Any(segment =>
                        segment.Zip(segment.Skip(1), (first, second) =>
                            Math.Sqrt(
                                Math.Pow(second.Row - first.Row, 2.0) +
                                Math.Pow(second.Column - first.Column, 2.0)))
                            .Any(distance => distance < 2.0)),
                    "default feature distance 6 must produce the reference 0.9 px model sampling");
            }
        }

        private static void TestXldShapeModel()
        {
            const double referenceRow = 112.0;
            const double referenceColumn = 105.0;
            const double targetRow = 164.0;
            const double targetColumn = 218.0;
            const double screenAngleDegrees = 13.0;

            using (Bitmap reference = CreateHalconConventionImage(referenceRow, referenceColumn, 0.0))
            using (Bitmap target = CreateHalconConventionImage(targetRow, targetColumn, screenAngleDegrees))
            using (XldTemplateMatchingEngine engine = new XldTemplateMatchingEngine())
            {
                TemplateDefinition definition = new TemplateDefinition
                {
                    ModelType = TemplateModelType.Shape,
                    TemplateRoi = RectangleRegion(referenceRow, referenceColumn, 50.0, 46.0, 0.0)
                };
                definition.ShapeParameters.NumLevels = 4;
                definition.ShapeParameters.AngleStartRad = -30.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleExtentRad = 60.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleStepRad = Math.PI / 180.0;
                definition.ShapeParameters.MinContrast = 8;
                definition.ShapeParameters.FeatureExtraction.PointSpacingPixels = 5.0;
                definition.ShapeParameters.FeatureExtraction.MinimumContourLengthPixels = 8.0;
                definition.ShapeParameters.FeatureExtraction.MaximumPointCount = 240;
                definition.MatchParameters = null;

                TemplateBuildResult build = engine.BuildTemplate(reference, definition);
                True(build.Success, "XLD template build failed: " + build.Message);
                True(build.Template != null && build.Template.HasUsableModel,
                    "XLD template build must return a serialized model");
                True(build.Template.MatchParameters != null,
                    "XLD template build must normalize missing match parameters");
                True(build.Template.ShapeParameters.Metric == "ignore_local_polarity",
                    "XLD template build must use ignore_local_polarity");
                True(build.ContourSegments.Count > 1,
                    "XLD template build must preserve multiple contour segments");
                True(build.ContourSegments.All(segment => segment != null && segment.Count >= 2),
                    "each XLD contour segment must remain an ordered polyline");
                True(build.ContourSegments.Sum(segment => segment.Count) <=
                    definition.ShapeParameters.FeatureExtraction.MaximumPointCount,
                    "XLD model contour count must respect the configured maximum");
                Console.WriteLine(
                    "       XLD model contours: {0} segments, {1} points",
                    build.ContourSegments.Count,
                    build.ContourSegments.Sum(segment => segment.Count));

                TemplateMatchResult match = engine.Locate(target, build.Template, true);
                True(match.Success, "XLD template locate failed: " + match.Message);
                Near(targetRow, match.Anchor.Row, 2.5, "XLD match anchor row");
                Near(targetColumn, match.Anchor.Column, 2.5, "XLD match anchor column");
                Near(-screenAngleDegrees, match.AngleDegrees, 2.0, "XLD match angle convention");
                True(match.ContourSegments.Count > 0,
                    "XLD locate must return transformed model polylines");

                using (Bitmap blank = new Bitmap(reference.Width, reference.Height, PixelFormat.Format24bppRgb))
                {
                    TemplateBuildResult blankBuild = engine.BuildTemplate(blank, definition);
                    True(!blankBuild.Success, "blank ROI must not create an XLD shape model");
                    True(blankBuild.ErrorCode == CalibrationErrorCode.ParameterInvalid,
                        "no-contour failure must be reported as a feature/parameter problem");
                }
            }
        }

        private static void TestTemplateEditorContourOverlay()
        {
            UiTemplateEditorState sparseState = new UiTemplateEditorState
            {
                ModelContours = new List<List<UiImageCoordinate>>
                {
                    new List<UiImageCoordinate>
                    {
                        new UiImageCoordinate(double.NaN, 1.0),
                        new UiImageCoordinate(2.0, double.PositiveInfinity),
                        new UiImageCoordinate(10.25, 20.75),
                        new UiImageCoordinate(12.25, 22.75),
                        new UiImageCoordinate(10.25, 40.75)
                    },
                    new List<UiImageCoordinate>
                    {
                        new UiImageCoordinate(30.5, 40.5),
                        new UiImageCoordinate(31.0, 41.0),
                        new UiImageCoordinate(30.5, 60.5)
                    }
                }
            };

            using (Bitmap image = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
            using (UiTemplateEditorForm form = new UiTemplateEditorForm(image, sparseState))
            {
                UiCalibrationCanvas canvas = GetPrivateField<UiCalibrationCanvas>(form, "_canvas");
                True(canvas.EditorOverlay.Polylines.Count == 0,
                    "feature preview must render sparse points without contour polylines");

                UiImageCoordinate[] expected =
                {
                    new UiImageCoordinate(10.25, 20.75),
                    new UiImageCoordinate(12.25, 22.75),
                    new UiImageCoordinate(10.25, 40.75),
                    new UiImageCoordinate(30.5, 40.5),
                    new UiImageCoordinate(31.0, 41.0),
                    new UiImageCoordinate(30.5, 60.5)
                };
                True(canvas.EditorOverlay.Points.Count == expected.Length,
                    "preview must reject only non-finite contour samples");
                for (int index = 0; index < expected.Length; index++)
                {
                    UiImageCoordinate actual = canvas.EditorOverlay.Points[index].Position;
                    True(!double.IsNaN(actual.Row) && !double.IsInfinity(actual.Row) &&
                         !double.IsNaN(actual.Column) && !double.IsInfinity(actual.Column),
                        "preview points must contain only finite coordinates");
                    Near(expected[index].Row, actual.Row, 0.0,
                        "preview must preserve absolute image row coordinates");
                    Near(expected[index].Column, actual.Column, 0.0,
                        "preview must preserve absolute image column coordinates");
                }
            }

            List<UiImageCoordinate> leadingContour = Enumerable.Range(0, 2500)
                .Select(index => new UiImageCoordinate(1000.0, 20.0 * index))
                .ToList();
            List<UiImageCoordinate> trailingContour = Enumerable.Range(0, 250)
                .Select(index => new UiImageCoordinate(2000.0, 20.0 * index))
                .ToList();
            UiTemplateEditorState completeState = new UiTemplateEditorState
            {
                ModelContours = new List<List<UiImageCoordinate>>
                {
                    leadingContour,
                    trailingContour
                }
            };

            using (Bitmap image = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
            using (UiTemplateEditorForm form = new UiTemplateEditorForm(image, completeState))
            {
                UiCalibrationCanvas canvas = GetPrivateField<UiCalibrationCanvas>(form, "_canvas");
                True(canvas.EditorOverlay.Polylines.Count == 0,
                    "complete feature preview must remain point-only");
                True(canvas.EditorOverlay.Points.Count == leadingContour.Count + trailingContour.Count,
                    "feature preview must display every extracted point");
                True(canvas.EditorOverlay.Points.Any(point => point.Position.Row == 2000.0),
                    "feature preview must include later contour segments");
            }
        }

        private static void TestTemplateEditorMetricModes()
        {
            UiTemplateEditorState state = new UiTemplateEditorState();
            state.Parameters.ModelType = TemplateModelType.Shape;
            state.Parameters.FeatureExtractionMode = ShapeFeatureExtractionMode.EdgeContours;
            state.Parameters.ShapeMetric = "ignore_global_polarity";
            state.Parameters.NccMetric = "ignore_color_polarity";

            using (Bitmap image = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
            using (UiTemplateEditorForm form = new UiTemplateEditorForm(image, state))
            {
                ComboBox modelType = GetPrivateField<ComboBox>(form, "_modelTypeCombo");
                ComboBox metric = GetPrivateField<ComboBox>(form, "_metricCombo");
                ComboBox subPixel = GetPrivateField<ComboBox>(form, "_subPixelCombo");
                CheckBox edgeContours = GetPrivateField<CheckBox>(form, "_edgeContourModeCheck");
                NumericUpDown contrast = GetPrivateField<NumericUpDown>(form, "_contrastInput");
                NumericUpDown minimumContrast = GetPrivateField<NumericUpDown>(form, "_minimumContrastInput");

                True(Convert.ToString(metric.SelectedItem) == "ignore_local_polarity",
                    "XLD mode must expose its fixed local-polarity metric");
                True(!metric.Enabled && !contrast.Enabled,
                    "XLD mode must disable metric and image-domain contrast controls");
                True(minimumContrast.Enabled,
                    "XLD mode must keep minimum contrast available for CreateShapeModelXld");

                edgeContours.Checked = false;
                True(metric.Enabled && contrast.Enabled && minimumContrast.Enabled,
                    "legacy Shape mode must enable its metric and contrast controls");
                True(Convert.ToString(metric.SelectedItem) == "ignore_global_polarity",
                    "legacy Shape mode must restore its saved metric");

                modelType.SelectedIndex = 1;
                True(Convert.ToString(metric.SelectedItem) == "ignore_color_polarity",
                    "NCC mode must restore its own saved metric");
                True(subPixel.Items.Count == 2 &&
                     Convert.ToString(subPixel.SelectedItem) == "interpolation",
                    "NCC mode must expose only its supported subpixel choices");
                True(metric.Enabled && contrast.Enabled && minimumContrast.Enabled,
                    "NCC mode must not inherit XLD control disabling");

                modelType.SelectedIndex = 0;
                True(Convert.ToString(metric.SelectedItem) == "ignore_global_polarity",
                    "returning to legacy Shape must restore the Shape metric");
                True(subPixel.Items.Count == 5,
                    "Shape mode must restore its least-squares subpixel choices");
            }
        }

        private static void TestCoreUiMapperRectangle2RoundTrip()
        {
            const double row = 123.5;
            const double column = 456.25;
            const double halfWidth = 37.0;
            const double halfHeight = 11.0;
            const double canvasAngleDegrees = 27.0;

            UiTemplateEditorState state = new UiTemplateEditorState
            {
                TemplateRoi = new UiRotatedRectangle(
                    row,
                    column,
                    halfWidth,
                    halfHeight,
                    canvasAngleDegrees)
            };
            state.Parameters.NumLevels = 6;
            state.Parameters.MatchNumLevels = 2;
            state.Parameters.AngleStartDegrees = -30.0;
            state.Parameters.AngleExtentDegrees = 60.0;
            state.Parameters.MatchAngleStartDegrees = -4.0;
            state.Parameters.MatchAngleExtentDegrees = 8.0;

            NinePointRotationCalibration.TemplateDefinition mapped = UiCoreUiMapper.ToCoreTemplate(
                state,
                null,
                640,
                480);
            True(mapped.TemplateRoi != null &&
                 mapped.TemplateRoi.Kind == NinePointRotationCalibration.RegionKind.Rectangle2,
                "UI mapper must emit a HALCON Rectangle2");
            Near(halfWidth, mapped.TemplateRoi.Length1, 0.0,
                "HALCON Length1 must equal canvas HalfWidth");
            Near(halfHeight, mapped.TemplateRoi.Length2, 0.0,
                "HALCON Length2 must equal canvas HalfHeight");
            Near(-canvasAngleDegrees * Math.PI / 180.0, mapped.TemplateRoi.PhiRadians, 1e-12,
                "HALCON Phi must use the opposite sign of the canvas angle");
            True(mapped.ShapeParameters.NumLevels == 6,
                "model pyramid level must remain independent");
            True(mapped.MatchParameters.NumLevels == 2,
                "search pyramid level must remain independent");
            Near(-4.0 * Math.PI / 180.0, mapped.MatchParameters.AngleStartRad, 1e-12,
                "search angle start must remain independent");
            Near(8.0 * Math.PI / 180.0, mapped.MatchParameters.AngleExtentRad, 1e-12,
                "search angle extent must remain independent");

            UiTemplateEditorState roundTrip = UiCoreUiMapper.FromCoreTemplate(mapped);
            True(roundTrip.TemplateRoi != null,
                "UI mapper must restore a Rectangle2 ROI");
            Near(row, roundTrip.TemplateRoi.CenterRow, 0.0,
                "Rectangle2 center row round-trip");
            Near(column, roundTrip.TemplateRoi.CenterColumn, 0.0,
                "Rectangle2 center column round-trip");
            Near(halfWidth, roundTrip.TemplateRoi.HalfWidth, 0.0,
                "Rectangle2 HalfWidth round-trip");
            Near(halfHeight, roundTrip.TemplateRoi.HalfHeight, 0.0,
                "Rectangle2 HalfHeight round-trip");
            Near(canvasAngleDegrees, roundTrip.TemplateRoi.AngleDegrees, 1e-12,
                "Rectangle2 angle sign round-trip");
            True(roundTrip.Parameters.NumLevels == 6,
                "model pyramid level round-trip");
            True(roundTrip.Parameters.MatchNumLevels == 2,
                "search pyramid level round-trip");
            Near(-4.0, roundTrip.Parameters.MatchAngleStartDegrees, 1e-12,
                "search angle start round-trip");
            Near(8.0, roundTrip.Parameters.MatchAngleExtentDegrees, 1e-12,
                "search angle extent round-trip");
        }

        private static void TestXldLegacyAndNccDelegation()
        {
            const double referenceRow = 112.0;
            const double referenceColumn = 105.0;
            const double targetRow = 164.0;
            const double targetColumn = 218.0;

            using (Bitmap reference = CreateHalconConventionImage(referenceRow, referenceColumn, 0.0))
            using (Bitmap target = CreateHalconConventionImage(targetRow, targetColumn, 0.0))
            using (XldTemplateMatchingEngine engine = new XldTemplateMatchingEngine())
            {
                TemplateDefinition definition = new TemplateDefinition
                {
                    ModelType = TemplateModelType.Shape,
                    TemplateRoi = RectangleRegion(referenceRow, referenceColumn, 50.0, 46.0, 0.0)
                };
                definition.ShapeParameters.NumLevels = 4;
                definition.ShapeParameters.AngleStartRad = -5.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleExtentRad = 10.0 * Math.PI / 180.0;
                definition.ShapeParameters.AngleStepRad = Math.PI / 180.0;
                definition.ShapeParameters.Contrast = 20;
                definition.ShapeParameters.MinContrast = 8;
                definition.ShapeParameters.FeatureExtraction = null;
                definition.MatchParameters.AngleStartRad = -5.0 * Math.PI / 180.0;
                definition.MatchParameters.AngleExtentRad = 10.0 * Math.PI / 180.0;
                definition.MatchParameters.MinScore = 0.55;
                definition.MatchParameters.NumMatches = 1;
                definition.MatchParameters.MaxOverlap = 0.5;
                definition.MatchParameters.Greediness = 0.8;

                TemplateBuildResult legacyBuild = engine.BuildTemplate(reference, definition);
                True(legacyBuild.Success, "legacy Shape delegate build failed: " + legacyBuild.Message);
                TemplateMatchResult legacyMatch = engine.Locate(target, legacyBuild.Template, false);
                True(legacyMatch.Success, "legacy Shape delegate locate failed: " + legacyMatch.Message);
                Near(targetRow, legacyMatch.Anchor.Row, 2.0, "legacy Shape delegate row");
                Near(targetColumn, legacyMatch.Anchor.Column, 2.0, "legacy Shape delegate column");

                definition.ModelType = TemplateModelType.Ncc;
                definition.NccParameters.NumLevels = 3;
                definition.NccParameters.AngleStartRad = -5.0 * Math.PI / 180.0;
                definition.NccParameters.AngleExtentRad = 10.0 * Math.PI / 180.0;
                definition.NccParameters.AngleStepRad = Math.PI / 180.0;
                definition.MatchParameters.MinScore = 0.75;

                TemplateBuildResult nccBuild = engine.BuildTemplate(reference, definition);
                True(nccBuild.Success, "NCC delegate build failed: " + nccBuild.Message);
                TemplateMatchResult nccMatch = engine.Locate(target, nccBuild.Template, false);
                True(nccMatch.Success, "NCC delegate locate failed: " + nccMatch.Message);
                Near(targetRow, nccMatch.Anchor.Row, 1.5, "NCC delegate row");
                Near(targetColumn, nccMatch.Anchor.Column, 1.5, "NCC delegate column");
            }
        }

        private static Bitmap CreateHalconConventionImage(
            double centerRow,
            double centerColumn,
            double angleDegrees)
        {
            Bitmap bitmap = new Bitmap(320, 260, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(22, 22, 22));
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.TranslateTransform((float)centerColumn, (float)centerRow);
                graphics.RotateTransform((float)angleDegrees);

                PointF[] body =
                {
                    new PointF(-36.0f, -31.0f),
                    new PointF(17.0f, -31.0f),
                    new PointF(17.0f, -17.0f),
                    new PointF(36.0f, -17.0f),
                    new PointF(36.0f, 24.0f),
                    new PointF(4.0f, 24.0f),
                    new PointF(4.0f, 36.0f),
                    new PointF(-36.0f, 36.0f)
                };

                using (Brush bodyBrush = new SolidBrush(Color.FromArgb(225, 225, 225)))
                using (Brush midBrush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                using (Brush darkBrush = new SolidBrush(Color.FromArgb(38, 38, 38)))
                using (Pen accentPen = new Pen(Color.FromArgb(250, 250, 250), 4.0f))
                {
                    graphics.FillPolygon(bodyBrush, body);
                    graphics.FillRectangle(midBrush, -27.0f, -21.0f, 20.0f, 17.0f);
                    graphics.FillEllipse(darkBrush, 9.0f, -8.0f, 16.0f, 16.0f);
                    graphics.FillRectangle(darkBrush, -25.0f, 13.0f, 19.0f, 12.0f);
                    graphics.DrawLine(accentPen, 8.0f, 14.0f, 27.0f, 14.0f);
                }
            }

            return bitmap;
        }

        private static ImagePoint TransformScreenPoint(
            ImagePoint point,
            double sourceCenterRow,
            double sourceCenterColumn,
            double targetCenterRow,
            double targetCenterColumn,
            double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double localColumn = point.Column - sourceCenterColumn;
            double localRow = point.Row - sourceCenterRow;
            return new ImagePoint(
                targetCenterRow + (sin * localColumn) + (cos * localRow),
                targetCenterColumn + (cos * localColumn) - (sin * localRow));
        }

        private static void TestBoardInspection()
        {
            TemplateMatchResult board = SuccessfulMatch(60.0, 80.0, 5.0);
            board.ContourSegments.Add(new List<ImagePoint>
            {
                new ImagePoint(58.0, 78.0),
                new ImagePoint(62.0, 82.0)
            });
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateAllResult = new NinePointRotationCalibration.Halcon.TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Located",
                    Matches = new List<TemplateMatchResult> { board }
                }
            };

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            using (Bitmap image = new Bitmap(160, 120))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Black);
                    using (Pen pen = new Pen(Color.White, 3.0f))
                    {
                        graphics.DrawLine(pen, 80, 50, 80, 70);
                        graphics.DrawLine(pen, 70, 60, 90, 60);
                    }
                }

                CalibrationJob positioning = new CalibrationJob();
                positioning.Template.ModelReference = new ImagePose(60.0, 80.0, 0.0);
                positioning.Template.TemplateRoi = RectangleRegion(60.0, 80.0, 30.0, 25.0, 0.0);
                positioning.Template.DetectionRoi = RectangleRegion(60.0, 80.0, 30.0, 25.0, 0.0);
                CalibrationResult calibration = new CalibrationResult
                {
                    TranslationSuccess = true,
                    PixelToStageMatrix = AffineMatrix2D.Identity,
                    Rotation = new RotationCalibrationResult
                    {
                        Success = true,
                        IsAvailable = true,
                        Direction = RotationDirection.Same
                    }
                };
                TeachingData teaching = new TeachingData
                {
                    Left = ValidTeaching(TeachingSide.Left, 60.0, 80.0, 0.0, 100.0, 200.0, 10.0),
                    Right = ValidTeaching(TeachingSide.Right, 60.0, 80.0, 0.0, 130.0, 200.0, 10.0)
                };
                DetectionProfile profile = new DetectionProfile
                {
                    BrightThreshold = 200,
                    MinimumBlobArea = 8.0,
                    MaximumBlobArea = 1000.0,
                    RectangleFilterEnabled = true,
                    RectangleFillThreshold = 0.9
                };

                BoardInspectionService service = new BoardInspectionService(sdk);
                InspectionResult marked = service.Inspect(
                    image, positioning, calibration, teaching, profile, 1, 12.0);
                True(marked.Executed && marked.Success, "inspection should execute when prerequisites are valid");
                True(!marked.Pass && marked.ErrorCode == "MANUAL_MARK_FOUND", "white cross must fail process judgement");
                True(marked.Items.Count == 1 && marked.Items[0].Targets.Count == 2,
                    "each board must include left and right mapped targets");
                True(marked.Items[0].DetectionRegion != null,
                    "each located board must expose its actual detection region");
                True(engine.LastLocateAllIncludeContour == true,
                    "board inspection must request match contours from the SDK");
                True(engine.LastLocateAllNumMatches == 2,
                    "board inspection must bound matching to expected count plus one overflow candidate");
                True(marked.Items[0].MatchContourSegments.Count == 1 &&
                    marked.Items[0].MatchContourSegments[0].Count == 2,
                    "board inspection must pass match contour segments to its result");
                True(!ReferenceEquals(
                    board.ContourSegments[0],
                    marked.Items[0].MatchContourSegments[0]),
                    "inspection results must own their contour segment lists");
                Near(3.0, marked.Items[0].Targets[0].ROffset, 1e-10, "board R offset");

                InspectionResult mismatch = service.Inspect(
                    image, positioning, calibration, teaching, profile, 2, 12.0);
                True(!mismatch.Success && mismatch.ErrorCode == "BOARD_COUNT_MISMATCH",
                    "wrong located count must be a structured failure");

                calibration.Rotation.IsAvailable = false;
                InspectionResult missingRotation = service.Inspect(
                    image, positioning, calibration, teaching, profile, 1, 12.0);
                True(!missingRotation.Executed && missingRotation.ErrorCode == "CALIBRATION_MISSING",
                    "missing rotation calibration must fail before acquisition results are trusted");

                calibration.Rotation.IsAvailable = true;
                teaching.Right.IsValid = false;
                InspectionResult missingTeaching = service.Inspect(
                    image, positioning, calibration, teaching, profile, 1, 12.0);
                True(!missingTeaching.Executed && missingTeaching.ErrorCode == "TEACHING_MISSING",
                    "both teaching poses are required");

                teaching.Right = ValidTeaching(TeachingSide.Right, 60.0, 80.0, 0.0, 130.0, 200.0, 10.0);
                engine.LocateAllResult = new NinePointRotationCalibration.Halcon.TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Located shuffled grid",
                    Matches = new List<TemplateMatchResult>
                    {
                        SuccessfulMatch(90.0, 130.0, 0.0),
                        SuccessfulMatch(32.0, 80.0, 0.0),
                        SuccessfulMatch(88.0, 20.0, 0.0),
                        SuccessfulMatch(29.0, 140.0, 0.0),
                        SuccessfulMatch(92.0, 75.0, 0.0),
                        SuccessfulMatch(30.0, 15.0, 0.0)
                    }
                };
                InspectionResult ordered = service.Inspect(
                    image, positioning, calibration, teaching, profile, 6, 12.0);
                True(ordered.Success && ordered.Items.Count == 6, "shuffled six-board grid should inspect successfully");
                AssertBoardPosition(ordered.Items[0], 1, 30.0, 15.0);
                AssertBoardPosition(ordered.Items[1], 2, 32.0, 80.0);
                AssertBoardPosition(ordered.Items[2], 3, 29.0, 140.0);
                AssertBoardPosition(ordered.Items[3], 4, 88.0, 20.0);
                AssertBoardPosition(ordered.Items[4], 5, 92.0, 75.0);
                AssertBoardPosition(ordered.Items[5], 6, 90.0, 130.0);
            }
        }

        private static async Task TestMockCameraAsync()
        {
            using (MockCameraDevice camera = new MockCameraDevice())
            {
                IReadOnlyList<CameraDescriptor> devices = await camera.EnumerateAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                True(devices.Count == 1, "mock camera enumeration");
                await camera.ConnectAsync(devices[0].Id, CancellationToken.None).ConfigureAwait(false);
                await camera.SetExposureAsync(5000.0, CancellationToken.None).ConfigureAwait(false);
                using (CameraFrame first = await camera.CaptureAsync(CancellationToken.None).ConfigureAwait(false))
                using (CameraFrame second = await camera.CaptureAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    True(first.Image != null && first.Image.Width == 1600 && first.Image.Height == 1000,
                        "mock frame dimensions");
                    True(second.Sequence == first.Sequence + 1, "frame sequence must increase");
                    True(!ReferenceEquals(first.Image, second.Image), "each frame must own an independent bitmap");
                    True(first.Image.GetPixel(40, 972).ToArgb() == first.Image.GetPixel(500, 972).ToArgb(),
                        "mock frame must not draw a decorative progress pulse below the image");
                }
                await camera.DisconnectAsync().ConfigureAwait(false);
                True(!camera.IsConnected, "mock camera disconnect");
            }
        }

        private static async Task TestStartupCancellationAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Station.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                StationOptions options = CreateTestOptions(
                    directory,
                    Path.Combine(directory, "detection.json"),
                    Path.Combine(directory, "positioning.nprcal"));
                options.TcpPort = FindAvailableTcpPort();
                ControllableCameraDevice device = new ControllableCameraDevice { BlockEnumeration = true };
                using (StationRuntime runtime = CreateRuntime(
                    Path.Combine(directory, "station.json"),
                    options,
                    device))
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    Task startup = runtime.StartAsync(cancellation.Token);
                    await WithTimeout(device.EnumerationStarted.Task, 3000).ConfigureAwait(false);
                    cancellation.Cancel();
                    await ExpectCancelled(startup).ConfigureAwait(false);
                    True(device.EnumerationCancellationObserved,
                        "startup cancellation must reach the in-flight camera enumeration");
                    await runtime.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static async Task TestPreviewHealthAsync()
        {
            ControllableCameraDevice device = new ControllableCameraDevice
            {
                CaptureFailuresBeforeSuccess = 3
            };
            using (CameraAcquisitionService camera = new CameraAcquisitionService(device, 50))
            {
                await camera.ConnectAsync("TEST-CAMERA", 1000.0, CancellationToken.None).ConfigureAwait(false);
                object stateSync = new object();
                List<bool> states = new List<bool>();
                TaskCompletionSource<bool> recovered = NewCompletion<bool>();
                camera.PreviewHealthChanged += delegate(bool available, string detail)
                {
                    lock (stateSync) states.Add(available);
                    if (available) recovered.TrySetResult(true);
                };

                camera.StartPreview();
                await WithTimeout(recovered.Task, 4000).ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
                await camera.StopPreviewAsync().ConfigureAwait(false);

                lock (stateSync)
                {
                    True(states.Count == 2, "preview health must not emit a state for every frame");
                    True(!states[0] && states[1],
                        "three consecutive failures must report unavailable, followed by one recovery");
                }
            }
        }

        private static async Task TestEnablementAndProtocolAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Station.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                AtomicJsonStore store = new AtomicJsonStore();

                string disabledOptionsPath = Path.Combine(directory, "station-disabled.json");
                string disabledDetectionPath = Path.Combine(directory, "detection-disabled.json");
                StationOptions disabled = CreateTestOptions(
                    directory,
                    disabledDetectionPath,
                    Path.Combine(directory, "positioning-disabled.nprcal"));
                disabled.TcpPort = FindAvailableTcpPort();
                disabled.CameraEnabled = false;
                disabled.CalibrationEnabled = false;
                store.Save(disabledOptionsPath, disabled);
                store.Save(disabledDetectionPath, new DetectionProfile());

                using (StationRuntime runtime = CreateRuntime(disabledOptionsPath, disabled))
                {
                    runtime.Log += delegate(object sender, LogEventArgs e)
                    {
                        Console.WriteLine("[TCP-DIAG] disabled log {0}: {1}", e.Level, e.Message);
                    };
                    Console.WriteLine("[TCP-DIAG] disabled StartAsync begin");
                    await runtime.StartAsync().ConfigureAwait(false);
                    Console.WriteLine("[TCP-DIAG] disabled StartAsync end");
                    True(runtime.IsStarted, "camera-disabled station must still start its TCP listener");
                    True(!runtime.IsCameraConnected, "camera-disabled startup must skip automatic connection");

                    SemaphoreSlim workflowGate = GetWorkflowGate(runtime);
                    True(workflowGate.Wait(0), "test must reserve the workflow gate");
                    ProtocolEnvelope response;
                    try
                    {
                        Console.WriteLine("[TCP-DIAG] disabled ExchangeAsync begin");
                        response = await ExchangeAsync(
                            disabled.TcpPort,
                            InspectionTrigger("camera-disabled-request"));
                        Console.WriteLine("[TCP-DIAG] disabled ExchangeAsync end");
                    }
                    finally
                    {
                        workflowGate.Release();
                    }
                    True(response.Type == ProtocolMessageTypes.Error, "disabled production trigger must return system.error");
                    True(response.CorrelationId == "camera-disabled-request", "disabled error correlation");
                    True(Convert.ToString(response.Data["code"]) == ProtocolErrorCodes.CameraDisabled,
                        "camera-disabled trigger must return CAMERA_DISABLED");
                    True(!(bool)response.Data["retryable"],
                        "CAMERA_DISABLED must remain non-retryable even while another workflow owns the gate");

                    bool framePublished = false;
                    runtime.FrameReady += delegate(object sender, FrameEventArgs e)
                    {
                        framePublished = e != null && e.Image != null &&
                            string.Equals(e.Source, "offline-inspection", StringComparison.Ordinal);
                    };
                    Console.WriteLine("[TCP-DIAG] disabled offline begin");
                    using (Bitmap localImage = new Bitmap(48, 32))
                    {
                        InspectionResult localResult = await runtime.RunOfflineInspectionAsync(
                            localImage,
                            1,
                            0.0,
                            CancellationToken.None).ConfigureAwait(false);
                        True(localResult != null, "offline inspection must return a structured result");
                    }
                    Console.WriteLine("[TCP-DIAG] disabled offline end");
                    True(framePublished, "offline inspection must publish the imported image");
                    True(!runtime.IsCameraConnected, "offline inspection must not connect or acquire from the camera");
                    Console.WriteLine("[TCP-DIAG] disabled StopAsync begin");
                    await runtime.StopAsync().ConfigureAwait(false);
                    Console.WriteLine("[TCP-DIAG] disabled StopAsync end");
                }

                string calibrationDisabledOptionsPath = Path.Combine(directory, "station-calibration-disabled.json");
                string calibrationDisabledDetectionPath = Path.Combine(directory, "detection-calibration-disabled.json");
                StationOptions calibrationDisabled = CreateTestOptions(
                    directory,
                    calibrationDisabledDetectionPath,
                    Path.Combine(directory, "positioning-calibration-disabled.nprcal"));
                calibrationDisabled.TcpPort = FindAvailableTcpPort();
                calibrationDisabled.CameraEnabled = true;
                calibrationDisabled.CalibrationEnabled = false;
                store.Save(calibrationDisabledOptionsPath, calibrationDisabled);
                store.Save(calibrationDisabledDetectionPath, new DetectionProfile());

                using (StationRuntime runtime = CreateRuntime(calibrationDisabledOptionsPath, calibrationDisabled))
                {
                    runtime.Log += delegate(object sender, LogEventArgs e)
                    {
                        Console.WriteLine("[TCP-DIAG] calibration-disabled log {0}: {1}", e.Level, e.Message);
                    };
                    Console.WriteLine("[TCP-DIAG] calibration-disabled StartAsync begin");
                    await runtime.StartAsync().ConfigureAwait(false);
                    Console.WriteLine("[TCP-DIAG] calibration-disabled StartAsync end");
                    True(runtime.IsCameraConnected, "enabled Mock camera must connect automatically at startup");

                    SemaphoreSlim workflowGate = GetWorkflowGate(runtime);
                    True(workflowGate.Wait(0), "test must reserve the workflow gate");
                    ProtocolEnvelope response;
                    try
                    {
                        Console.WriteLine("[TCP-DIAG] calibration-disabled ExchangeAsync begin");
                        response = await ExchangeAsync(
                            calibrationDisabled.TcpPort,
                            InspectionTrigger("calibration-disabled-request"));
                        Console.WriteLine("[TCP-DIAG] calibration-disabled ExchangeAsync end");
                    }
                    finally
                    {
                        workflowGate.Release();
                    }
                    True(response.Type == ProtocolMessageTypes.Error, "calibration-disabled trigger must return system.error");
                    True(Convert.ToString(response.Data["code"]) == ProtocolErrorCodes.CalibrationDisabled,
                        "calibration-disabled trigger must return CALIBRATION_DISABLED");
                    True(!(bool)response.Data["retryable"],
                        "CALIBRATION_DISABLED must remain non-retryable while another workflow owns the gate");
                    Console.WriteLine("[TCP-DIAG] calibration-disabled StopAsync begin");
                    await runtime.StopAsync().ConfigureAwait(false);
                    Console.WriteLine("[TCP-DIAG] calibration-disabled StopAsync end");
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void TestInspectionWithoutMapping()
        {
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateAllResult = new NinePointRotationCalibration.Halcon.TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Located for offline visual inspection",
                    Matches = new List<TemplateMatchResult> { SuccessfulMatch(40.0, 60.0, 2.0) }
                }
            };

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            using (Bitmap image = new Bitmap(120, 80))
            {
                CalibrationJob positioning = new CalibrationJob();
                positioning.Template.ModelReference = new ImagePose(40.0, 60.0, 0.0);
                positioning.Template.TemplateRoi = RectangleRegion(40.0, 60.0, 25.0, 20.0, 0.0);
                positioning.Template.DetectionRoi = RectangleRegion(40.0, 60.0, 25.0, 20.0, 0.0);
                DetectionProfile profile = new DetectionProfile
                {
                    BrightThreshold = 200,
                    MinimumBlobArea = 5.0,
                    MaximumBlobArea = 1000.0
                };

                BoardInspectionService service = new BoardInspectionService(sdk);
                InspectionResult visualOnly = service.InspectWithoutMapping(image, positioning, profile, 1);
                True(visualOnly.Executed && visualOnly.Success,
                    "calibration-disabled mode must still execute positioning, count, ROI, and Blob analysis");
                True(visualOnly.Items.Count == 1 && visualOnly.Items[0].Targets.Count == 0,
                    "visual-only inspection must not invent left/right machine targets");

                InspectionResult production = service.Inspect(
                    image,
                    positioning,
                    null,
                    null,
                    profile,
                    1,
                    0.0);
                True(!production.Executed && production.ErrorCode == "CALIBRATION_MISSING",
                    "production mapping must continue to require a valid calibration");
            }
        }

        private static void TestLegacyOptionsMigration()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Station.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "legacy-station.json");
                AtomicJsonStore store = new AtomicJsonStore();
                store.Save(path, new StationOptions());
                string legacyJson = File.ReadAllText(path)
                    .Replace("\"_calibrationEnabled\":true,", string.Empty)
                    .Replace("\"_cameraEnabled\":true,", string.Empty)
                    .Replace("\"_configurationVersion\":1,", string.Empty);
                File.WriteAllText(path, legacyJson);
                StationOptions options = store.Load<StationOptions>(path);

                True(options.NormalizeAfterLoad(), "version 0 settings must be migrated");
                True(options.CameraEnabled && options.CalibrationEnabled,
                    "missing legacy enable flags must preserve the original enabled behavior");
                True(options.ConfigurationVersion == StationOptions.CurrentConfigurationVersion,
                    "migration must stamp the current configuration version");
                True(!options.NormalizeAfterLoad(), "current settings must not be migrated repeatedly");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void TestDesignerSafeMainForm()
        {
            using (MainForm form = new MainForm())
            {
                True(form.Viewport != null, "viewport must be initialized by the parameterless constructor");
                True(form.CalibrationGrid != null, "calibration grid must be initialized");
                True(!form.IsHandleCreated, "constructing the form must not force a native window handle");
            }
        }

        private static void TestOfflineModeControls()
        {
            using (MainForm form = new MainForm())
            {
                True(form.CameraEnabledInput != null && form.CameraEnabledInput.Name == "chkCameraEnabled",
                    "camera enable switch must be exposed");
                True(form.CalibrationEnabledInput != null &&
                    form.CalibrationEnabledInput.Name == "chkCalibrationEnabled",
                    "calibration enable switch must be exposed");
                True(form.ImportImageButton != null && form.ImportImageButton.Name == "btnImportImage",
                    "local image import command must be exposed");
                True(form.DetectCurrentImageButton != null &&
                    form.DetectCurrentImageButton.Name == "btnDetectCurrentImage",
                    "current-image inspection command must be exposed");
                True(form.Controls.Find("chkBinaryPreview", true).Length == 1,
                    "main detection page must expose a binary-image toggle");
                True(form.Controls.Find("chkAutoThreshold", true).Length == 1,
                    "main detection page must expose automatic ROI thresholding");
                True(form.Controls.Find("chkDarkForeground", true).Length == 1,
                    "main detection page must expose black-target/white-background polarity");
                True(form.Controls.Find("nudMorphologyRadius", true).Length == 1 &&
                    form.Controls.Find("chkFillHoles", true).Length == 1,
                    "main detection page must expose morphology and hole-filling controls");
                True(form.CameraStatusIndicator != null &&
                    form.CameraStatusIndicator.Name == "lblCameraIndicator" &&
                    form.CameraStatusIndicator.Text == "\u25cf",
                    "camera state must have a dedicated status dot");

                Label hud = form.Controls.Find("lblInspectionHud", true)
                    .OfType<Label>()
                    .Single();
                form.SetInspectionHud(42.4, 24.1, 11.6, 2.0, 3.0, 1.0, 1.0, 4.0,
                    6, 6, true, false);
                True(hud.Text.Contains("CT 42 ms") && hud.Text.Contains("结果 OK") &&
                    hud.Text.Contains("图转 2") && hud.Text.Contains("连通 4"),
                    "main viewport HUD must show concise cycle time and inspection result");

                form.SetCameraStatus(CameraUiState.Connected, "Mock / mock-camera-0");
                True(form.CameraStatusState == CameraUiState.Connected,
                    "camera status should retain the connected state");
                True(form.CameraStatusIndicator.ForeColor.G > form.CameraStatusIndicator.ForeColor.R,
                    "connected camera indicator must be green");

                True(form.TcpStatusIndicator != null &&
                    form.TcpStatusIndicator.Name == "lblTcpIndicator" &&
                    form.TcpStatusIndicator.Text == "\u25cf",
                    "TCP state must have a dedicated status dot");
                form.SetTcpStatus(true, "Listening on 0.0.0.0:9100");
                True(form.TcpStatusState == TcpUiState.Listening,
                    "TCP server without a client should show the listening state");
                True(form.TcpStatusIndicator.ForeColor.R > form.TcpStatusIndicator.ForeColor.G,
                    "TCP listening indicator must be amber");
                form.SetTcpStatus(true, "上位机 127.0.0.1:50210");
                True(form.TcpStatusState == TcpUiState.Connected,
                    "TCP server with an upper-machine client should show the connected state");
                True(form.TcpStatusIndicator.ForeColor.G > form.TcpStatusIndicator.ForeColor.R,
                    "connected TCP indicator must be green");

                form.RefreshFeatureAvailability(false, false, false);
                True(form.ImportImageButton.Enabled,
                    "local image import must remain available with camera and calibration disabled");
                True(!form.CameraEnabledInput.Checked && !form.CalibrationEnabledInput.Checked,
                    "mode switches must reflect disabled state");

                string[] calibrationTemplateActions =
                {
                    "template.calibration.create",
                    "template.calibration.load",
                    "template.calibration.test"
                };
                string[] positioningTemplateActions =
                {
                    "template.positioning.create",
                    "template.positioning.load",
                    "template.positioning.test"
                };

                foreach (string action in calibrationTemplateActions)
                {
                    Control control = form.FindActionControl(action);
                    True(control != null, "calibration template action must exist: " + action);
                    True(!control.Enabled,
                        "calibration template action must be disabled when calibration is disabled: " + action);
                }
                foreach (string action in positioningTemplateActions)
                {
                    Control control = form.FindActionControl(action);
                    True(control != null, "positioning template action must exist: " + action);
                    True(control.Enabled,
                        "positioning template action must remain available for imported images: " + action);
                }

                form.RefreshFeatureAvailability(false, true, false);
                foreach (string action in calibrationTemplateActions)
                {
                    True(form.FindActionControl(action).Enabled,
                        "calibration template action must support imported images when calibration is enabled: " + action);
                }

                form.RefreshFeatureAvailability(false, true, true);
                foreach (string action in calibrationTemplateActions.Concat(positioningTemplateActions))
                {
                    True(!form.FindActionControl(action).Enabled,
                        "template action must be disabled while a workflow is busy: " + action);
                }
            }
        }

        private static void TestLockedFrameDisplay()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Station.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                StationOptions options = CreateTestOptions(
                    directory,
                    Path.Combine(directory, "detection.json"),
                    Path.Combine(directory, "positioning.nprcal"));
                options.CameraEnabled = false;
                using (StationRuntime runtime = CreateRuntime(
                    Path.Combine(directory, "station.json"),
                    options))
                using (MainForm form = new MainForm())
                using (MainFormRuntimeBinder binder = new MainFormRuntimeBinder(form, runtime))
                {
                    using (FrameEventArgs inspection = SolidFrame(Color.Red, "offline-inspection"))
                        InvokeBinder(binder, "Runtime_FrameReady", runtime, inspection);

                    InspectionResult result = new InspectionResult
                    {
                        Executed = true,
                        Success = true,
                        Pass = true,
                        ExpectedCount = 1,
                        ActualCount = 1,
                        Message = "OK",
                        Items = new List<BoardInspectionItem>
                        {
                            new BoardInspectionItem
                            {
                                BoardIndex = 1,
                                ImageRow = 4.0,
                                ImageColumn = 5.0,
                                MatchScore = 0.95,
                                Blobs = new List<BlobFinding>
                                {
                                    new BlobFinding
                                    {
                                        Area = 25.0,
                                        Contour = new List<PointF>
                                        {
                                            new PointF(2.0F, 2.0F),
                                            new PointF(8.0F, 2.0F),
                                            new PointF(8.0F, 8.0F),
                                            new PointF(2.0F, 2.0F)
                                        }
                                    }
                                }
                            }
                        }
                    };
                    InvokeBinder(
                        binder,
                        "Runtime_InspectionCompleted",
                        runtime,
                        new InspectionEventArgs(result));
                    True(GetViewportPointOverlayCount(form.Viewport) == 0 &&
                        GetViewportContourOverlayCount(form.Viewport) == 1,
                        "inspection result must draw only the largest blob edge on its captured frame");

                    using (FrameEventArgs preview = SolidFrame(Color.Blue, "preview"))
                        InvokeBinder(binder, "Runtime_FrameReady", runtime, preview);
                    Bitmap locked = GetLockedDisplayImage(binder);
                    True(locked != null && locked.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(),
                        "preview must not replace a locked offline or inspection frame");
                    True(GetViewportContourOverlayCount(form.Viewport) == 1,
                        "ignored preview must not detach result overlays from their frame");

                    using (FrameEventArgs manual = SolidFrame(Color.Green, "manual"))
                        InvokeBinder(binder, "Runtime_FrameReady", runtime, manual);
                    True(GetViewportPointOverlayCount(form.Viewport) == 0,
                        "a new explicit frame must clear overlays from the previous frame");
                    InvokeBinder(
                        binder,
                        "Runtime_InspectionCompleted",
                        runtime,
                        new InspectionEventArgs(result));
                    True(GetViewportPointOverlayCount(form.Viewport) == 0,
                        "an inspection result must not draw on a newer unrelated frame");

                    InvokeBinder(binder, "ResumeLivePreview");
                    True(GetLockedDisplayImage(binder) == null,
                        "resuming camera preview must release the retained offline frame");
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void TestEmptyViewportIsBlack()
        {
            using (ImageViewport viewport = new ImageViewport())
            using (Bitmap rendered = new Bitmap(320, 240))
            {
                viewport.Size = rendered.Size;
                using (Graphics graphics = Graphics.FromImage(rendered)) graphics.Clear(Color.Magenta);
                viewport.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));

                True(!viewport.HasImage, "new viewport must start without an image");
                True(viewport.BackColor.ToArgb() == Color.Black.ToArgb(),
                    "image viewport background must be pure black");
                int black = Color.Black.ToArgb();
                for (int y = 0; y < rendered.Height; y++)
                {
                    for (int x = 0; x < rendered.Width; x++)
                    {
                        if (rendered.GetPixel(x, y).ToArgb() != black)
                            throw new InvalidOperationException("empty viewport must not draw text, grids, or badges");
                    }
                }
            }
        }

        private static void TestPositioningJobLoadRoi()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Station.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string optionsPath = Path.Combine(directory, "station.json");
                string detectionPath = Path.Combine(directory, "detection-profile.json");
                string firstJobPath = Path.Combine(directory, "positioning-first.nprcal");
                string secondJobPath = Path.Combine(directory, "positioning-second.nprcal");
                StationOptions options = CreateTestOptions(directory, detectionPath, firstJobPath);
                AtomicJsonStore store = new AtomicJsonStore();
                store.Save(optionsPath, options);
                store.Save(detectionPath, new DetectionProfile
                {
                    Roi = new DetectionRoi
                    {
                        ReferenceCenterRow = 123.5,
                        ReferenceCenterColumn = 456.25,
                        PhiDegrees = 12.0,
                        HalfWidth = 80.0,
                        HalfHeight = 40.0
                    }
                });
                SavePositioningJob(firstJobPath, 123.5, 456.25);
                SavePositioningJob(secondJobPath, 700.0, 800.0);

                JobPersistenceResult secondLoaded = JobPackageSerializer.TryLoad(secondJobPath);
                True(secondLoaded.Success, "second positioning job should load for setup");
                secondLoaded.Job.Template.DetectionRoi = RectangleRegion(710.0, 820.0, 90.0, 50.0, 4.0);
                JobPackageSerializer.Save(secondJobPath, secondLoaded.Job);

                using (StationRuntime runtime = CreateRuntime(optionsPath, options))
                using (MainForm form = new MainForm())
                using (MainFormRuntimeBinder binder = new MainFormRuntimeBinder(form, runtime))
                {
                    LoadPositioningJobThroughView(form, binder, firstJobPath);

                    CalibrationJob migrated = runtime.GetPositioningJobSnapshot();
                    True(migrated.Template.DetectionRoi != null,
                        "legacy detection-profile ROI should migrate into the positioning template");
                    Near(123.5, migrated.Template.DetectionRoi.CenterRow, 0.0,
                        "migrated detection ROI row");
                    Near(456.25, migrated.Template.DetectionRoi.CenterColumn, 0.0,
                        "migrated detection ROI column");
                    Near(80.0, migrated.Template.DetectionRoi.Length1, 0.0,
                        "migrated detection ROI half width");

                    JobPersistenceResult persistedFirst = JobPackageSerializer.TryLoad(firstJobPath);
                    True(persistedFirst.Success && persistedFirst.Job.Template.DetectionRoi != null,
                        "migrated ROI should be persisted with the positioning template package");

                    LoadPositioningJobThroughView(form, binder, secondJobPath);
                    CalibrationJob second = runtime.GetPositioningJobSnapshot();
                    Near(710.0, second.Template.DetectionRoi.CenterRow, 0.0,
                        "loaded template must retain its own detection ROI row");
                    Near(820.0, second.Template.DetectionRoi.CenterColumn, 0.0,
                        "loaded template must retain its own detection ROI column");
                    True(form.Controls.Find("nudRoiRow", true).Length == 0 &&
                         form.Controls.Find("btnDrawDetectionRoi", true).Length == 0,
                        "detection page must not expose a second independent ROI editor");
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static StationOptions CreateTestOptions(
            string directory,
            string detectionPath,
            string positioningJobPath)
        {
            return new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                CameraProvider = "Mock",
                CalibrationJobPath = Path.Combine(directory, "calibration.nprcal"),
                PositioningJobPath = positioningJobPath,
                TeachingPath = Path.Combine(directory, "teaching.json"),
                DetectionProfilePath = detectionPath,
                ImageArchiveDirectory = Path.Combine(directory, "images")
            };
        }

        private static int FindAvailableTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static ProtocolEnvelope InspectionTrigger(string messageId)
        {
            ProtocolEnvelope request = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
            request.MessageId = messageId;
            request.Data["currentR"] = 0.0;
            request.Data["expectedCount"] = 1;
            return request;
        }

        private static async Task<ProtocolEnvelope> ExchangeAsync(int port, ProtocolEnvelope request)
        {
            using (TcpClient client = new TcpClient())
            {
                await WithTimeout(client.ConnectAsync(IPAddress.Loopback, port), 3000).ConfigureAwait(false);
                using (NetworkStream stream = client.GetStream())
                {
                    ProtocolJsonCodec codec = new ProtocolJsonCodec();
                    byte[] payload = codec.Serialize(request);
                    byte[] header =
                    {
                        (byte)(payload.Length >> 24),
                        (byte)(payload.Length >> 16),
                        (byte)(payload.Length >> 8),
                        (byte)payload.Length
                    };
                    await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                    await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);

                    byte[] responseHeader = await ReadExactlyAsync(stream, 4).ConfigureAwait(false);
                    int responseLength = (responseHeader[0] << 24) |
                                         (responseHeader[1] << 16) |
                                         (responseHeader[2] << 8) |
                                         responseHeader[3];
                    True(responseLength > 0 && responseLength <= 1024 * 1024,
                        "TCP response frame length must be valid");
                    byte[] responsePayload = await ReadExactlyAsync(stream, responseLength).ConfigureAwait(false);
                    return codec.Deserialize(responsePayload);
                }
            }
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await WithTimeout(
                    stream.ReadAsync(buffer, offset, count - offset),
                    3000).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("TCP peer closed before a complete frame arrived.");
                offset += read;
            }
            return buffer;
        }

        private static StationRuntime CreateRuntime(string optionsPath, StationOptions options)
        {
            ConstructorInfo constructor = typeof(StationRuntime).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(StationOptions) },
                null);
            True(constructor != null, "StationRuntime test constructor must be available");
            return (StationRuntime)constructor.Invoke(new object[] { optionsPath, options });
        }

        private static StationRuntime CreateRuntime(
            string optionsPath,
            StationOptions options,
            ICameraDevice cameraDevice)
        {
            ConstructorInfo constructor = typeof(StationRuntime).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(StationOptions), typeof(ICameraDevice) },
                null);
            True(constructor != null, "StationRuntime injectable camera constructor must be available");
            return (StationRuntime)constructor.Invoke(new object[] { optionsPath, options, cameraDevice });
        }

        private static SemaphoreSlim GetWorkflowGate(StationRuntime runtime)
        {
            FieldInfo field = typeof(StationRuntime).GetField(
                "_workflowGate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(field != null, "StationRuntime workflow gate must be available to the protocol test");
            return (SemaphoreSlim)field.GetValue(runtime);
        }

        private static FrameEventArgs SolidFrame(Color color, string source)
        {
            Bitmap image = new Bitmap(12, 10);
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(color);
            return new FrameEventArgs(image, source);
        }

        private static void InvokeBinder(MainFormRuntimeBinder binder, string methodName, params object[] arguments)
        {
            MethodInfo method = typeof(MainFormRuntimeBinder).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(method != null, "binder method must be available: " + methodName);
            method.Invoke(binder, arguments);
        }

        private static Bitmap GetLockedDisplayImage(MainFormRuntimeBinder binder)
        {
            FieldInfo field = typeof(MainFormRuntimeBinder).GetField(
                "_lockedDisplayImage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(field != null, "binder must retain a stable locked display image");
            return (Bitmap)field.GetValue(binder);
        }

        private static int GetViewportPointOverlayCount(ImageViewport viewport)
        {
            FieldInfo field = typeof(ImageViewport).GetField(
                "_points",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(field != null, "viewport point overlays must be available to the display test");
            System.Collections.ICollection points = field.GetValue(viewport) as System.Collections.ICollection;
            True(points != null, "viewport point overlays must be a collection");
            return points.Count;
        }

        private static int GetViewportContourOverlayCount(ImageViewport viewport)
        {
            FieldInfo field = typeof(ImageViewport).GetField(
                "_contours",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(field != null, "viewport contour overlays must be available to the display test");
            System.Collections.ICollection contours = field.GetValue(viewport) as System.Collections.ICollection;
            True(contours != null, "viewport contour overlays must be a collection");
            return contours.Count;
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
            where T : class
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(field != null, "private field must be available: " + fieldName);
            T value = field.GetValue(instance) as T;
            True(value != null, "private field has the expected type: " + fieldName);
            return value;
        }

        private static void SavePositioningJob(string path, double row, double column)
        {
            CalibrationJob job = new CalibrationJob { Name = "Positioning test" };
            job.Template.ReferenceAnchor = new ImagePoint(row, column);
            job.Template.TemplateRoi = RectangleRegion(row, column, 100.0, 60.0, 0.0);
            JobPackageSerializer.Save(path, job);
        }

        private static RegionDefinition RectangleRegion(
            double row,
            double column,
            double halfWidth,
            double halfHeight,
            double angleDegrees)
        {
            return new RegionDefinition
            {
                Kind = RegionKind.Rectangle2,
                CenterRow = row,
                CenterColumn = column,
                PhiRadians = angleDegrees * Math.PI / 180.0,
                Length1 = halfWidth,
                Length2 = halfHeight
            };
        }

        private static void LoadPositioningJobThroughView(
            MainForm form,
            MainFormRuntimeBinder binder,
            string path)
        {
            System.Windows.Forms.TextBox pathInput = form.Controls
                .Find("txtPositioningTemplatePath", true)
                .OfType<System.Windows.Forms.TextBox>()
                .Single();
            pathInput.Text = path;

            MethodInfo load = typeof(MainFormRuntimeBinder).GetMethod(
                "LoadTemplateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True(load != null, "positioning template load action must be available");
            Task task = (Task)load.Invoke(binder, new object[] { true, CancellationToken.None });
            task.GetAwaiter().GetResult();
        }

        private static double ReadNumericControl(MainForm form, string name)
        {
            System.Windows.Forms.NumericUpDown input = form.Controls
                .Find(name, true)
                .OfType<System.Windows.Forms.NumericUpDown>()
                .Single();
            return Convert.ToDouble(input.Value);
        }

        private static TemplateMatchResult SuccessfulMatch(double row, double column, double angle)
        {
            return new TemplateMatchResult
            {
                Success = true,
                ErrorCode = CalibrationErrorCode.None,
                Anchor = new ImagePoint(row, column),
                ModelPose = new ImagePose(row, column, angle),
                Score = 0.95
            };
        }

        private static TeachingPose ValidTeaching(
            TeachingSide side,
            double row,
            double column,
            double imageAngle,
            double machineX,
            double machineY,
            double machineR)
        {
            return new TeachingPose
            {
                Side = side,
                IsValid = true,
                ImageRow = row,
                ImageColumn = column,
                ImageAngleDegrees = imageAngle,
                MachineX = machineX,
                MachineY = machineY,
                MachineR = machineR,
                CapturedAtUtc = DateTime.UtcNow
            };
        }

        private static void AssertBoardPosition(
            BoardInspectionItem item,
            int expectedIndex,
            double expectedRow,
            double expectedColumn)
        {
            True(item.BoardIndex == expectedIndex, "boardIndex must follow spatial row-major order");
            Near(expectedRow, item.ImageRow, 0.0, "ordered board row " + expectedIndex);
            Near(expectedColumn, item.ImageColumn, 0.0, "ordered board column " + expectedIndex);
        }

        private static ProtocolEnvelope Arrival(
            string correlationId,
            int pointNumber,
            double x,
            double y,
            double r)
        {
            ProtocolEnvelope arrival = ProtocolEnvelope.Create(ProtocolMessageTypes.MotionArrived);
            arrival.CorrelationId = correlationId;
            arrival.Data["pointNumber"] = pointNumber;
            arrival.Data["x"] = x;
            arrival.Data["y"] = y;
            arrival.Data["r"] = r;
            return arrival;
        }

        private static TaskCompletionSource<T> NewCompletion<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int milliseconds)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
            if (completed != task) throw new TimeoutException("Test operation timed out.");
            return await task.ConfigureAwait(false);
        }

        private static async Task WithTimeout(Task task, int milliseconds)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
            if (completed != task) throw new TimeoutException("Test operation timed out.");
            await task.ConfigureAwait(false);
        }

        private static async Task ExpectCancelled(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            throw new InvalidOperationException("Expected the operation to be cancelled.");
        }

        private static void Run(string name, Action test)
        {
            string skip = Environment.GetEnvironmentVariable("NPRCAL_SKIP");
            if (!string.IsNullOrWhiteSpace(skip) && skip.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => name.IndexOf(token.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Console.WriteLine("[SKIP] " + name);
                return;
            }
            try
            {
                test();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + ": " + exception);
            }
        }

        private static void RunAsync(string name, Func<Task> test)
        {
            Run(name, () => test().GetAwaiter().GetResult());
        }

        private static void Near(double expected, double actual, double tolerance, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}: expected {1:G17}, got {2:G17}", message, expected, actual));
            }
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class ControllableCameraDevice : ICameraDevice
        {
            private int _captureAttempts;
            private bool _connected;

            public ControllableCameraDevice()
            {
                EnumerationStarted = NewCompletion<bool>();
            }

            public bool BlockEnumeration { get; set; }
            public int CaptureFailuresBeforeSuccess { get; set; }
            public bool EnumerationCancellationObserved { get; private set; }
            public TaskCompletionSource<bool> EnumerationStarted { get; private set; }
            public string ProviderName { get { return "Test Camera"; } }
            public bool IsConnected { get { return _connected; } }
            public string ConnectedDeviceId { get { return _connected ? "TEST-CAMERA" : null; } }

            public async Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(
                CancellationToken cancellationToken)
            {
                EnumerationStarted.TrySetResult(true);
                if (BlockEnumeration)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        EnumerationCancellationObserved = true;
                        throw;
                    }
                }
                return new List<CameraDescriptor>
                {
                    new CameraDescriptor { Id = "TEST-CAMERA", DisplayName = "Test Camera" }
                }.AsReadOnly();
            }

            public Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _connected = true;
                return Task.CompletedTask;
            }

            public Task DisconnectAsync()
            {
                _connected = false;
                return Task.CompletedTask;
            }

            public Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int attempt = Interlocked.Increment(ref _captureAttempts);
                if (attempt <= CaptureFailuresBeforeSuccess)
                    throw new IOException("Scripted preview failure " + attempt + ".");

                Bitmap image = new Bitmap(8, 6);
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.DarkGreen);
                return Task.FromResult(new CameraFrame(image, attempt, DateTime.UtcNow));
            }

            public void Dispose()
            {
                _connected = false;
            }
        }
    }
}
