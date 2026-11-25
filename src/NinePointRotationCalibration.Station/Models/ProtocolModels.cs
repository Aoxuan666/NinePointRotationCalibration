using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>上位机 TCP 通信协议消息类型常量（v1.1）。</summary>
    public static class ProtocolMessageTypes
    {
        public const string Hello = "system.hello";
        public const string Heartbeat = "system.heartbeat";
        public const string Acknowledge = "system.ack";
        public const string SystemStatus = "system.status";
        public const string CalibrationStarted = "calibration.started";
        public const string CalibrationStartedAck = "calibration.started.ack";
        public const string MoveRequest = "calibration.move.request";
        public const string MotionArrived = "motion.arrived";
        public const string MotionFailed = "motion.failed";
        public const string CalibrationCancel = "calibration.cancel";
        public const string CalibrationPointResult = "calibration.point.result";
        public const string CalibrationCompleted = "calibration.completed";
        public const string InspectionTrigger = "inspection.trigger";
        public const string InspectionResult = "inspection.result";
        public const string TeachingSubmit = "teaching.submit";
        public const string TeachingResult = "teaching.result";
        public const string TeachingPoseRequest = "teaching.pose.request";
        public const string TeachingPoseResponse = "teaching.pose.response";
        public const string Error = "system.error";
    }

    /// <summary>通信协议错误码常量（全协议统一 UPPER_SNAKE_CASE）。</summary>
    public static class ProtocolErrorCodes
    {
        public const string None = "NONE";

        // system.error
        public const string ProtocolInvalid = "PROTOCOL_INVALID";
        public const string InspectionDisabled = "INSPECTION_DISABLED";
        public const string CameraDisabled = "CAMERA_DISABLED";
        public const string CameraNotConnected = "CAMERA_NOT_CONNECTED";
        public const string CalibrationDisabled = "CALIBRATION_DISABLED";
        public const string Busy = "BUSY";
        public const string DuplicateMessage = "DUPLICATE_MESSAGE";
        public const string MessageTypeUnsupported = "MESSAGE_TYPE_UNSUPPORTED";
        public const string InternalError = "INTERNAL_ERROR";

        // inspection.result
        public const string NoMaterial = "NO_MATERIAL";
        public const string CoordinateNotMapped = "COORDINATE_NOT_MAPPED";
        public const string CoordinateMappingFailed = "COORDINATE_MAPPING_FAILED";
        public const string ManualMarkFound = "MANUAL_MARK_FOUND";
        public const string BoardCountMismatch = "BOARD_COUNT_MISMATCH";
        public const string PositioningTemplateMissing = "POSITIONING_TEMPLATE_MISSING";

        // teaching.result
        public const string TeachingSaveFailed = "TEACHING_SAVE_FAILED";

        // calibration.completed
        public const string MotionFailed = "MOTION_FAILED";
        public const string Cancelled = "CANCELLED";
        public const string MotionTimeout = "MOTION_TIMEOUT";
        public const string SessionRejected = "SESSION_REJECTED";
        public const string TemplateMissing = "TEMPLATE_MISSING";
        public const string CameraCaptureFailed = "CAMERA_CAPTURE_FAILED";
        public const string MatchFailed = "MATCH_FAILED";
        public const string CalibrationSolveFailed = "CALIBRATION_SOLVE_FAILED";
        public const string Disconnected = "DISCONNECTED";
    }

    /// <summary>上位机 motion.failed 标准错误码常量。</summary>
    public static class MotionFailedCodes
    {
        public const string SoftLimit = "SOFT_LIMIT";
        public const string EmergencyStop = "EMERGENCY_STOP";
        public const string ServoAlarm = "SERVO_ALARM";
        public const string NotInCalibrationMode = "NOT_IN_CALIBRATION_MODE";
        public const string SessionMismatch = "SESSION_MISMATCH";
        public const string SettleTimeout = "SETTLE_TIMEOUT";
    }

    /// <summary>双吸嘴与独立 R 轴协议标识映射辅助类。</summary>
    public static class ProtocolNozzle
    {
        public static string GetNozzleName(TeachingSide side)
        {
            return side == TeachingSide.Right ? "Right" : "Left";
        }

        public static string GetAxisR(TeachingSide side)
        {
            return side == TeachingSide.Right ? "R2" : "R1";
        }

        public static string GetDisplayName(TeachingSide side)
        {
            return side == TeachingSide.Right ? "右吸嘴 (Right / R2)" : "左吸嘴 (Left / R1)";
        }

        public static bool TryParse(string value, out TeachingSide side)
        {
            if (string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "R2", StringComparison.OrdinalIgnoreCase))
            {
                side = TeachingSide.Right;
                return true;
            }
            if (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "R1", StringComparison.OrdinalIgnoreCase))
            {
                side = TeachingSide.Left;
                return true;
            }
            side = TeachingSide.Left;
            return false;
        }
    }

    /// <summary>协议数值精度统一舍入辅助类（机械坐标/角度 3 位小数，统计量按约定保留）。</summary>
    public static class ProtocolNumber
    {
        public static double Round(double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            return rounded == 0.0 ? 0.0 : rounded;
        }

        public static double Round1(double value) { return Round(value, 1); }
        public static double Round2(double value) { return Round(value, 2); }
        public static double Round3(double value) { return Round(value, 3); }
        public static double Round6(double value) { return Round(value, 6); }
    }

    /// <summary>TCP 协议通用 JSON 消息信封。</summary>
    public sealed class ProtocolEnvelope
    {
        public const string CurrentVersion = "1.1";

        public ProtocolEnvelope()
        {
            Version = CurrentVersion;
            MessageId = Guid.NewGuid().ToString("N");
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public string Version { get; set; }
        public string MessageId { get; set; }
        /// <summary>关联请求消息的唯一 ID，用于异步请求与响应配对。</summary>
        public string CorrelationId { get; set; }
        public string Type { get; set; }
        public string TimestampUtc { get; set; }
        public Dictionary<string, object> Data { get; set; }

        /// <summary>创建指定消息类型的协议信封。</summary>
        public static ProtocolEnvelope Create(string type)
        {
            return new ProtocolEnvelope { Type = type };
        }
    }

    /// <summary>上位机对 calibration.started 的会话确认及锁存原点数据。</summary>
    public sealed class CalibrationStartAck
    {
        public string MessageId { get; set; }
        public string CorrelationId { get; set; }
        public string SessionId { get; set; }
        public bool Accepted { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginR { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>上位机运动到位回调解析后的机台坐标数据。</summary>
    public sealed class MotionArrival
    {
        public string CorrelationId { get; set; }
        public string SessionId { get; set; }
        public int PointNumber { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
    }

    /// <summary>上位机运动拒绝或异常回调解析后的失败数据。</summary>
    public sealed class MotionFailure
    {
        public string MessageId { get; set; }
        public string CorrelationId { get; set; }
        public string SessionId { get; set; }
        public int PointNumber { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    /// <summary>上位机对 teaching.pose.request 的实时机械轴坐标应答数据。</summary>
    public sealed class TeachingPoseResponse
    {
        public string MessageId { get; set; }
        public string CorrelationId { get; set; }
        public TeachingSide Side { get; set; }
        public bool Success { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
        public string Message { get; set; }
    }

    /// <summary>上位机主动中止标定会话请求解析后的数据。</summary>
    public sealed class CalibrationCancelRequest
    {
        public string MessageId { get; set; }
        public string SessionId { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>从协议字典负载中安全提取基础类型值的辅助类。</summary>
    internal static class ProtocolValue
    {
        /// <summary>安全读取字符串字段，缺失或为空时返回默认值。</summary>
        public static string String(IDictionary<string, object> data, string key, string fallback = "")
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : fallback;
        }

        /// <summary>尝试解析布尔字段。</summary>
        public static bool TryBool(IDictionary<string, object> data, string key, out bool result)
        {
            result = false;
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return false;
            if (value is bool)
            {
                result = (bool)value;
                return true;
            }
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result);
        }

        /// <summary>使用不变区域格式解析整数字段，失败时返回默认值。</summary>
        public static int Integer(IDictionary<string, object> data, string key, int fallback = 0)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            int result;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        /// <summary>使用不变区域格式解析浮点字段，失败时返回默认值。</summary>
        public static double Double(IDictionary<string, object> data, string key, double fallback = 0.0)
        {
            double result;
            return TryDouble(data, key, out result) ? result : fallback;
        }

        /// <summary>尝试以不变区域格式解析浮点字段。</summary>
        public static bool TryDouble(IDictionary<string, object> data, string key, out double result)
        {
            result = 0.0;
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return false;
            return double.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }
    }
}
