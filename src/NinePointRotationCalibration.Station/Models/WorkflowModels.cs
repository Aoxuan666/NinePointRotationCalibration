using System;

namespace NinePointRotationCalibration.Station.Models
{
    public enum CalibrationPointStatus
    {
        Pending = 0,
        Moving = 1,
        Capturing = 2,
        Locating = 3,
        Completed = 4,
        Failed = 5
    }

    public sealed class CalibrationPointRecord
    {
        public int PointNumber { get; set; }
        public CalibrationSampleKind Kind { get; set; }
        public string Tag { get; set; }
        public CalibrationPointStatus Status { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetR { get; set; }
        public double MachineX { get; set; }
        public double MachineY { get; set; }
        public double MachineR { get; set; }
        public double ImageRow { get; set; }
        public double ImageColumn { get; set; }
        public double ImageAngle { get; set; }
        public double MatchScore { get; set; }
        public string Message { get; set; }
        public DateTime? CapturedAtUtc { get; set; }

        public CalibrationPointRecord DeepClone()
        {
            return (CalibrationPointRecord)MemberwiseClone();
        }
    }

    public sealed class CalibrationPointEventArgs : EventArgs
    {
        public CalibrationPointEventArgs(CalibrationPointRecord record)
        {
            Record = record == null ? null : record.DeepClone();
        }

        public CalibrationPointRecord Record { get; private set; }
    }

    public sealed class CalibrationResultEventArgs : EventArgs
    {
        public CalibrationResultEventArgs(CalibrationResult result)
        {
            Result = result == null ? null : result.DeepClone();
        }

        public CalibrationResult Result { get; private set; }
    }

    public sealed class FrameEventArgs : EventArgs, IDisposable
    {
        public FrameEventArgs(System.Drawing.Bitmap image, string source)
        {
            Image = image;
            Source = source;
            CapturedAtUtc = DateTime.UtcNow;
        }

        public System.Drawing.Bitmap Image { get; private set; }
        public string Source { get; private set; }
        public DateTime CapturedAtUtc { get; private set; }

        public void Dispose()
        {
            System.Drawing.Bitmap image = Image;
            Image = null;
            if (image != null) image.Dispose();
        }
    }

    public sealed class LogEventArgs : EventArgs
    {
        public LogEventArgs(string level, string message)
        {
            Level = level;
            Message = message;
            Timestamp = DateTime.Now;
        }

        public DateTime Timestamp { get; private set; }
        public string Level { get; private set; }
        public string Message { get; private set; }
    }

    public sealed class TeachingEventArgs : EventArgs
    {
        public TeachingEventArgs(TeachingPose pose)
        {
            if (pose == null) return;
            Pose = new TeachingPose
            {
                Side = pose.Side,
                IsValid = pose.IsValid,
                ImageRow = pose.ImageRow,
                ImageColumn = pose.ImageColumn,
                ImageAngleDegrees = pose.ImageAngleDegrees,
                MachineX = pose.MachineX,
                MachineY = pose.MachineY,
                MachineR = pose.MachineR,
                CapturedAtUtc = pose.CapturedAtUtc
            };
        }

        public TeachingPose Pose { get; private set; }
    }

    public sealed class InspectionEventArgs : EventArgs
    {
        public InspectionEventArgs(InspectionResult result)
        {
            Result = result;
        }

        public InspectionResult Result { get; private set; }
    }

    public sealed class RuntimeStateEventArgs : EventArgs
    {
        public RuntimeStateEventArgs(string component, bool available, string detail)
        {
            Component = component;
            Available = available;
            Detail = detail;
        }

        public string Component { get; private set; }
        public bool Available { get; private set; }
        public string Detail { get; private set; }
    }
}
