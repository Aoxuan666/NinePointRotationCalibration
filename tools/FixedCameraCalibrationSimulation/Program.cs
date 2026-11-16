using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using NinePointRotationCalibration;
using NinePointRotationCalibration.Halcon;

namespace FixedCameraCalibrationSimulation
{
    internal static class Program
    {
        private const int ImageWidth = 640;
        private const int ImageHeight = 480;

        private const double ReferenceColumn = 320.0;
        private const double ReferenceRow = 240.0;

        // 固定相机模型：stage(X,Y) -> image(column,row)。
        private const double M11 = 2.4;
        private const double M12 = 0.25;
        private const double M21 = -0.15;
        private const double M22 = 2.1;

        private static int Main()
        {
            string outputDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "GeneratedImages");
            Directory.CreateDirectory(outputDirectory);

            CalibrationJob job;
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk())
            using (Bitmap reference = CreateSyntheticImage(ReferenceRow, ReferenceColumn, 0.0))
            {
                string referencePath = Path.Combine(outputDirectory, "reference.png");
                reference.Save(referencePath, ImageFormat.Png);

                TemplateDefinition definition = CreateTemplateDefinition();
                TemplateBuildResult build = sdk.BuildTemplate(reference, definition);
                if (build == null || !build.Success || build.Template == null)
                {
                    Console.WriteLine("模板创建失败: " + (build == null ? "无返回结果" : build.Message));
                    return 1;
                }

                job = new CalibrationJob
                {
                    Name = "固定相机合成标定",
                    Template = build.Template
                };

                Console.WriteLine("参考图: " + referencePath);
                Console.WriteLine("模板创建成功，HALCON 版本: " + build.Template.HalconVersion);

                AddTranslationSamples(sdk, job, outputDirectory);
                AddRotationSamples(sdk, job, outputDirectory);

                Console.WriteLine("有效样本数: " + job.Samples.Count);
                CalibrationResult result = sdk.Calculate(job);
                PrintResult(result);
                return result != null && result.Success ? 0 : 2;
            }
        }

        private static void AddTranslationSamples(
            NinePointRotationCalibrationSdk sdk,
            CalibrationJob job,
            string outputDirectory)
        {
            double[] axis = { -10.0, 0.0, 10.0 };
            int index = 0;
            foreach (double stageY in axis)
            {
                foreach (double stageX in axis)
                {
                    Point2D image = StageToImage(stageX, stageY);
                    string path = Path.Combine(outputDirectory, "translation_" + index.ToString("00") + ".png");
                    using (Bitmap bitmap = CreateSyntheticImage(image.Y, image.X, 0.0))
                    {
                        bitmap.Save(path, ImageFormat.Png);
                        SampleCaptureResult capture = sdk.CaptureSample(
                            job,
                            bitmap,
                            stageX,
                            stageY,
                            0.0,
                            "T" + index.ToString("00"),
                            CalibrationSampleKind.Translation);
                        RequireCapture(capture, path);
                        ReplaceJobSamples(job, capture.Job);
                    }

                    index++;
                }
            }
        }

        private static void AddRotationSamples(
            NinePointRotationCalibrationSdk sdk,
            CalibrationJob job,
            string outputDirectory)
        {
            double[] angles = { -40.0, -20.0, 0.0, 20.0, 40.0 };
            Point2D local = new Point2D(26.0, -14.0);
            for (int index = 0; index < angles.Length; index++)
            {
                double angle = angles[index];
                // Shape 模板返回的图像角度与机械角度方向相反，位置也按同一图像角度旋转。
                Point2D rotated = Rotate(local, -angle);
                Point2D image = new Point2D(
                    ReferenceColumn + rotated.X,
                    ReferenceRow + rotated.Y);
                string path = Path.Combine(outputDirectory, "rotation_" + index.ToString("00") + ".png");
                using (Bitmap bitmap = CreateSyntheticImage(image.Y, image.X, angle))
                {
                    bitmap.Save(path, ImageFormat.Png);
                    SampleCaptureResult capture = sdk.CaptureSample(
                        job,
                        bitmap,
                        0.0,
                        0.0,
                        angle,
                        "R" + index.ToString("00"),
                        CalibrationSampleKind.Rotation);
                    RequireCapture(capture, path);
                    ReplaceJobSamples(job, capture.Job);
                }
            }
        }

        private static void ReplaceJobSamples(CalibrationJob target, CalibrationJob source)
        {
            target.Samples = source.Samples;
            target.UpdatedAtUtc = source.UpdatedAtUtc;
        }

        private static void RequireCapture(SampleCaptureResult capture, string path)
        {
            if (capture == null || !capture.Success || capture.Job == null)
            {
                throw new InvalidOperationException(
                    "样本采集失败: " + path + " / "
                    + (capture == null ? "无返回结果" : capture.ErrorCode + " " + capture.Message));
            }

            Console.WriteLine(
                "匹配 " + Path.GetFileName(path)
                + " score=" + (capture.Match == null ? "n/a" : capture.Match.Score.ToString("0.000"))
                + " pose=(" + capture.Sample.ImagePose.Column.ToString("0.00")
                + "," + capture.Sample.ImagePose.Row.ToString("0.00")
                + "," + capture.Sample.ImagePose.AngleDegrees.ToString("0.00") + ")");
        }

        private static TemplateDefinition CreateTemplateDefinition()
        {
            TemplateDefinition definition = new TemplateDefinition
            {
                ModelType = TemplateModelType.Shape,
                TemplateRoi = new RegionDefinition
                {
                    Kind = RegionKind.Rectangle1,
                    CenterRow = ReferenceRow,
                    CenterColumn = ReferenceColumn,
                    Length1 = 52.0,
                    Length2 = 48.0
                }
            };

            definition.ShapeParameters.NumLevels = 4;
            definition.ShapeParameters.AngleStartRad = DegreesToRadians(-50.0);
            definition.ShapeParameters.AngleExtentRad = DegreesToRadians(100.0);
            definition.ShapeParameters.AngleStepRad = DegreesToRadians(1.0);
            definition.ShapeParameters.Contrast = 15;
            definition.ShapeParameters.MinContrast = 5;
            definition.MatchParameters.AngleStartRad = DegreesToRadians(-50.0);
            definition.MatchParameters.AngleExtentRad = DegreesToRadians(100.0);
            definition.MatchParameters.MinScore = 0.50;
            definition.MatchParameters.NumMatches = 1;
            definition.MatchParameters.MaxOverlap = 0.5;
            definition.MatchParameters.NumLevels = 0;
            definition.MatchParameters.Greediness = 0.8;
            return definition;
        }

        private static Bitmap CreateSyntheticImage(
            double centerRow,
            double centerColumn,
            double angleDegrees)
        {
            Bitmap bitmap = new Bitmap(ImageWidth, ImageHeight, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(25, 25, 25));
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.TranslateTransform((float)centerColumn, (float)centerRow);
                graphics.RotateTransform((float)angleDegrees);

                PointF[] body =
                {
                    new PointF(-38.0f, -32.0f),
                    new PointF(16.0f, -32.0f),
                    new PointF(16.0f, -18.0f),
                    new PointF(36.0f, -18.0f),
                    new PointF(36.0f, 25.0f),
                    new PointF(3.0f, 25.0f),
                    new PointF(3.0f, 36.0f),
                    new PointF(-38.0f, 36.0f)
                };

                using (Brush bodyBrush = new SolidBrush(Color.FromArgb(230, 230, 230)))
                using (Brush midBrush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                using (Brush darkBrush = new SolidBrush(Color.FromArgb(35, 35, 35)))
                using (Pen accentPen = new Pen(Color.FromArgb(250, 250, 250), 4.0f))
                {
                    graphics.FillPolygon(bodyBrush, body);
                    graphics.FillRectangle(midBrush, -28.0f, -22.0f, 20.0f, 17.0f);
                    graphics.FillEllipse(darkBrush, 9.0f, -8.0f, 16.0f, 16.0f);
                    graphics.FillRectangle(darkBrush, -27.0f, 13.0f, 20.0f, 12.0f);
                    graphics.DrawLine(accentPen, 8.0f, 14.0f, 27.0f, 14.0f);
                }
            }

            return bitmap;
        }

        private static Point2D StageToImage(double stageX, double stageY)
        {
            return new Point2D(
                ReferenceColumn + M11 * stageX + M12 * stageY,
                ReferenceRow + M21 * stageX + M22 * stageY);
        }

        private static Point2D Rotate(Point2D point, double degrees)
        {
            double radians = DegreesToRadians(degrees);
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Point2D(
                cos * point.X - sin * point.Y,
                sin * point.X + cos * point.Y);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void PrintResult(CalibrationResult result)
        {
            Console.WriteLine();
            Console.WriteLine("===== 固定相机合成标定结果 =====");
            Console.WriteLine("Success: " + (result != null && result.Success));
            Console.WriteLine("Message: " + (result == null ? "null" : result.Message));
            if (result == null) return;

            Console.WriteLine("TranslationSuccess: " + result.TranslationSuccess);
            Console.WriteLine("RMS pixel residual: " + result.RmsResidualPixels.ToString("0.0000"));
            Console.WriteLine("Max pixel residual: " + result.MaxResidualPixels.ToString("0.0000"));
            Console.WriteLine("StageToPixel: ["
                + result.StageToPixelMatrix.M11.ToString("0.000000") + ", "
                + result.StageToPixelMatrix.M12.ToString("0.000000") + ", "
                + result.StageToPixelMatrix.OffsetX.ToString("0.000000") + "; "
                + result.StageToPixelMatrix.M21.ToString("0.000000") + ", "
                + result.StageToPixelMatrix.M22.ToString("0.000000") + ", "
                + result.StageToPixelMatrix.OffsetY.ToString("0.000000") + "]");
            Console.WriteLine("Expected: ["
                + M11.ToString("0.000000") + ", "
                + M12.ToString("0.000000") + ", "
                + ReferenceColumn.ToString("0.000000") + "; "
                + M21.ToString("0.000000") + ", "
                + M22.ToString("0.000000") + ", "
                + ReferenceRow.ToString("0.000000") + "]");

            if (result.Rotation == null) return;
            Console.WriteLine("RotationSuccess: " + result.Rotation.Success);
            Console.WriteLine("Rotation direction: " + result.Rotation.Direction);
            Console.WriteLine("Rotation center image: "
                + result.Rotation.RotationCenterImage.Column.ToString("0.000")
                + ", " + result.Rotation.RotationCenterImage.Row.ToString("0.000"));
            Console.WriteLine("Rotation center stage: "
                + result.Rotation.RotationCenterStage.X.ToString("0.000")
                + ", " + result.Rotation.RotationCenterStage.Y.ToString("0.000"));
            Console.WriteLine("Angle RMS: " + result.Rotation.AngleRmsDegrees.ToString("0.0000"));
            Console.WriteLine("Center RMS pixel residual: "
                + result.Rotation.CenterRmsResidualPixels.ToString("0.0000"));
            Console.WriteLine("Inliers/rejected: "
                + result.Rotation.InlierCount + "/" + result.Rotation.RejectedCount);
        }
    }
}
