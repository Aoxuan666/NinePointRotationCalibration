using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>基于模板参考坐标系或固定像素区域的检测 ROI 定义。</summary>
    public sealed class DetectionRoi
    {
        public double ReferenceCenterRow { get; set; }
        public double ReferenceCenterColumn { get; set; }
        public double PhiDegrees { get; set; }
        public double HalfWidth { get; set; }
        public double HalfHeight { get; set; }
    }

    /// <summary>人工标记（X/点）斑点二值化检测与矩形过滤参数。</summary>
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
            FillDarkHoles = true;
            MorphologyRadius = 1;
            FollowTemplateTransform = true;
            TemplateMatchingEnabled = true;
        }

        // 仅用于兼容迁移旧版 detection-profile.json 文件。
        public DetectionRoi Roi { get; set; }
        /// <summary>是否随单板模板匹配位姿变换检测区域。</summary>
        public bool FollowTemplateTransform { get; set; }
        /// <summary>是否启用模板匹配（关闭时仅执行全图或固定 ROI 斑点检测）。</summary>
        public bool TemplateMatchingEnabled { get; set; }
        public bool AutoThreshold { get; set; }
        public bool DetectDarkForeground { get; set; }
        public int BrightThreshold { get; set; }
        public double MinimumBlobArea { get; set; }
        public double MaximumBlobArea { get; set; }
        public bool RectangleFilterEnabled { get; set; }
        public double RectangleFillThreshold { get; set; }
        public bool FillHoles { get; set; }
        /// <summary>检测黑色目标（白底黑点/黑叉）时是否填充黑色目标内部空洞。</summary>
        public bool FillDarkHoles { get; set; }
        public int MorphologyRadius { get; set; }

        // 旧版检测配置文件缺少新增开关字段，在反序列化前先预置安全的生产默认值。
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            TemplateMatchingEnabled = true;
            AutoThreshold = true;
            DetectDarkForeground = false;
            FillHoles = true;
            FillDarkHoles = true;
            MorphologyRadius = 1;
        }
    }

    /// <summary>单个候选斑点的几何特征与矩形过滤状态。</summary>
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
        /// <summary>是否因矩形填充度过高（疑似矩形标签而非人工坏板标记）被排除。</summary>
        public bool RejectedAsRectangle { get; set; }
        public List<PointF> Contour { get; set; }
    }

    /// <summary>单次斑点分析的二值掩膜图、检出斑点及分阶段耗时统计。</summary>
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

        /// <summary>释放二值预览位图资源。</summary>
        public void Dispose()
        {
            Bitmap binaryImage = BinaryImage;
            BinaryImage = null;
            if (binaryImage != null) binaryImage.Dispose();
        }
    }

    /// <summary>单块分板（子板）的模板匹配结果、坏板标记与机台目标位姿。</summary>
    public sealed class BoardInspectionItem
    {
        public BoardInspectionItem()
        {
            Valid = true;
            Blobs = new List<BlobFinding>();
            Targets = new List<MappedTargetPose>();
            MatchContourSegments = new List<List<ImagePoint>>();
        }

        public int BoardIndex { get; set; }
        /// <summary>单板坐标有效性标识；若个别板件映射异常无法给出可靠坐标则为 false。</summary>
        public bool Valid { get; set; }
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

    /// <summary>整盘视觉定位与人工标记检测的汇总结果。</summary>
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
        /// <summary>是否综合通过（定位成功、数量匹配且无人工坏板标记）。</summary>
        public bool Pass { get; set; }
        /// <summary>本次检测是否已结合标定与示教结果完成机械坐标映射。</summary>
        public bool CoordinateMappingApplied { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public int ExpectedCount { get; set; }
        public int ActualCount { get; set; }
        public double CurrentRLeft { get; set; }
        public double CurrentRRight { get; set; }
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
