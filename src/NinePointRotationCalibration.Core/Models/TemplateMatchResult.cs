using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>单次模板匹配输出结果，包含锚点、模板位姿、得分与轮廓点。</summary>
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
        /// <summary>模板参考锚点在目标图像中的匹配位置。</summary>
        public ImagePoint Anchor { get; set; }
        /// <summary>模板原点（质心/基准）在目标图像中的位姿与旋转角。</summary>
        public ImagePose ModelPose { get; set; }
        public double Score { get; set; }
        public List<ImagePoint> Contour { get; set; }
        public List<List<ImagePoint>> ContourSegments { get; set; }
        public double ElapsedMilliseconds { get; set; }

        /// <summary>匹配锚点行坐标（读写 Anchor.Row）。</summary>
        public double Row
        {
            get { return Anchor.Row; }
            set { Anchor = new ImagePoint(value, Anchor.Column); }
        }

        /// <summary>匹配锚点列坐标（读写 Anchor.Column）。</summary>
        public double Column
        {
            get { return Anchor.Column; }
            set { Anchor = new ImagePoint(Anchor.Row, value); }
        }

        /// <summary>匹配角度（弧度，读写 ModelPose.AngleRadians）。</summary>
        public double AngleRad
        {
            get { return ModelPose == null ? double.NaN : ModelPose.AngleRadians; }
            set
            {
                if (ModelPose == null) ModelPose = new ImagePose();
                ModelPose.AngleRadians = value;
            }
        }

        /// <summary>匹配角度（度，读写 ModelPose.AngleDegrees）。</summary>
        public double AngleDegrees
        {
            get { return ModelPose == null ? double.NaN : ModelPose.AngleDegrees; }
            set
            {
                if (ModelPose == null) ModelPose = new ImagePose();
                ModelPose.AngleDegrees = value;
            }
        }

        /// <summary>构造失败的匹配结果。</summary>
        public static TemplateMatchResult Failure(CalibrationErrorCode errorCode, string message)
        {
            return new TemplateMatchResult { Success = false, ErrorCode = errorCode, Message = message };
        }

        /// <summary>创建匹配结果的深拷贝（含轮廓点集合）。</summary>
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
