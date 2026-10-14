using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;

namespace NinePointRotationCalibration.Station.Models
{
    public sealed class DetectionRoi
    {
        public double ReferenceCenterRow { get; set; }
        public double ReferenceCenterColumn { get; set; }
        public double PhiDegrees { get; set; }
        public double HalfWidth { get; set; }
        public double HalfHeight { get; set; }
    }

    public sealed class DetectionProfile
    {
        public DetectionProfile()
        {
            AutoThreshold = true;
            DetectDarkForeground = false;
            BrightThreshold = 128;
            MinimumBlobArea = 20.0;
            MaximumBlobArea = 100000.0;
            RectangleFilterEnabled = true;
            RectangleFillThreshold = 0.92;
            FillHoles = true;
            MorphologyRadius = 1;
            FollowTemplateTransform = true;
            TemplateMatchingEnabled = true;
        }

        // Retained only to migrate detection-profile.json files created by older versions.
        public DetectionRoi Roi { get; set; }
        public bool FollowTemplateTransform { get; set; }
        public bool TemplateMatchingEnabled { get; set; }
        public bool AutoThreshold { get; set; }
        public bool DetectDarkForeground { get; set; }
        public int BrightThreshold { get; set; }
        public double MinimumBlobArea { get; set; }
        public double MaximumBlobArea { get; set; }
        public bool RectangleFilterEnabled { get; set; }
        public double RectangleFillThreshold { get; set; }
        public bool FillHoles { get; set; }
        public int MorphologyRadius { get; set; }

        // Older detection profiles do not contain this field. Seed the safe,
        // production-default value before the serializer applies present fields.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            TemplateMatchingEnabled = true;
            AutoThreshold = true;
            DetectDarkForeground = false;
            FillHoles = true;
            MorphologyRadius = 1;
        }
    }

    public sealed class BlobFinding
    {
        public BlobFinding()
        {
            Contour = new List<PointF>();
        }

        public double Row { get; set; }
        public double Column { get; set; }
        public double Area { get; set; }
        public double Rectangularity { get; set; }
        public bool RejectedAsRectangle { get; set; }
        public List<PointF> Contour { get; set; }
    }

    public sealed class BlobAnalysisResult : IDisposable
    {
        public BlobAnalysisResult()
        {
            Findings = new List<BlobFinding>();
        }

        public DetectionRoi Roi { get; set; }
        public Rectangle ImageBounds { get; set; }
        public Bitmap BinaryImage { get; set; }
        public List<BlobFinding> Findings { get; set; }
        public int ActivePixelCount { get; set; }
        public int EffectiveThreshold { get; set; }
        public double ImageConversionElapsedMilliseconds { get; set; }
        public double ThresholdElapsedMilliseconds { get; set; }
        public double MorphologyElapsedMilliseconds { get; set; }
        public double HoleFillElapsedMilliseconds { get; set; }
        public double BinaryImageElapsedMilliseconds { get; set; }
        public double ComponentElapsedMilliseconds { get; set; }
        public double ElapsedMilliseconds { get; set; }

        public void Dispose()
        {
            Bitmap binaryImage = BinaryImage;
            BinaryImage = null;
            if (binaryImage != null) binaryImage.Dispose();
        }
    }

    public sealed class BoardInspectionItem
    {
        public BoardInspectionItem()
        {
            Blobs = new List<BlobFinding>();
            Targets = new List<MappedTargetPose>();
            MatchContourSegments = new List<List<ImagePoint>>();
        }

        public int BoardIndex { get; set; }
        public double ImageRow { get; set; }
        public double ImageColumn { get; set; }
        public double ImageAngleDegrees { get; set; }
        public double MatchScore { get; set; }
        public bool HasManualMark { get; set; }
        public DetectionRoi DetectionRegion { get; set; }
        public List<List<ImagePoint>> MatchContourSegments { get; set; }
        public List<BlobFinding> Blobs { get; set; }
        public List<MappedTargetPose> Targets { get; set; }
    }

    public sealed class InspectionResult
    {
        public InspectionResult()
        {
            Items = new List<BoardInspectionItem>();
            ErrorCode = "NONE";
        }

        public bool Executed { get; set; }
        public bool Success { get; set; }
        public bool CountMatchesExpected { get; set; }
        public bool HasManualMarks { get; set; }
        public bool Pass { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public int ExpectedCount { get; set; }
        public int ActualCount { get; set; }
        public double CurrentR { get; set; }
        public double MatchElapsedMilliseconds { get; set; }
        public double BlobElapsedMilliseconds { get; set; }
        public double ImageConversionElapsedMilliseconds { get; set; }
        public double ThresholdElapsedMilliseconds { get; set; }
        public double MorphologyElapsedMilliseconds { get; set; }
        public double HoleFillElapsedMilliseconds { get; set; }
        public double ComponentElapsedMilliseconds { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public List<BoardInspectionItem> Items { get; set; }
    }
}
