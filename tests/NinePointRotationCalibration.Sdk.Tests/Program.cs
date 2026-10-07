using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using NinePointRotationCalibration;
using NinePointRotationCalibration.Halcon;

namespace NinePointRotationCalibration.Sdk.Tests
{
    internal static class Program
    {
        private static readonly byte[] InitialModel = { 0x48, 0x41, 0x4c, 0x43, 0x4f, 0x4e, 0x00, 0x01, 0x7f, 0xff };
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Run("Complete package round-trip", CompletePackageRoundTrip);
            Run("Repeated save replaces package", RepeatedSaveReplacesPackage);
            Run("Corrupted model hash is rejected", CorruptedModelHashIsRejected);
            Run("Missing model hash is rejected", MissingModelHashIsRejected);
            Run("TrySave and TryLoad return structured errors", TryMethodsReturnStructuredErrors);
            Run("SDK Locate success and failure", SdkLocateSuccessAndFailure);
            Run("SDK CaptureSample success mapping", SdkCaptureSampleSuccessMapping);
            Run("SDK CaptureSample failure is immutable", SdkCaptureSampleFailureIsImmutable);
            Run("SDK AddSampleFromMatch success and failure", SdkAddSampleFromMatchSuccessAndFailure);
            Run("Simple SDK translation and rotation workflow", SimpleSdkTranslationAndRotationWorkflow);
            Run("SDK does not dispose injected engine", SdkDoesNotDisposeInjectedEngine);

            Console.WriteLine();
            Console.WriteLine("Passed: {0}; Failed: {1}", _passed, _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action<string> test)
        {
            string testDirectory = Path.Combine(
                Path.GetTempPath(),
                "NinePointRotationCalibration.Sdk.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            try
            {
                test(testDirectory);
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + ": " + exception.Message);
            }
            finally
            {
                try { Directory.Delete(testDirectory, true); } catch { }
            }
        }

        private static void CompletePackageRoundTrip(string directory)
        {
            string path = Path.Combine(directory, "nested", "complete.nprcal");
            CalibrationJob source = CreateCompleteJob("Complete job", InitialModel);

            JobPackageSerializer.Save(path, source);
            True(File.Exists(path), "package file was not created");
            AssertPackageEntries(path, InitialModel);

            CalibrationJob loaded = JobPackageSerializer.Load(path);
            True(!ReferenceEquals(source, loaded), "Load must create a new job instance");
            Equal(source.SchemaVersion, loaded.SchemaVersion, "schema version");
            Equal(source.JobId, loaded.JobId, "job id");
            Equal(source.Name, loaded.Name, "job name");
            Equal(source.LinearUnit, loaded.LinearUnit, "linear unit");
            Equal(source.CustomLinearUnitName, loaded.CustomLinearUnitName, "custom unit name");
            Equal(source.RotationConvention, loaded.RotationConvention, "rotation convention");

            TemplateDefinition expected = source.Template;
            TemplateDefinition actual = loaded.Template;
            True(actual != null, "template was not restored");
            Equal(expected.ModelType, actual.ModelType, "model type");
            BytesEqual(InitialModel, actual.ModelData, "model bytes");
            Equal(expected.ModelHash, actual.ModelHash, "model hash");
            Equal("HALCON 19.11", actual.HalconVersion, "HALCON version");
            Equal(expected.Revision, actual.Revision, "template revision");
            Equal(false, actual.IsModelDirty, "model dirty state");
            Equal(true, actual.IsReferenceAnchorLocked, "anchor lock");
            Near(205.25, actual.ReferenceAnchor.Row, 1e-12, "anchor row");
            Near(310.75, actual.ReferenceAnchor.Column, 1e-12, "anchor column");
            Near(204.5, actual.DomainCenter.Row, 1e-12, "domain center row");
            Near(309.5, actual.DomainCenter.Column, 1e-12, "domain center column");
            Equal(1280, actual.ReferenceImageWidth, "reference width");
            Equal(1024, actual.ReferenceImageHeight, "reference height");

            AssertRegion(expected.TemplateRoi, actual.TemplateRoi, "template ROI");
            AssertRegion(expected.SearchRegion, actual.SearchRegion, "search ROI");
            Equal(2, actual.MaskRegions.Count, "mask count");
            Equal(MaskOperation.Exclude, actual.MaskRegions[0].Operation, "mask operation 0");
            AssertRegion(expected.MaskRegions[0].Region, actual.MaskRegions[0].Region, "mask region 0");
            Equal(MaskOperation.Include, actual.MaskRegions[1].Operation, "mask operation 1");
            AssertRegion(expected.MaskRegions[1].Region, actual.MaskRegions[1].Region, "mask region 1");
            Equal(1, actual.EraseStrokes.Count, "erase stroke count");
            Near(6.5, actual.EraseStrokes[0].Radius, 1e-12, "erase radius");
            Equal(3, actual.EraseStrokes[0].Points.Count, "erase point count");
            Near(102.0, actual.EraseStrokes[0].Points[2].Column, 1e-12, "erase point column");

            Equal(4, actual.ShapeParameters.NumLevels, "shape levels");
            Near(-0.7, actual.ShapeParameters.AngleStartRad, 1e-12, "shape angle start");
            Near(1.4, actual.ShapeParameters.AngleExtentRad, 1e-12, "shape angle extent");
            Equal("point_reduction_high", actual.ShapeParameters.Optimization, "shape optimization");
            Equal("ignore_global_polarity", actual.ShapeParameters.Metric, "shape metric");
            Equal(42, actual.ShapeParameters.Contrast, "shape contrast");
            Equal(9, actual.ShapeParameters.MinContrast, "shape minimum contrast");
            Near(0.61, actual.MatchParameters.MinScore, 1e-12, "minimum score");
            Equal(2, actual.MatchParameters.NumMatches, "match count");
            Equal("least_squares_high", actual.MatchParameters.SubPixel, "sub-pixel mode");
            Equal(750, actual.MatchParameters.TimeoutMilliseconds, "timeout");
            Near(4.5, actual.MatchParameters.ContourPointSpacingPixels, 1e-12, "contour point spacing");

            Equal(7, loaded.SolverOptions.MinimumTranslationInliers, "minimum translation inliers");
            Near(2.25, loaded.SolverOptions.RansacInlierThresholdPixels, 1e-12, "RANSAC threshold");
            Equal(true, loaded.SolverOptions.RequireRotationCalibration, "rotation requirement");
            Equal(2, loaded.Samples.Count, "sample count");
            AssertSample(source.Samples[0], loaded.Samples[0], "translation sample");
            AssertSample(source.Samples[1], loaded.Samples[1], "rotation sample");

            actual.ModelData[0] ^= 0xff;
            True(source.Template.ModelData[0] == InitialModel[0], "loaded model must not alias source model bytes");
        }

        private static void RepeatedSaveReplacesPackage(string directory)
        {
            string path = Path.Combine(directory, "replace.nprcal");
            CalibrationJob first = CreateCompleteJob("First revision", InitialModel);
            JobPackageSerializer.Save(path, first);

            byte[] replacementModel = { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
            CalibrationJob second = CreateCompleteJob("Second revision", replacementModel);
            second.JobId = first.JobId;
            second.Template.Revision = first.Template.Revision + 1;
            second.Samples[0].Tag = "replacement sample";
            JobPackageSerializer.Save(path, second);

            CalibrationJob loaded = JobPackageSerializer.Load(path);
            Equal("Second revision", loaded.Name, "overwritten job name");
            Equal(second.Template.Revision, loaded.Template.Revision, "overwritten template revision");
            Equal("replacement sample", loaded.Samples[0].Tag, "overwritten sample");
            BytesEqual(replacementModel, loaded.Template.ModelData, "overwritten model");
            Equal(second.Template.ModelHash, loaded.Template.ModelHash, "overwritten hash");
            AssertPackageEntries(path, replacementModel);

            True(!File.Exists(path + ".bak"), "successful replacement should not leave a backup file");
            string[] temporaryFiles = Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories);
            Equal(0, temporaryFiles.Length, "successful replacement should not leave temporary files");
        }

        private static void CorruptedModelHashIsRejected(string directory)
        {
            string path = Path.Combine(directory, "corrupt.nprcal");
            CalibrationJob job = CreateCompleteJob("Corruption test", InitialModel);
            JobPackageSerializer.Save(path, job);

            byte[] damagedModel = (byte[])InitialModel.Clone();
            damagedModel[damagedModel.Length - 1] ^= 0x5a;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
            {
                ZipArchiveEntry modelEntry = archive.GetEntry("model.bin");
                True(modelEntry != null, "model.bin is missing before corruption");
                modelEntry.Delete();
                ZipArchiveEntry replacement = archive.CreateEntry("model.bin", CompressionLevel.NoCompression);
                using (Stream output = replacement.Open())
                    output.Write(damagedModel, 0, damagedModel.Length);
            }

            bool threw = false;
            try
            {
                JobPackageSerializer.Load(path);
            }
            catch (InvalidDataException exception)
            {
                threw = true;
                Contains(exception.Message, "哈希", "direct Load hash error message");
            }
            True(threw, "Load must reject a model whose SHA-256 does not match metadata");

            JobPersistenceResult result = JobPackageSerializer.TryLoad(path);
            True(!result.Success, "TryLoad must report corrupted model failure");
            Equal(CalibrationErrorCode.SerializationError, result.ErrorCode, "corrupt model error code");
            True(result.Job == null, "failed TryLoad must not expose a partially loaded job");
            Equal(path, result.Path, "corrupt model path");
            Contains(result.Message, "哈希", "TryLoad hash error message");
        }

        private static void MissingModelHashIsRejected(string directory)
        {
            string path = Path.Combine(directory, "missing-hash.nprcal");
            CalibrationJob job = CreateCompleteJob("Missing hash test", InitialModel);
            JobPackageSerializer.Save(path, job);

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
            {
                ZipArchiveEntry metadataEntry = archive.GetEntry("job.json");
                True(metadataEntry != null, "job.json is missing before metadata tampering");
                string json;
                using (Stream input = metadataEntry.Open())
                using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true))
                {
                    json = reader.ReadToEnd();
                }

                int hashIndex = json.IndexOf(job.Template.ModelHash, StringComparison.Ordinal);
                True(hashIndex >= 0, "model hash was not found in job metadata");
                json = json.Remove(hashIndex, job.Template.ModelHash.Length);
                metadataEntry.Delete();
                ZipArchiveEntry replacement = archive.CreateEntry("job.json", CompressionLevel.Optimal);
                using (Stream output = replacement.Open())
                using (StreamWriter writer = new StreamWriter(output, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                }
            }

            bool threw = false;
            try
            {
                JobPackageSerializer.Load(path);
            }
            catch (InvalidDataException exception)
            {
                threw = true;
                Contains(exception.Message, "哈希", "missing hash error message");
            }

            True(threw, "Load must reject model data without a declared SHA-256 hash");
        }

        private static void TryMethodsReturnStructuredErrors(string directory)
        {
            string savePath = Path.Combine(directory, "try-save.nprcal");
            CalibrationJob job = CreateCompleteJob("Try methods", InitialModel);

            JobPersistenceResult saved = JobPackageSerializer.TrySave(savePath, job);
            True(saved.Success, "valid TrySave should succeed: " + saved.Message);
            Equal(CalibrationErrorCode.None, saved.ErrorCode, "successful TrySave error code");
            Equal(Path.GetFullPath(savePath), saved.Path, "successful TrySave path");
            True(saved.Job != null && !ReferenceEquals(saved.Job, job), "successful TrySave should return a job snapshot");
            BytesEqual(job.Template.ModelData, saved.Job.Template.ModelData, "TrySave job snapshot model");

            JobPersistenceResult saveFailure = JobPackageSerializer.TrySave(savePath, null);
            True(!saveFailure.Success, "TrySave with null job must fail");
            Equal(CalibrationErrorCode.SerializationError, saveFailure.ErrorCode, "TrySave failure error code");
            Equal(savePath, saveFailure.Path, "TrySave failure path");
            True(saveFailure.Job == null, "TrySave failure job");
            Contains(saveFailure.Message, "Job 保存失败", "TrySave failure message");

            string missingPath = Path.Combine(directory, "missing.nprcal");
            JobPersistenceResult loadFailure = JobPackageSerializer.TryLoad(missingPath);
            True(!loadFailure.Success, "TryLoad with missing package must fail");
            Equal(CalibrationErrorCode.SerializationError, loadFailure.ErrorCode, "TryLoad failure error code");
            Equal(missingPath, loadFailure.Path, "TryLoad failure path");
            True(loadFailure.Job == null, "TryLoad failure job");
            Contains(loadFailure.Message, "Job 加载失败", "TryLoad failure message");

            JobPersistenceResult nullPathFailure = JobPackageSerializer.TryLoad(null);
            True(!nullPathFailure.Success, "TryLoad with null path must fail");
            Equal(CalibrationErrorCode.SerializationError, nullPathFailure.ErrorCode, "null path error code");
            True(nullPathFailure.Path == null, "null input path should be preserved in result");
            True(nullPathFailure.Job == null, "null path failure job");
        }

        private static void SdkLocateSuccessAndFailure(string directory)
        {
            CalibrationJob job = CreateCompleteJob("Locate API", InitialModel);
            int originalSampleCount = job.Samples.Count;
            long originalRevision = job.Template.Revision;
            ImagePoint originalAnchor = job.Template.ReferenceAnchor;
            TemplateMatchResult expectedSuccess = CreateSuccessfulMatch();
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = expectedSuccess,
                MutateLocatedTemplate = true
            };

            using (Bitmap image = new Bitmap(8, 6))
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                TemplateMatchResult actual = sdk.Locate(image, job, true);
                True(ReferenceEquals(expectedSuccess, actual), "Locate should forward the engine result");
                Equal(1, engine.LocateCallCount, "Locate engine call count");
                True(ReferenceEquals(image, engine.LastLocateImage), "Locate image forwarding");
                Equal(true, engine.LastIncludeContour, "Locate overlay forwarding");
                True(engine.LastDefinition != null && !ReferenceEquals(job.Template, engine.LastDefinition),
                    "Locate must pass a cloned template to the engine");
                Equal(originalRevision, job.Template.Revision, "engine mutation must not change input template revision");
                Near(originalAnchor.Row, job.Template.ReferenceAnchor.Row, 0.0, "engine mutation must not change input anchor row");
                Near(originalAnchor.Column, job.Template.ReferenceAnchor.Column, 0.0, "engine mutation must not change input anchor column");
                Equal(InitialModel[0], job.Template.ModelData[0], "engine mutation must not change input model bytes");
                Equal(originalSampleCount, job.Samples.Count, "Locate must not change input samples");

                engine.MutateLocatedTemplate = false;
                engine.LocateResult = TemplateMatchResult.Failure(
                    CalibrationErrorCode.TemplateMatchFailed,
                    "synthetic locate failure");
                TemplateMatchResult failure = sdk.Locate(image, job, false);
                True(!failure.Success, "Locate failure should be returned");
                Equal(CalibrationErrorCode.TemplateMatchFailed, failure.ErrorCode, "Locate failure code");
                Equal("synthetic locate failure", failure.Message, "Locate failure message");
                Equal(2, engine.LocateCallCount, "Locate failure engine call count");
                Equal(false, engine.LastIncludeContour, "Locate failure overlay forwarding");
                Equal(originalSampleCount, job.Samples.Count, "failed Locate must not change input samples");
            }

            Equal(0, engine.DisposeCallCount, "using SDK must not dispose an injected engine");
        }

        private static void SdkCaptureSampleSuccessMapping(string directory)
        {
            CalibrationJob job = CreateCompleteJob("Capture success", InitialModel);
            int originalSampleCount = job.Samples.Count;
            DateTime originalUpdatedAt = job.UpdatedAtUtc;
            long expectedRevision = job.Template.Revision;
            TemplateMatchResult match = CreateSuccessfulMatch();
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine { LocateResult = match };

            SampleCaptureResult result;
            using (Bitmap image = new Bitmap(5, 5))
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                result = sdk.CaptureSample(
                    job,
                    image,
                    12.25,
                    -8.5,
                    27.75,
                    "rotation-capture",
                    CalibrationSampleKind.Rotation);
            }

            True(result.Success, result.Message);
            Equal(CalibrationErrorCode.None, result.ErrorCode, "CaptureSample success error code");
            True(result.Job != null && !ReferenceEquals(job, result.Job), "CaptureSample must return a cloned job");
            Equal(originalSampleCount, job.Samples.Count, "CaptureSample must not change input sample count");
            Equal(originalUpdatedAt, job.UpdatedAtUtc, "CaptureSample must not change input timestamp");
            Equal(originalSampleCount + 1, result.Job.Samples.Count, "CaptureSample output sample count");

            CalibrationSample appended = result.Job.Samples[result.Job.Samples.Count - 1];
            True(result.Sample != null && !ReferenceEquals(appended, result.Sample), "CaptureSample result sample must be a snapshot");
            Equal(CalibrationSampleKind.Rotation, appended.Kind, "captured sample kind");
            Equal(expectedRevision, appended.TemplateRevision, "captured sample template revision");
            Equal("rotation-capture", appended.Tag, "captured sample tag");
            Equal(true, appended.Enabled, "captured sample enabled state");
            Near(12.25, appended.MachinePose.X, 0.0, "captured machine X");
            Near(-8.5, appended.MachinePose.Y, 0.0, "captured machine Y");
            Near(27.75, appended.MachinePose.ThetaDegrees, 0.0, "captured machine angle");
            Near(match.Anchor.Row, appended.ImagePose.Row, 0.0, "captured anchor row");
            Near(match.Anchor.Column, appended.ImagePose.Column, 0.0, "captured anchor column");
            Near(match.ModelPose.AngleDegrees, appended.ImagePose.AngleDegrees, 0.0, "captured model angle");
            Near(match.Score, appended.MatchScore, 0.0, "captured match score");
            True(result.Match != null && !ReferenceEquals(match, result.Match), "CaptureSample must snapshot the match result");
            Near(match.Anchor.Row, result.Match.Anchor.Row, 0.0, "captured match snapshot anchor");
            Equal(1, engine.LocateCallCount, "CaptureSample Locate call count");
            Equal(false, engine.LastIncludeContour, "CaptureSample should not request an overlay");
            Equal(0, engine.DisposeCallCount, "CaptureSample SDK must not dispose injected engine");
        }

        private static void SdkCaptureSampleFailureIsImmutable(string directory)
        {
            CalibrationJob job = CreateCompleteJob("Capture failure", InitialModel);
            int originalSampleCount = job.Samples.Count;
            DateTime originalUpdatedAt = job.UpdatedAtUtc;
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = TemplateMatchResult.Failure(
                    CalibrationErrorCode.MatchScoreTooLow,
                    "score below threshold")
            };

            SampleCaptureResult result;
            using (Bitmap image = new Bitmap(4, 4))
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                result = sdk.CaptureSample(job, image, 1.0, 2.0, 3.0, "must-not-add");
            }

            True(!result.Success, "CaptureSample should preserve Locate failure");
            Equal(CalibrationErrorCode.MatchScoreTooLow, result.ErrorCode, "CaptureSample failure code");
            Equal("score below threshold", result.Message, "CaptureSample failure message");
            True(result.Sample == null, "failed CaptureSample must not return a sample");
            True(result.Job != null && !ReferenceEquals(job, result.Job), "failed CaptureSample should return a job snapshot");
            Equal(originalSampleCount, result.Job.Samples.Count, "failed CaptureSample output sample count");
            Equal(originalSampleCount, job.Samples.Count, "failed CaptureSample input sample count");
            Equal(originalUpdatedAt, job.UpdatedAtUtc, "failed CaptureSample input timestamp");
            True(result.Match != null && !result.Match.Success, "failed CaptureSample should snapshot failed match");
            Equal(1, engine.LocateCallCount, "failed CaptureSample Locate call count");
        }

        private static void SdkAddSampleFromMatchSuccessAndFailure(string directory)
        {
            CalibrationJob job = CreateCompleteJob("Add from match", InitialModel);
            int originalSampleCount = job.Samples.Count;
            DateTime originalUpdatedAt = job.UpdatedAtUtc;
            TemplateMatchResult match = CreateSuccessfulMatch();
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine();

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                SampleCaptureResult success = sdk.AddSampleFromMatch(
                    job,
                    match,
                    -4.25,
                    9.75,
                    -12.5,
                    "prelocated",
                    CalibrationSampleKind.Translation);

                True(success.Success, success.Message);
                Equal(originalSampleCount + 1, success.Job.Samples.Count, "AddSampleFromMatch output count");
                Equal(originalSampleCount, job.Samples.Count, "AddSampleFromMatch input count");
                Equal(originalUpdatedAt, job.UpdatedAtUtc, "AddSampleFromMatch input timestamp");
                CalibrationSample sample = success.Job.Samples[success.Job.Samples.Count - 1];
                Equal(CalibrationSampleKind.Translation, sample.Kind, "AddSampleFromMatch sample kind");
                Equal(job.Template.Revision, sample.TemplateRevision, "AddSampleFromMatch sample revision");
                Near(match.Anchor.Row, sample.ImagePose.Row, 0.0, "AddSampleFromMatch anchor row");
                Near(match.Anchor.Column, sample.ImagePose.Column, 0.0, "AddSampleFromMatch anchor column");
                Near(match.AngleDegrees, sample.ImagePose.AngleDegrees, 0.0, "AddSampleFromMatch angle");
                Equal("prelocated", sample.Tag, "AddSampleFromMatch tag");

                TemplateMatchResult failedMatch = TemplateMatchResult.Failure(
                    CalibrationErrorCode.TemplateMatchFailed,
                    "prelocated failure");
                SampleCaptureResult failure = sdk.AddSampleFromMatch(
                    job,
                    failedMatch,
                    0.0,
                    0.0,
                    0.0,
                    "must-not-add",
                    CalibrationSampleKind.Rotation);
                True(!failure.Success, "AddSampleFromMatch should reject a failed match");
                Equal(CalibrationErrorCode.TemplateMatchFailed, failure.ErrorCode, "AddSampleFromMatch failure code");
                True(failure.Sample == null, "failed AddSampleFromMatch must not return a sample");
                Equal(originalSampleCount, failure.Job.Samples.Count, "failed AddSampleFromMatch job snapshot count");
                Equal(originalSampleCount, job.Samples.Count, "failed AddSampleFromMatch input count");

                SampleCaptureResult invalidKind = sdk.AddSampleFromMatch(
                    job,
                    match,
                    0.0,
                    0.0,
                    0.0,
                    "invalid-kind",
                    (CalibrationSampleKind)12345);
                True(!invalidKind.Success, "AddSampleFromMatch should reject an unknown sample kind");
                Equal(CalibrationErrorCode.ParameterInvalid, invalidKind.ErrorCode, "unknown sample kind error code");
                Equal(originalSampleCount, invalidKind.Job.Samples.Count, "unknown sample kind must not add a sample");
            }

            Equal(0, engine.LocateCallCount, "AddSampleFromMatch must not call the matching engine");
        }

        private static void SdkDoesNotDisposeInjectedEngine(string directory)
        {
            CalibrationJob job = CreateCompleteJob("Dispose ownership", InitialModel);
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = CreateSuccessfulMatch()
            };
            NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine);
            sdk.Dispose();
            sdk.Dispose();
            Equal(0, engine.DisposeCallCount, "externally injected engine must remain caller-owned");

            bool threw = false;
            using (Bitmap image = new Bitmap(2, 2))
            {
                try
                {
                    sdk.Locate(image, job);
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }
            }
            True(threw, "disposed SDK should reject subsequent calls");

            engine.ClearCache();
            Equal(1, engine.ClearCacheCallCount, "injected engine should remain usable after SDK disposal");
            engine.Dispose();
            Equal(1, engine.DisposeCallCount, "caller should retain the ability to dispose injected engine");
        }

        private static void SimpleSdkTranslationAndRotationWorkflow(string directory)
        {
            CalibrationJob original = CreateCompleteJob("Simple facade", InitialModel);
            int originalCount = original.Samples.Count;
            FakeTemplateMatchingEngine engine = new FakeTemplateMatchingEngine
            {
                LocateResult = CreateSuccessfulMatch()
            };

            CalibrationJob translation;
            CalibrationJob rotation;
            using (Bitmap image = new Bitmap(5, 5))
            using (CalibrationSdk sdk = new CalibrationSdk(engine))
            {
                translation = sdk.AddSample(original, image, 10.0, 20.0);
                rotation = sdk.AddRotationSample(translation, image, 10.5, 20.5, 30.0);
            }

            Equal(originalCount, original.Samples.Count, "simple SDK must not mutate input Job");
            Equal(originalCount + 1, translation.Samples.Count, "simple translation count");
            Equal(originalCount + 2, rotation.Samples.Count, "simple rotation count");
            CalibrationSample translationSample = translation.Samples[translation.Samples.Count - 1];
            CalibrationSample rotationSample = rotation.Samples[rotation.Samples.Count - 1];
            Equal(CalibrationSampleKind.Translation, translationSample.Kind, "simple translation kind");
            Equal("P2", translationSample.Tag, "simple translation tag");
            Equal(CalibrationSampleKind.Rotation, rotationSample.Kind, "simple rotation kind");
            Equal("R2", rotationSample.Tag, "simple rotation tag");
            Near(30.0, rotationSample.MachinePose.ThetaDegrees, 0.0, "simple rotation angle");
            Equal(true, rotation.SolverOptions.RequireRotationCalibration, "simple rotation requirement");
            Equal(2, engine.LocateCallCount, "simple SDK locate count");
        }

        private static TemplateMatchResult CreateSuccessfulMatch()
        {
            return new TemplateMatchResult
            {
                Success = true,
                ErrorCode = CalibrationErrorCode.None,
                Message = "synthetic match",
                Anchor = new ImagePoint(123.5, 456.75),
                ModelPose = new ImagePose(119.25, 451.5, -37.125),
                Score = 0.936,
                ElapsedMilliseconds = 2.5,
                Contour = new List<ImagePoint>
                {
                    new ImagePoint(120, 450),
                    new ImagePoint(121, 451)
                },
                ContourSegments = new List<List<ImagePoint>>
                {
                    new List<ImagePoint>
                    {
                        new ImagePoint(120, 450),
                        new ImagePoint(121, 451)
                    }
                }
            };
        }

        private static CalibrationJob CreateCompleteJob(string name, byte[] modelData)
        {
            CalibrationJob job = new CalibrationJob
            {
                JobId = "job-serialization-self-test",
                Name = name,
                LinearUnit = LinearUnit.Custom,
                CustomLinearUnitName = "stage-unit",
                RotationConvention = RotationConvention.WorkpieceRotates,
                CreatedAtUtc = new DateTime(2026, 10, 7, 1, 2, 3, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 10, 7, 4, 5, 6, DateTimeKind.Utc)
            };

            job.Template.ModelType = TemplateModelType.Shape;
            job.Template.TemplateRoi = new RegionDefinition
            {
                Kind = RegionKind.Rectangle2,
                CenterRow = 204.5,
                CenterColumn = 309.5,
                PhiRadians = 0.125,
                Length1 = 80.25,
                Length2 = 45.75
            };
            job.Template.SearchRegion = new RegionDefinition
            {
                Kind = RegionKind.Polygon,
                CenterRow = 250,
                CenterColumn = 350,
                Points = new List<ImagePoint>
                {
                    new ImagePoint(10, 20),
                    new ImagePoint(10, 700),
                    new ImagePoint(600, 710),
                    new ImagePoint(620, 15)
                }
            };
            job.Template.MaskRegions.Add(new TemplateMaskShape
            {
                Operation = MaskOperation.Exclude,
                Region = new RegionDefinition
                {
                    Kind = RegionKind.Circle,
                    CenterRow = 201.25,
                    CenterColumn = 305.5,
                    Radius = 12.75
                }
            });
            job.Template.MaskRegions.Add(new TemplateMaskShape
            {
                Operation = MaskOperation.Include,
                Region = new RegionDefinition
                {
                    Kind = RegionKind.Freehand,
                    CenterRow = 210,
                    CenterColumn = 315,
                    Points = new List<ImagePoint>
                    {
                        new ImagePoint(200, 300),
                        new ImagePoint(204, 318),
                        new ImagePoint(222, 312)
                    }
                }
            });
            job.Template.EraseStrokes.Add(new TemplateEraseStroke
            {
                Radius = 6.5,
                Points = new List<ImagePoint>
                {
                    new ImagePoint(90, 100),
                    new ImagePoint(91, 101),
                    new ImagePoint(92, 102)
                }
            });
            job.Template.ShapeParameters.NumLevels = 4;
            job.Template.ShapeParameters.AngleStartRad = -0.7;
            job.Template.ShapeParameters.AngleExtentRad = 1.4;
            job.Template.ShapeParameters.AngleStepRad = 0.015;
            job.Template.ShapeParameters.Optimization = "point_reduction_high";
            job.Template.ShapeParameters.Metric = "ignore_global_polarity";
            job.Template.ShapeParameters.Contrast = 42;
            job.Template.ShapeParameters.MinContrast = 9;
            job.Template.NccParameters.NumLevels = 3;
            job.Template.NccParameters.AngleStartRad = -0.4;
            job.Template.NccParameters.AngleExtentRad = 0.8;
            job.Template.MatchParameters.AngleStartRad = -0.6;
            job.Template.MatchParameters.AngleExtentRad = 1.2;
            job.Template.MatchParameters.MinScore = 0.61;
            job.Template.MatchParameters.NumMatches = 2;
            job.Template.MatchParameters.MaxOverlap = 0.35;
            job.Template.MatchParameters.SubPixel = "least_squares_high";
            job.Template.MatchParameters.NumLevels = 3;
            job.Template.MatchParameters.Greediness = 0.82;
            job.Template.MatchParameters.TimeoutMilliseconds = 750;
            job.Template.MatchParameters.ContourPointSpacingPixels = 4.5;
            job.Template.ReferenceAnchor = new ImagePoint(205.25, 310.75);
            job.Template.IsReferenceAnchorLocked = true;
            job.Template.DomainCenter = new ImagePoint(204.5, 309.5);
            job.Template.ModelReference = new ImagePose(204.5, 309.5, 7.25);
            job.Template.ReferenceImageWidth = 1280;
            job.Template.ReferenceImageHeight = 1024;
            job.Template.Revision = 17;
            job.Template.SetModelData(modelData, "HALCON 19.11");

            job.SolverOptions.MinimumTranslationSamples = 9;
            job.SolverOptions.MinimumTranslationInliers = 7;
            job.SolverOptions.MinimumAxisTravel = 1.25;
            job.SolverOptions.MinimumMatchScore = 0.61;
            job.SolverOptions.EnableRansac = true;
            job.SolverOptions.RansacIterations = 321;
            job.SolverOptions.RansacInlierThresholdPixels = 2.25;
            job.SolverOptions.MadMultiplier = 4.25;
            job.SolverOptions.MaxRmsResidualPixels = 1.2;
            job.SolverOptions.MaxResidualPixels = 3.4;
            job.SolverOptions.RequireRotationCalibration = true;
            job.SolverOptions.MinimumRotationSamples = 4;
            job.SolverOptions.MinimumRotationInliers = 3;
            job.SolverOptions.MinimumRotationSpanDegrees = 35;
            job.SolverOptions.RandomSeed = 8675309;

            job.AddSample(new CalibrationSample
            {
                SampleId = "translation-1",
                Kind = CalibrationSampleKind.Translation,
                MachinePose = new MachinePose(12.5, -3.75, 0),
                ImagePose = new ImagePose(222.125, 345.875, -1.5),
                MatchScore = 0.987,
                Enabled = true,
                Tag = "grid-center",
                CapturedAtUtc = new DateTime(2026, 10, 7, 6, 0, 0, DateTimeKind.Utc)
            });
            job.AddSample(new CalibrationSample
            {
                SampleId = "rotation-1",
                Kind = CalibrationSampleKind.Rotation,
                MachinePose = new MachinePose(12.5, -3.75, 30),
                ImagePose = new ImagePose(229.25, 352.5, -28.75),
                MatchScore = 0.934,
                Enabled = false,
                Tag = "theta-plus-30",
                CapturedAtUtc = new DateTime(2026, 10, 7, 6, 1, 0, DateTimeKind.Utc)
            });
            job.UpdatedAtUtc = new DateTime(2026, 10, 7, 4, 5, 6, DateTimeKind.Utc);
            return job;
        }

        private static void AssertPackageEntries(string path, byte[] expectedModel)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry jobEntry = archive.GetEntry("job.json");
                ZipArchiveEntry modelEntry = archive.GetEntry("model.bin");
                True(jobEntry != null && jobEntry.Length > 0, "package must contain non-empty job.json");
                True(modelEntry != null, "package must contain model.bin");
                using (Stream input = modelEntry.Open())
                using (MemoryStream buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    BytesEqual(expectedModel, buffer.ToArray(), "model.bin bytes");
                }
            }
        }

        private static void AssertRegion(RegionDefinition expected, RegionDefinition actual, string name)
        {
            True(actual != null, name + " is null");
            Equal(expected.Kind, actual.Kind, name + " kind");
            Near(expected.CenterRow, actual.CenterRow, 1e-12, name + " center row");
            Near(expected.CenterColumn, actual.CenterColumn, 1e-12, name + " center column");
            Near(expected.PhiRadians, actual.PhiRadians, 1e-12, name + " phi");
            Near(expected.Length1, actual.Length1, 1e-12, name + " length1");
            Near(expected.Length2, actual.Length2, 1e-12, name + " length2");
            Near(expected.Radius, actual.Radius, 1e-12, name + " radius");
            int expectedCount = expected.Points == null ? 0 : expected.Points.Count;
            int actualCount = actual.Points == null ? 0 : actual.Points.Count;
            Equal(expectedCount, actualCount, name + " point count");
            for (int i = 0; i < expectedCount; i++)
            {
                Near(expected.Points[i].Row, actual.Points[i].Row, 1e-12, name + " point row " + i);
                Near(expected.Points[i].Column, actual.Points[i].Column, 1e-12, name + " point column " + i);
            }
        }

        private static void AssertSample(CalibrationSample expected, CalibrationSample actual, string name)
        {
            True(actual != null, name + " is null");
            Equal(expected.SampleId, actual.SampleId, name + " id");
            Equal(expected.Kind, actual.Kind, name + " kind");
            Equal(expected.Enabled, actual.Enabled, name + " enabled");
            Equal(expected.TemplateRevision, actual.TemplateRevision, name + " template revision");
            Equal(expected.Tag, actual.Tag, name + " tag");
            Near(expected.MatchScore, actual.MatchScore, 1e-12, name + " score");
            Near(expected.MachinePose.X, actual.MachinePose.X, 1e-12, name + " machine X");
            Near(expected.MachinePose.Y, actual.MachinePose.Y, 1e-12, name + " machine Y");
            Near(expected.MachinePose.ThetaDegrees, actual.MachinePose.ThetaDegrees, 1e-12, name + " machine theta");
            Near(expected.ImagePose.Row, actual.ImagePose.Row, 1e-12, name + " image row");
            Near(expected.ImagePose.Column, actual.ImagePose.Column, 1e-12, name + " image column");
            Near(expected.ImagePose.AngleDegrees, actual.ImagePose.AngleDegrees, 1e-12, name + " image angle");
            Equal(expected.CapturedAtUtc, actual.CapturedAtUtc, name + " capture time");
        }

        private static void BytesEqual(byte[] expected, byte[] actual, string message)
        {
            True(expected != null && actual != null, message + ": byte array is null");
            Equal(expected.Length, actual.Length, message + " length");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    throw new InvalidOperationException(message + ": mismatch at byte " + i);
            }
        }

        private static void Contains(string value, string expectedPart, string message)
        {
            if (value == null || value.IndexOf(expectedPart, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException(message + ": expected text '" + expectedPart + "', got '" + value + "'");
        }

        private static void Near(double expected, double actual, double tolerance, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
                throw new InvalidOperationException(string.Format("{0}: expected {1:G17}, got {2:G17}", message, expected, actual));
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(string.Format("{0}: expected '{1}', got '{2}'", message, expected, actual));
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
