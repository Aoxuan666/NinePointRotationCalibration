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
    }

    public sealed class MappedTargetPose
    {
        public TeachingSide Side { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double ROffset { get; set; }
        public double TargetR { get; set; }
    }
}
