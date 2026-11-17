using System;

namespace NinePointRotationCalibration.Station.Models
{
    public enum TeachingSide
    {
        Left = 0,
        Right = 1
    }

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

    public sealed class TeachingData
    {
        public TeachingData()
        {
            Left = new TeachingPose { Side = TeachingSide.Left };
            Right = new TeachingPose { Side = TeachingSide.Right };
        }

        public TeachingPose Left { get; set; }
        public TeachingPose Right { get; set; }

        public bool HasBothNozzles
        {
            get { return Left != null && Left.IsValid && Right != null && Right.IsValid; }
        }

        public double NozzlePitchX
        {
            get { return HasBothNozzles ? Right.MachineX - Left.MachineX : 0.0; }
        }

        public double NozzlePitchY
        {
            get { return HasBothNozzles ? Right.MachineY - Left.MachineY : 0.0; }
        }

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

    public sealed class MappedTargetPose
    {
        public TeachingSide Side { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double ROffset { get; set; }
        public double TargetR { get; set; }
        /// <summary>吸取时 R 轴旋转对齐 PCB 后的目标绝对角度（度）。</summary>
        public double PickR { get; set; }
        /// <summary>盲摆到固定位置时 R 轴转回的固定示教角度（通常为 0°）。</summary>
        public double BlindPlaceR { get; set; }
    }
}
