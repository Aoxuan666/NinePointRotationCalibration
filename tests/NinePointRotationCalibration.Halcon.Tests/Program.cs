using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NinePointRotationCalibration.Halcon;

namespace NinePointRotationCalibration.Halcon.Tests
{
    internal static class Program
    {
        private const int ImageWidth = 320;
        private const int ImageHeight = 260;
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.WriteLine("NinePointRotationCalibration HALCON self-test");
            Console.WriteLine("Process: {0}-bit", Environment.Is64BitProcess ? 64 : 32);
            Console.WriteLine();

            Run("Effective region applies exclude mask and erase stroke", TestMaskAndErase);
            Run("Ordered stroke restore and inverted mask area", TestStrokeRestoreAndMaskInversion);
            Run("Shape model reload locates rotation and custom anchor", TestShapeReloadAndAnchor);
            Run("NCC model reload locates translated target", TestNccReload);

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
                Console.WriteLine("[FAIL] " + name);
                Console.WriteLine("       " + Flatten(exception));
            }
        }

        private static void TestMaskAndErase()
        {
            TemplateDefinition plain = CreateDefinition(TemplateModelType.Shape, 120.0, 130.0);
            TemplateDefinition masked = plain.DeepClone();
            masked.MaskRegions.Add(new TemplateMaskShape
            {
                Operation = MaskOperation.Exclude,
                Region = new RegionDefinition
                {
                    Kind = RegionKind.Circle,
                    CenterRow = 120.0,
                    CenterColumn = 130.0,
                    Radius = 11.0
                }
            });
            TemplateDefinition erased = plain.DeepClone();
            erased.EraseStrokes.Add(new TemplateEraseStroke
            {
                Radius = 4.0,
                Points =
                {
                    new ImagePoint(90.0, 102.0),
                    new ImagePoint(150.0, 102.0)
                }
            });
            TemplateDefinition combined = masked.DeepClone();
            combined.EraseStrokes.Add(erased.EraseStrokes[0].DeepClone());

            using (HalconTemplateEngine engine = new HalconTemplateEngine())
            {
                TemplateRegionPreviewResult plainPreview = engine.PreviewEffectiveRegion(plain, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult maskedPreview = engine.PreviewEffectiveRegion(masked, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult erasedPreview = engine.PreviewEffectiveRegion(erased, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult combinedPreview = engine.PreviewEffectiveRegion(combined, ImageWidth, ImageHeight);

                True(plainPreview.Success, "plain preview failed: " + plainPreview.Message);
                True(maskedPreview.Success, "masked preview failed: " + maskedPreview.Message);
                True(erasedPreview.Success, "erased preview failed: " + erasedPreview.Message);
                True(combinedPreview.Success, "combined preview failed: " + combinedPreview.Message);
                True(maskedPreview.EffectiveArea < plainPreview.EffectiveArea,
                    "exclude mask did not reduce the effective area");
                True(erasedPreview.EffectiveArea < plainPreview.EffectiveArea,
                    "erase stroke did not reduce the effective area");
                True(combinedPreview.EffectiveArea < maskedPreview.EffectiveArea,
                    "erase stroke did not further reduce the masked effective area");
                True(combinedPreview.EffectiveArea < erasedPreview.EffectiveArea,
                    "exclude mask did not further reduce the erased effective area");
                True(combinedPreview.ContourSegments.Count > 0,
                    "edited region should expose a preview boundary");
            }
        }

        private static void TestStrokeRestoreAndMaskInversion()
        {
            TemplateDefinition plain = CreateDefinition(TemplateModelType.Shape, 120.0, 130.0);
            TemplateEraseStroke excludeStroke = new TemplateEraseStroke
            {
                Operation = MaskOperation.Exclude,
                Radius = 6.0,
                Points =
                {
                    new ImagePoint(92.0, 112.0),
                    new ImagePoint(148.0, 112.0)
                }
            };
            TemplateEraseStroke includeStroke = excludeStroke.DeepClone();
            includeStroke.Operation = MaskOperation.Include;

            TemplateDefinition excluded = plain.DeepClone();
            excluded.EraseStrokes.Add(excludeStroke.DeepClone());

            TemplateDefinition restored = plain.DeepClone();
            restored.EraseStrokes.Add(excludeStroke.DeepClone());
            restored.EraseStrokes.Add(includeStroke.DeepClone());

            TemplateDefinition reversed = plain.DeepClone();
            reversed.EraseStrokes.Add(includeStroke.DeepClone());
            reversed.EraseStrokes.Add(excludeStroke.DeepClone());

            TemplateDefinition inverted = excluded.DeepClone();
            inverted.IsMaskInverted = true;

            using (HalconTemplateEngine engine = new HalconTemplateEngine())
            {
                TemplateRegionPreviewResult plainPreview = engine.PreviewEffectiveRegion(plain, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult excludedPreview = engine.PreviewEffectiveRegion(excluded, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult restoredPreview = engine.PreviewEffectiveRegion(restored, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult reversedPreview = engine.PreviewEffectiveRegion(reversed, ImageWidth, ImageHeight);
                TemplateRegionPreviewResult invertedPreview = engine.PreviewEffectiveRegion(inverted, ImageWidth, ImageHeight);

                True(plainPreview.Success, "plain preview failed: " + plainPreview.Message);
                True(excludedPreview.Success, "excluded preview failed: " + excludedPreview.Message);
                True(restoredPreview.Success, "restored preview failed: " + restoredPreview.Message);
                True(reversedPreview.Success, "reverse-ordered preview failed: " + reversedPreview.Message);
                True(invertedPreview.Success, "inverted preview failed: " + invertedPreview.Message);
                True(excludedPreview.EffectiveArea < plainPreview.EffectiveArea,
                    "exclude stroke did not reduce the effective area");
                True(restoredPreview.EffectiveArea > excludedPreview.EffectiveArea,
                    "ordered include stroke did not restore excluded pixels");
                Near(plainPreview.EffectiveArea, restoredPreview.EffectiveArea, 0.5,
                    "exclude-then-include restored area");
                Near(excludedPreview.EffectiveArea, reversedPreview.EffectiveArea, 0.5,
                    "stroke list order");
                True(invertedPreview.EffectiveArea > 0.0,
                    "inverted mask should retain the excluded stroke pixels");
                Near(
                    plainPreview.EffectiveArea,
                    excludedPreview.EffectiveArea + invertedPreview.EffectiveArea,
                    0.5,
                    "normal and inverted mask partition the template ROI");
            }
        }

        private static void TestShapeReloadAndAnchor()
        {
            const double referenceRow = 112.0;
            const double referenceColumn = 105.0;
            const double targetRow = 164.0;
            const double targetColumn = 218.0;
            const double graphicsAngleDegrees = 13.0;
            ImagePoint customAnchor = new ImagePoint(referenceRow - 17.0, referenceColumn + 24.0);

            using (Bitmap reference = CreateSyntheticImage(referenceRow, referenceColumn, 0.0))
            using (Bitmap target = CreateSyntheticImage(targetRow, targetColumn, graphicsAngleDegrees))
            {
                TemplateDefinition definition = CreateDefinition(TemplateModelType.Shape, referenceRow, referenceColumn);
                definition.ReferenceAnchor = customAnchor;
                definition.IsReferenceAnchorLocked = true;
                definition.MaskRegions.Add(new TemplateMaskShape
                {
                    Operation = MaskOperation.Exclude,
                    Region = new RegionDefinition
                    {
                        Kind = RegionKind.Circle,
                        CenterRow = referenceRow + 35.0,
                        CenterColumn = referenceColumn + 39.0,
                        Radius = 8.0
                    }
                });

                TemplateBuildResult build;
                using (HalconTemplateEngine builder = new HalconTemplateEngine())
                {
                    build = builder.BuildTemplate(reference, definition);
                }

                AssertBuiltModel(build, TemplateModelType.Shape);

                TemplateMatchResult match;
                using (HalconTemplateEngine reloadedEngine = new HalconTemplateEngine())
                {
                    match = reloadedEngine.Locate(target, build.Template, true);
                }

                True(match.Success, "shape locate failed after model reload: " + match.Message);
                True(match.Score >= 0.65, "shape score was too low: " + match.Score.ToString("0.000"));
                True(match.ContourSegments.Count > 0, "shape locate should return model contours");

                ImagePoint expectedModelReference = TransformScreenPoint(
                    build.Template.ModelReference.Row,
                    build.Template.ModelReference.Column,
                    referenceRow,
                    referenceColumn,
                    targetRow,
                    targetColumn,
                    graphicsAngleDegrees);
                ImagePoint expectedAnchor = TransformScreenPoint(
                    customAnchor.Row,
                    customAnchor.Column,
                    referenceRow,
                    referenceColumn,
                    targetRow,
                    targetColumn,
                    graphicsAngleDegrees);

                NearPoint(expectedModelReference, new ImagePoint(match.ModelPose.Row, match.ModelPose.Column), 2.5,
                    "shape model reference");
                NearPoint(expectedAnchor, match.Anchor, 2.5, "custom anchor");
                Near(-graphicsAngleDegrees, match.AngleDegrees, 2.0, "shape angle");
            }
        }

        private static void TestNccReload()
        {
            const double referenceRow = 106.0;
            const double referenceColumn = 104.0;
            const double targetRow = 169.0;
            const double targetColumn = 222.0;

            using (Bitmap reference = CreateSyntheticImage(referenceRow, referenceColumn, 0.0))
            using (Bitmap target = CreateSyntheticImage(targetRow, targetColumn, 0.0))
            {
                TemplateDefinition definition = CreateDefinition(TemplateModelType.Ncc, referenceRow, referenceColumn);
                definition.NccParameters.AngleStartRad = DegreesToRadians(-5.0);
                definition.NccParameters.AngleExtentRad = DegreesToRadians(10.0);
                definition.MatchParameters.AngleStartRad = DegreesToRadians(-5.0);
                definition.MatchParameters.AngleExtentRad = DegreesToRadians(10.0);
                definition.MatchParameters.MinScore = 0.75;

                TemplateBuildResult build;
                using (HalconTemplateEngine builder = new HalconTemplateEngine())
                {
                    build = builder.BuildTemplate(reference, definition);
                }

                AssertBuiltModel(build, TemplateModelType.Ncc);

                TemplateMatchResult match;
                using (HalconTemplateEngine reloadedEngine = new HalconTemplateEngine())
                {
                    match = reloadedEngine.Locate(target, build.Template, false);
                }

                True(match.Success, "NCC locate failed after model reload: " + match.Message);
                True(match.Score >= 0.90, "NCC score was too low: " + match.Score.ToString("0.000"));
                True(match.ContourSegments.Count == 0, "includeContour=false should suppress contours");

                ImagePoint expectedModelReference = TransformScreenPoint(
                    build.Template.ModelReference.Row,
                    build.Template.ModelReference.Column,
                    referenceRow,
                    referenceColumn,
                    targetRow,
                    targetColumn,
                    0.0);
                NearPoint(expectedModelReference, new ImagePoint(match.ModelPose.Row, match.ModelPose.Column), 1.5,
                    "NCC model reference");
                NearPoint(new ImagePoint(targetRow, targetColumn), match.Anchor, 1.5, "NCC default anchor");
            }
        }

        private static TemplateDefinition CreateDefinition(TemplateModelType modelType, double row, double column)
        {
            TemplateDefinition definition = new TemplateDefinition
            {
                ModelType = modelType,
                TemplateRoi = new RegionDefinition
                {
                    Kind = RegionKind.Rectangle1,
                    CenterRow = row,
                    CenterColumn = column,
                    Length1 = 50.0,
                    Length2 = 46.0
                }
            };

            definition.ShapeParameters.NumLevels = 4;
            definition.ShapeParameters.AngleStartRad = DegreesToRadians(-30.0);
            definition.ShapeParameters.AngleExtentRad = DegreesToRadians(60.0);
            definition.ShapeParameters.AngleStepRad = DegreesToRadians(1.0);
            definition.ShapeParameters.Contrast = 20;
            definition.ShapeParameters.MinContrast = 8;
            definition.NccParameters.NumLevels = 3;
            definition.NccParameters.AngleStartRad = DegreesToRadians(-30.0);
            definition.NccParameters.AngleExtentRad = DegreesToRadians(60.0);
            definition.NccParameters.AngleStepRad = DegreesToRadians(1.0);
            definition.MatchParameters.AngleStartRad = DegreesToRadians(-30.0);
            definition.MatchParameters.AngleExtentRad = DegreesToRadians(60.0);
            definition.MatchParameters.MinScore = 0.55;
            definition.MatchParameters.NumMatches = 1;
            definition.MatchParameters.MaxOverlap = 0.5;
            definition.MatchParameters.NumLevels = 0;
            definition.MatchParameters.Greediness = 0.8;
            return definition;
        }

        private static Bitmap CreateSyntheticImage(double centerRow, double centerColumn, double angleDegrees)
        {
            Bitmap bitmap = new Bitmap(ImageWidth, ImageHeight, PixelFormat.Format24bppRgb);
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
            double row,
            double column,
            double sourceCenterRow,
            double sourceCenterColumn,
            double targetCenterRow,
            double targetCenterColumn,
            double graphicsAngleDegrees)
        {
            double radians = DegreesToRadians(graphicsAngleDegrees);
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            double localColumn = column - sourceCenterColumn;
            double localRow = row - sourceCenterRow;
            return new ImagePoint(
                targetCenterRow + (sine * localColumn) + (cosine * localRow),
                targetCenterColumn + (cosine * localColumn) - (sine * localRow));
        }

        private static void AssertBuiltModel(TemplateBuildResult build, TemplateModelType expectedType)
        {
            True(build.Success, expectedType + " build failed: " + build.Message);
            True(build.Template != null, expectedType + " build returned no template");
            True(build.Template.ModelType == expectedType, expectedType + " model type changed during build");
            True(build.Template.HasUsableModel, expectedType + " model was left dirty or empty");
            True(build.Template.ModelData != null && build.Template.ModelData.Length > 0,
                expectedType + " model bytes were not serialized");
            True(string.Equals(
                    build.Template.ModelHash,
                    TemplateDefinition.ComputeModelHash(build.Template.ModelData),
                    StringComparison.OrdinalIgnoreCase),
                expectedType + " model hash does not match serialized bytes");
        }

        private static void NearPoint(ImagePoint expected, ImagePoint actual, double tolerance, string message)
        {
            double distance = expected.DistanceTo(actual);
            if (double.IsNaN(distance) || distance > tolerance)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}: expected {1}, got {2}, distance {3:0.###} px (tolerance {4:0.###})",
                    message,
                    expected,
                    actual,
                    distance,
                    tolerance));
            }
        }

        private static void Near(double expected, double actual, double tolerance, string message)
        {
            double delta = Math.Abs(expected - actual);
            if (double.IsNaN(actual) || delta > tolerance)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}: expected {1:0.###}, got {2:0.###} (tolerance {3:0.###})",
                    message,
                    expected,
                    actual,
                    tolerance));
            }
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static string Flatten(Exception exception)
        {
            if (exception == null) return "Unknown error.";
            return exception.InnerException == null
                ? exception.GetType().Name + ": " + exception.Message
                : exception.GetType().Name + ": " + exception.Message + " -> " + Flatten(exception.InnerException);
        }
    }
}
