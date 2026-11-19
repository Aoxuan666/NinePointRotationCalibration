using System;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>单个标定点的执行状态。</summary>
    public enum CalibrationPointStatus
    {
        Pending = 0,
        Moving = 1,
        Capturing = 2,
        Locating = 3,
        Completed = 4,
        Failed = 5
    }

    /// <summary>标定点的计划偏移、实测机台坐标与图像匹配结果记录。</summary>
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

        /// <summary>创建点位记录的独立副本用于跨线程快照传递。</summary>
        public CalibrationPointRecord DeepClone()
        {
            return (CalibrationPointRecord)MemberwiseClone();
        }
    }

    /// <summary>标定点状态变更事件参数。</summary>
    public sealed class CalibrationPointEventArgs : EventArgs
    {
        public CalibrationPointEventArgs(CalibrationPointRecord record)
        {
            Record = record == null ? null : record.DeepClone();
        }

        public CalibrationPointRecord Record { get; private set; }
    }

    /// <summary>标定计算完成事件参数。</summary>
    public sealed class CalibrationResultEventArgs : EventArgs
    {
        public CalibrationResultEventArgs(CalibrationResult result)
        {
            Result = result == null ? null : result.DeepClone();
        }

        public CalibrationResult Result { get; private set; }
    }

    /// <summary>图像帧推送事件参数（可选持有位图所有权并在 Dispose 时释放）。</summary>
    public sealed class FrameEventArgs : EventArgs, IDisposable
    {
        private readonly bool _ownsImage;

        public FrameEventArgs(System.Drawing.Bitmap image, string source)
            : this(image, source, true)
        {
        }

        public FrameEventArgs(System.Drawing.Bitmap image, string source, bool ownsImage)
        {
            Image = image;
            Source = source;
            _ownsImage = ownsImage;
            CapturedAtUtc = DateTime.UtcNow;
        }

        public System.Drawing.Bitmap Image { get; private set; }
        public string Source { get; private set; }
        public DateTime CapturedAtUtc { get; private set; }

        /// <summary>若当前事件参数持有位图所有权，则释放底层位图资源。</summary>
        public void Dispose()
        {
            System.Drawing.Bitmap image = Image;
            Image = null;
            if (_ownsImage && image != null) image.Dispose();
        }
    }

    /// <summary>工作站运行日志事件参数。</summary>
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

    /// <summary>吸嘴示教位姿更新事件参数。</summary>
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

    /// <summary>整盘视觉检测完成事件参数。</summary>
    public sealed class InspectionEventArgs : EventArgs
    {
        public InspectionEventArgs(InspectionResult result)
        {
            Result = result;
        }

        public InspectionResult Result { get; private set; }
    }

    /// <summary>运行时组件可用性状态变更事件参数。</summary>
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
