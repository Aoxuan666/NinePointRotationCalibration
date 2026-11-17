using System;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>双吸嘴示教侧别。</summary>
    public enum TeachingSide
    {
        Left = 0,
        Right = 1
    }

    /// <summary>单个吸嘴示教采集到的模板像素位姿与对应机台物理坐标。</summary>
    public sealed class TeachingPose
    {
        public TeachingSide Side { get; set; }
        public bool IsValid { get; set; }
        public double ImageRow { get; set; }
        public double ImageColumn { get; set; }
        public double ImageAngleDegrees { get; set; }
        public double MachineX { get; set; }
        public double MachineY { get; set; }
        public double MachineR { get; set; }
        public DateTime CapturedAtUtc { get; set; }
    }

    /// <summary>左右双吸嘴示教位姿集合及吸嘴间距派生参数。</summary>
    public sealed class TeachingData
    {
        public TeachingData()
        {
            Left = new TeachingPose { Side = TeachingSide.Left };
            Right = new TeachingPose { Side = TeachingSide.Right };
        }

        public TeachingPose Left { get; set; }
        public TeachingPose Right { get; set; }

        /// <summary>左右两个吸嘴是否均已保存有效示教位姿。</summary>
        public bool HasBothNozzles
        {
            get { return Left != null && Left.IsValid && Right != null && Right.IsValid; }
        }

        /// <summary>右吸嘴相对左吸嘴的 X 向物理间距（mm）。</summary>
        public double NozzlePitchX
        {
            get { return HasBothNozzles ? Right.MachineX - Left.MachineX : 0.0; }
        }

        /// <summary>右吸嘴相对左吸嘴的 Y 向物理间距（mm）。</summary>
        public double NozzlePitchY
        {
            get { return HasBothNozzles ? Right.MachineY - Left.MachineY : 0.0; }
        }

        /// <summary>左右吸嘴中心之间的欧氏物理距离（mm）。</summary>
        public double NozzleDistance
        {
            get
            {
                if (!HasBothNozzles) return 0.0;
                double dx = NozzlePitchX;
                double dy = NozzlePitchY;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
        }
    }

    /// <summary>将检测到的 PCB 图像位姿映射到机台坐标后的吸取与摆盘目标位姿。</summary>
    public sealed class MappedTargetPose
    {
        public TeachingSide Side { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>相对示教基准角度的旋转偏移量（度）。</summary>
        public double ROffset { get; set; }
        /// <summary>映射后对应的目标旋转角度（度）。</summary>
        public double TargetR { get; set; }
        /// <summary>吸取时 R 轴旋转对齐 PCB 后的目标绝对角度（度）。</summary>
        public double PickR { get; set; }
        /// <summary>盲摆到固定位置时 R 轴转回的固定示教角度（通常为 0°）。</summary>
        public double BlindPlaceR { get; set; }
    }
}
