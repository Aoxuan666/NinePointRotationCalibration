using System;
using System.Collections.Generic;
using NinePointRotationCalibration;

namespace NinePointRotationCalibration.Core.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Run("Affine inverse round-trip", TestAffineInverse);
            Run("Angle unwrap", TestAngleUnwrap);
            Run("Exact nine-point affine", TestExactNinePoint);
            Run("Robust affine outlier rejection", TestAffineOutlier);
            Run("Degenerate translation geometry", TestDegenerateGeometry);
            Run("Opposite rotation and center with XY compensation", TestRotationCenter);
            Run("Deep clone and template revision invalidation", TestDeepCloneAndRevision);
            Run("Full job validation", TestValidation);
            Run("Reject unknown enum values", TestUnknownEnumValues);

            Console.WriteLine();
            Console.WriteLine("Passed: {0}; Failed: {1}", _passed, _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + ": " + exception.Message);
            }
        }

        private static void TestAffineInverse()
        {
            AffineMatrix2D matrix = new AffineMatrix2D(2.0, 0.25, 100.0, -0.1, 1.5, 50.0);
            AffineMatrix2D inverse = matrix.Inverse();
            Point2D original = new Point2D(12.5, -7.25);
            Point2D roundTrip = inverse.Transform(matrix.Transform(original));
            Near(original.X, roundTrip.X, 1e-10, "round-trip X");
            Near(original.Y, roundTrip.Y, 1e-10, "round-trip Y");
        }

        private static void TestAngleUnwrap()
        {
            double[] values = AngleMath.UnwrapDegrees(new[] { 170.0, -175.0, -160.0 });
            Near(170.0, values[0], 1e-12, "unwrap[0]");
            Near(185.0, values[1], 1e-12, "unwrap[1]");
            Near(200.0, values[2], 1e-12, "unwrap[2]");
        }

        private static void TestExactNinePoint()
        {
            AffineMatrix2D expected = TestMatrix();
            CalibrationJob job = CreateTranslationJob(expected, false, false);
            CalibrationResult result = CalibrationSolver.Solve(job);
            True(result.Success, result.Message);
            True(result.TranslationSuccess, "translation should succeed");
            Near(expected.M11, result.StageToPixelMatrix.M11, 1e-10, "M11");
            Near(expected.M12, result.StageToPixelMatrix.M12, 1e-10, "M12");
            Near(expected.M21, result.StageToPixelMatrix.M21, 1e-10, "M21");
            Near(expected.M22, result.StageToPixelMatrix.M22, 1e-10, "M22");
            Near(expected.OffsetX, result.StageToPixelMatrix.OffsetX, 1e-10, "offset X");
            Near(expected.OffsetY, result.StageToPixelMatrix.OffsetY, 1e-10, "offset Y");
            Near(0.0, result.RmsResidualPixels, 1e-9, "RMS");
            True(result.InlierCount == 9, "all nine samples should be inliers");
        }

        private static void TestAffineOutlier()
        {
            AffineMatrix2D expected = TestMatrix();
            CalibrationJob job = CreateTranslationJob(expected, false, true);
            CalibrationResult result = CalibrationSolver.Solve(job);
            True(result.Success, result.Message);
            True(result.RejectedCount == 1, "one gross translation outlier should be rejected");
            Near(expected.M11, result.StageToPixelMatrix.M11, 1e-8, "robust M11");
            Near(expected.M22, result.StageToPixelMatrix.M22, 1e-8, "robust M22");
            True(!result.Diagnostics[8].IsInlier, "corrupted sample should be marked outlier");
        }

        private static void TestDegenerateGeometry()
        {
            CalibrationJob job = BasicJob(false);
            for (int i = 0; i < 9; i++)
            {
                double x = i;
                AddSample(job, CalibrationSampleKind.Translation, x, 0.0, 0.0, 100.0 + x, 50.0, 0.0);
            }

            CalibrationResult result = CalibrationSolver.Solve(job);
            True(!result.Success, "collinear calibration must fail");
            True(result.ErrorCode == CalibrationErrorCode.DegenerateGeometry, "expected degenerate geometry error");
        }

        private static void TestRotationCenter()
        {
            AffineMatrix2D matrix = TestMatrix();
            CalibrationJob job = CreateTranslationJob(matrix, true, false);
            job.SolverOptions.MinimumRotationSamples = 5;
            job.SolverOptions.MinimumRotationInliers = 4;
            job.SolverOptions.MaxAngleRmsDegrees = 0.01;
            job.SolverOptions.MaxRotationCenterRmsPixels = 0.01;

            double[] angles = { -20.0, -10.0, 0.0, 10.0, 20.0 };
            StagePoint[] stages =
            {
                new StagePoint(0.0, 0.0),
                new StagePoint(1.0, -1.0),
                new StagePoint(-1.0, 1.0),
                new StagePoint(0.5, 0.5),
                new StagePoint(-0.5, -0.5)
            };
            const double centerColumn = 320.0;
            const double centerRow = 240.0;
            const double localX = 40.0;
            const double localY = 10.0;
            for (int i = 0; i < angles.Length; i++)
            {
                double phi = AngleMath.DegreesToRadians(-angles[i]);
                double normalizedColumn = centerColumn + (Math.Cos(phi) * localX) - (Math.Sin(phi) * localY);
                double normalizedRow = centerRow + (Math.Sin(phi) * localX) + (Math.Cos(phi) * localY);
                Vector2D shift = matrix.TransformVector(new Vector2D(stages[i].X, stages[i].Y));
                double imageAngle = AngleMath.NormalizeDegrees((-angles[i]) + 175.0);
                AddSample(job, CalibrationSampleKind.Rotation,
                    stages[i].X, stages[i].Y, angles[i],
                    normalizedColumn + shift.X, normalizedRow + shift.Y, imageAngle);
            }

            CalibrationResult result = CalibrationSolver.Solve(job);
            True(result.Success, result.Message);
            True(result.Rotation.Success, result.Rotation.Message);
            True(result.Rotation.Direction == RotationDirection.Opposite, "rotation direction should be opposite");
            Near(175.0, result.Rotation.AngleOffsetDegrees, 1e-8, "angle offset across wrap");
            Near(-1.0, result.Rotation.UnconstrainedSlope, 1e-8, "unconstrained slope");
            Near(centerColumn, result.Rotation.RotationCenterImage.Column, 1e-7, "center column");
            Near(centerRow, result.Rotation.RotationCenterImage.Row, 1e-7, "center row");
            Near(Math.Sqrt((localX * localX) + (localY * localY)), result.Rotation.RadiusPixels, 1e-7, "radius");
            Near(0.0, result.Rotation.CenterRmsResidualPixels, 1e-7, "center RMS");
        }

        private static void TestDeepCloneAndRevision()
        {
            CalibrationJob job = BasicJob(false);
            job.Template.ModelData = new byte[] { 1, 2, 3 };
            job.Template.MaskRegions.Add(new TemplateMaskShape
            {
                Operation = MaskOperation.Exclude,
                Region = new RegionDefinition
                {
                    Kind = RegionKind.Circle,
                    CenterRow = 10,
                    CenterColumn = 20,
                    Radius = 3
                }
            });
            AddSample(job, CalibrationSampleKind.Translation, 0, 0, 0, 10, 10, 0);
            CalibrationJob clone = job.DeepClone();
            clone.Template.ModelData[0] = 99;
            clone.Template.MaskRegions[0].Region.Radius = 77;
            True(job.Template.ModelData[0] == 1, "model data must be deep-cloned");
            Near(3.0, job.Template.MaskRegions[0].Region.Radius, 0.0, "mask region clone");

            True(!job.HasStaleEnabledSamples, "new sample should match template revision");
            job.MarkTemplateChanged();
            True(job.HasStaleEnabledSamples, "template change must invalidate existing samples");
        }

        private static void TestValidation()
        {
            AffineMatrix2D matrix = TestMatrix();
            CalibrationJob job = CreateTranslationJob(matrix, true, false);
            job.Template.TemplateRoi = new RegionDefinition
            {
                Kind = RegionKind.Rectangle2,
                CenterRow = 200,
                CenterColumn = 300,
                PhiRadians = 0,
                Length1 = 50,
                Length2 = 40
            };
            job.Template.DomainCenter = new ImagePoint(200, 300);
            job.Template.ReferenceAnchor = new ImagePoint(200, 300);
            job.Template.SetModelData(new byte[] { 1, 2, 3, 4 }, "self-test");

            double[] angles = { -20, 0, 20 };
            for (int i = 0; i < angles.Length; i++)
            {
                double radians = AngleMath.DegreesToRadians(angles[i]);
                AddSample(job, CalibrationSampleKind.Rotation, 0, 0, angles[i],
                    320 + (30 * Math.Cos(radians)), 240 + (30 * Math.Sin(radians)), angles[i] + 5);
            }

            JobValidationResult validation = CalibrationJobValidator.Validate(job, CalibrationValidationScope.Full);
            True(validation.IsValid, "expected a valid full job; errors=" + validation.ErrorCount);
        }

        private static void TestUnknownEnumValues()
        {
            CalibrationJob job = CreateConfigurationFixture();

            job.LinearUnit = (LinearUnit)12345;
            AssertIssue(job, CalibrationValidationScope.Configuration, "LINEAR_UNIT_INVALID");

            job = CreateConfigurationFixture();
            job.RotationConvention = (RotationConvention)12345;
            AssertIssue(job, CalibrationValidationScope.Configuration, "ROTATION_CONVENTION_INVALID");

            job = CreateConfigurationFixture();
            job.Template.ModelType = (TemplateModelType)12345;
            AssertIssue(job, CalibrationValidationScope.Configuration, "MODEL_TYPE_INVALID");
            True(CalibrationSolver.Solve(job).ErrorCode == CalibrationErrorCode.ParameterInvalid,
                "solver should reject an unknown model type");

            job = CreateConfigurationFixture();
            job.Template.TemplateRoi.Kind = (RegionKind)12345;
            AssertIssue(job, CalibrationValidationScope.Configuration, "REGION_KIND_INVALID");

            job = CreateConfigurationFixture();
            job.Template.MaskRegions.Add(new TemplateMaskShape
            {
                Operation = (MaskOperation)12345,
                Region = new RegionDefinition
                {
                    Kind = RegionKind.Circle,
                    Radius = 1.0
                }
            });
            AssertIssue(job, CalibrationValidationScope.Configuration, "MASK_OPERATION_INVALID");

            job = CreateConfigurationFixture();
            job.AddSample(new CalibrationSample
            {
                Kind = (CalibrationSampleKind)12345,
                MachinePose = new MachinePose(0.0, 0.0),
                ImagePose = new ImagePose(0.0, 0.0),
                MatchScore = 1.0,
                Enabled = true
            });
            AssertIssue(job, CalibrationValidationScope.Translation, "SAMPLE_KIND_INVALID");
            True(CalibrationSolver.Solve(job).ErrorCode == CalibrationErrorCode.ParameterInvalid,
                "solver should reject an unknown sample kind");
        }

        private static CalibrationJob CreateConfigurationFixture()
        {
            CalibrationJob job = BasicJob(false);
            job.Template.TemplateRoi = new RegionDefinition
            {
                Kind = RegionKind.Rectangle1,
                Length1 = 1.0,
                Length2 = 1.0
            };
            job.Template.SetModelData(new byte[] { 1, 2, 3 }, "self-test");
            return job;
        }

        private static void AssertIssue(CalibrationJob job, CalibrationValidationScope scope, string code)
        {
            JobValidationResult validation = CalibrationJobValidator.Validate(job, scope);
            foreach (ValidationIssue issue in validation.Issues)
            {
                if (issue != null && issue.Code == code) return;
            }

            throw new InvalidOperationException("Expected validation issue " + code + ".");
        }

        private static CalibrationJob CreateTranslationJob(AffineMatrix2D matrix, bool requireRotation, bool corruptLast)
        {
            CalibrationJob job = BasicJob(requireRotation);
            int index = 0;
            double[] coordinates = { -10.0, 0.0, 10.0 };
            foreach (double y in coordinates)
            {
                foreach (double x in coordinates)
                {
                    Point2D pixel = matrix.Transform(new Point2D(x, y));
                    if (corruptLast && index == 8)
                        pixel = new Point2D(pixel.X + 25.0, pixel.Y - 18.0);
                    AddSample(job, CalibrationSampleKind.Translation, x, y, 0.0, pixel.X, pixel.Y, 0.0);
                    index++;
                }
            }
            return job;
        }

        private static CalibrationJob BasicJob(bool requireRotation)
        {
            CalibrationJob job = new CalibrationJob();
            job.SolverOptions.RequireRotationCalibration = requireRotation;
            job.SolverOptions.MaxRmsResidualPixels = 0.1;
            job.SolverOptions.MaxResidualPixels = 0.2;
            return job;
        }

        private static AffineMatrix2D TestMatrix()
        {
            return new AffineMatrix2D(2.0, 0.25, 320.0, -0.1, 1.5, 240.0);
        }

        private static void AddSample(
            CalibrationJob job,
            CalibrationSampleKind kind,
            double x,
            double y,
            double theta,
            double column,
            double row,
            double imageAngle)
        {
            job.AddSample(new CalibrationSample
            {
                Kind = kind,
                MachinePose = new MachinePose(x, y, theta),
                ImagePose = new ImagePose(row, column, imageAngle),
                MatchScore = 0.95,
                Enabled = true
            });
        }

        private static void Near(double expected, double actual, double tolerance, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
                throw new InvalidOperationException(string.Format("{0}: expected {1:G17}, got {2:G17}", message, expected, actual));
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
