using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinePointRotationCalibration.Station.Models
{
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

    public static class ProtocolErrorCodes
    {
        public const string CameraDisabled = "CAMERA_DISABLED";
        public const string CalibrationDisabled = "CALIBRATION_DISABLED";
    }

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
        public string CorrelationId { get; set; }
        public string Type { get; set; }
        public string TimestampUtc { get; set; }
        public Dictionary<string, object> Data { get; set; }

        public static ProtocolEnvelope Create(string type)
        {
            return new ProtocolEnvelope { Type = type };
        }
    }

    public sealed class MotionArrival
    {
        public string CorrelationId { get; set; }
        public int PointNumber { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
    }

    internal static class ProtocolValue
    {
        public static string String(IDictionary<string, object> data, string key, string fallback = "")
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : fallback;
        }

        public static int Integer(IDictionary<string, object> data, string key, int fallback = 0)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            int result;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        public static double Double(IDictionary<string, object> data, string key, double fallback = 0.0)
        {
            double result;
            return TryDouble(data, key, out result) ? result : fallback;
        }

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
