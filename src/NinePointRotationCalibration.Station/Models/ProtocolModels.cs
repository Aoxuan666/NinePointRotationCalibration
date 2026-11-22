using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>上位机 TCP 通信协议消息类型常量。</summary>
    public static class ProtocolMessageTypes
    {
        public const string Hello = "system.hello";
        public const string Heartbeat = "system.heartbeat";
        public const string Acknowledge = "system.ack";
        public const string MoveRequest = "calibration.move.request";
        public const string MotionArrived = "motion.arrived";
        public const string CalibrationPointResult = "calibration.point.result";
        public const string CalibrationCompleted = "calibration.completed";
        public const string InspectionTrigger = "inspection.trigger";
        public const string InspectionResult = "inspection.result";
        public const string Error = "system.error";
    }

    /// <summary>通信协议错误码常量。</summary>
    public static class ProtocolErrorCodes
    {
        public const string CameraDisabled = "CAMERA_DISABLED";
        public const string CameraNotConnected = "CAMERA_NOT_CONNECTED";
        public const string CalibrationDisabled = "CALIBRATION_DISABLED";
        public const string InspectionDisabled = "INSPECTION_DISABLED";
    }

    /// <summary>TCP 协议通用 JSON 消息信封。</summary>
    public sealed class ProtocolEnvelope
    {
        public ProtocolEnvelope()
        {
            Version = "1.0";
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

    /// <summary>上位机运动到位回调解析后的机台坐标数据。</summary>
    public sealed class MotionArrival
    {
        public string CorrelationId { get; set; }
        public int PointNumber { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
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
            return data != null && data.TryGetValue(key, out value) && value != null &&
                double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }
}
