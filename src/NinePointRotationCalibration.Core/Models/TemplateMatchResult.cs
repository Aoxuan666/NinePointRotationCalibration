using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    [Serializable]
    public sealed class TemplateMatchResult
    {
        public TemplateMatchResult()
        {
            Contour = new List<ImagePoint>();
            ContourSegments = new List<List<ImagePoint>>();
            ModelPose = new ImagePose();
        }

        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public ImagePoint Anchor { get; set; }
        public ImagePose ModelPose { get; set; }
        public double Score { get; set; }
        public List<ImagePoint> Contour { get; set; }
        public List<List<ImagePoint>> ContourSegments { get; set; }
        public double ElapsedMilliseconds { get; set; }

        public double Row
        {
            get { return Anchor.Row; }
            set { Anchor = new ImagePoint(value, Anchor.Column); }
        }

        public double Column
        {
            get { return Anchor.Column; }
            set { Anchor = new ImagePoint(Anchor.Row, value); }
        }

        public double AngleRad
        {
            get { return ModelPose == null ? double.NaN : ModelPose.AngleRadians; }
            set
            {
                if (ModelPose == null) ModelPose = new ImagePose();
                ModelPose.AngleRadians = value;
            }
        }

        public double AngleDegrees
        {
            get { return ModelPose == null ? double.NaN : ModelPose.AngleDegrees; }
            set
            {
                if (ModelPose == null) ModelPose = new ImagePose();
                ModelPose.AngleDegrees = value;
            }
        }

        public static TemplateMatchResult Failure(CalibrationErrorCode errorCode, string message)
        {
            return new TemplateMatchResult { Success = false, ErrorCode = errorCode, Message = message };
        }

        public TemplateMatchResult DeepClone()
        {
            TemplateMatchResult clone = (TemplateMatchResult)MemberwiseClone();
            clone.ModelPose = ModelPose == null ? null : ModelPose.DeepClone();
            clone.Contour = Contour == null ? new List<ImagePoint>() : new List<ImagePoint>(Contour);
            clone.ContourSegments = new List<List<ImagePoint>>();
            if (ContourSegments != null)
            {
                foreach (List<ImagePoint> segment in ContourSegments)
                    clone.ContourSegments.Add(segment == null ? null : new List<ImagePoint>(segment));
            }
            return clone;
        }
    }
}
