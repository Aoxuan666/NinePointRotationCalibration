using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MVSDK_Net;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;

namespace NinePointRotationCalibration.Station.Devices
{
    /// <summary>大华 IMV 相机 SDK 调用失败异常，包含失败操作名及 SDK 错误码。</summary>
    public sealed class CameraDeviceException : Exception
    {
        public CameraDeviceException(string operation, int errorCode)
            : base(BuildMessage(operation, errorCode))
        {
            Operation = operation;
            ErrorCode = errorCode;
        }

        public CameraDeviceException(string operation, int errorCode, Exception innerException)
            : base(BuildMessage(operation, errorCode), innerException)
        {
            Operation = operation;
            ErrorCode = errorCode;
        }

        public string Operation { get; private set; }
        public int ErrorCode { get; private set; }

        private static string BuildMessage(string operation, int errorCode)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Dahua camera operation '{0}' failed: {1} (0x{2:X8}).",
                operation,
                GetErrorName(errorCode),
                unchecked((uint)errorCode));
        }

        private static string GetErrorName(int errorCode)
        {
            switch (errorCode)
            {
                case IMVDefine.IMV_OK: return "OK";
                case IMVDefine.IMV_ERROR: return "General error";
                case IMVDefine.IMV_INVALID_HANDLE: return "Invalid handle";
                case IMVDefine.IMV_INVALID_PARAM: return "Invalid parameter";
                case IMVDefine.IMV_INVALID_FRAME_HANDLE: return "Invalid frame handle";
                case IMVDefine.IMV_INVALID_FRAME: return "Invalid frame";
                case IMVDefine.IMV_INVALID_RESOURCE: return "Invalid resource";
                case IMVDefine.IMV_INVALID_IP: return "Invalid IP address";
                case IMVDefine.IMV_NO_MEMORY: return "Out of memory";
                case IMVDefine.IMV_INSUFFICIENT_MEMORY: return "Insufficient buffer";
                case IMVDefine.IMV_ERROR_PROPERTY_TYPE: return "Feature type mismatch";
                case IMVDefine.IMV_INVALID_ACCESS: return "Access denied";
                case IMVDefine.IMV_INVALID_RANGE: return "Value outside feature range";
                case IMVDefine.IMV_NOT_SUPPORT: return "Operation not supported";
                default: return "SDK error";
            }
        }
    }

    /// <summary>基于大华 MVSDK_Net（IMV）的工业相机驱动，通过信号量串行化全部 SDK 调用并支持低延迟实时预览。</summary>
    public sealed class DahuaCameraDevice : ICameraDevice, ICameraParameterDevice, ICameraLivePreviewDevice
    {
        private const uint DriverBufferCount = 8;

        private readonly SemaphoreSlim _sdkGate = new SemaphoreSlim(1, 1);
        private readonly object _stateSync = new object();
        private readonly int _captureTimeoutMilliseconds;
        private MyCamera _camera;
        private bool _handleCreated;
        private bool _cameraOpened;
        private bool _streamStarted;
        private bool _isConnected;
        private string _connectedDeviceId;
        private string _cachedTriggerMode = "Software";
        private bool _disposeRequested;
        private bool _disposed;
        private IntPtr _conversionBuffer = IntPtr.Zero;
        private int _conversionBufferSize;
        private long _sequence;

        public DahuaCameraDevice()
            : this(3000)
        {
        }

        public DahuaCameraDevice(int captureTimeoutMilliseconds)
        {
            if (captureTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(captureTimeoutMilliseconds));

            _captureTimeoutMilliseconds = captureTimeoutMilliseconds;
        }

        public string ProviderName
        {
            get { return "Dahua IMV"; }
        }

        public bool IsConnected
        {
            get
            {
                lock (_stateSync)
                {
                    return _isConnected;
                }
            }
        }

        public string ConnectedDeviceId
        {
            get
            {
                lock (_stateSync)
                {
                    return _connectedDeviceId;
                }
            }
        }

        /// <summary>串行枚举当前在线的大华相机设备。</summary>
        public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () => EnumerateCore(cancellationToken),
                cancellationToken);
        }

        /// <summary>按指定 ID（或默认首台相机）打开句柄、配置软触发并开启取流。</summary>
        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () =>
                {
                    ConnectCore(deviceId, cancellationToken);
                    return true;
                },
                cancellationToken);
        }

        /// <summary>停止取流、关闭相机并销毁底层 SDK 句柄与转换缓存。</summary>
        public Task DisconnectAsync()
        {
            return RunSerializedAsync(
                () =>
                {
                    int cleanupError = ReleaseCameraCore();
                    if (cleanupError != IMVDefine.IMV_OK)
                    {
                        LocalDebugLog.Write(
                            "WARN",
                            "camera.dahua",
                            "Disconnect cleanup returned SDK code " + cleanupError + "; handle has been released.");
                    }
                    return true;
                },
                CancellationToken.None);
        }

        /// <summary>关闭自动曝光并在相机允许范围内设置曝光时间（微秒）。</summary>
        public Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
        {
            if (double.IsNaN(exposureMicroseconds) || double.IsInfinity(exposureMicroseconds) || exposureMicroseconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(exposureMicroseconds));

            return RunSerializedAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureConnectedCore();

                    double minimum = 0.0;
                    double maximum = 0.0;
                    int minimumResult = _camera.IMV_GetDoubleFeatureMin("ExposureTime", ref minimum);
                    int maximumResult = _camera.IMV_GetDoubleFeatureMax("ExposureTime", ref maximum);
                    if (minimumResult == IMVDefine.IMV_OK && maximumResult == IMVDefine.IMV_OK &&
                        (exposureMicroseconds < minimum || exposureMicroseconds > maximum))
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(exposureMicroseconds),
                            exposureMicroseconds,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Exposure must be between {0:0.###} and {1:0.###} microseconds.",
                                minimum,
                                maximum));
                    }

                    if (!_camera.IMV_FeatureIsWriteable("ExposureTime"))
                        throw new InvalidOperationException("当前曝光不可写，请检查相机的自动曝光设置。");

                    CheckResult(
                        _camera.IMV_SetDoubleFeatureValue("ExposureTime", exposureMicroseconds),
                        "Set exposure");
                    return true;
                },
                cancellationToken);
        }

        /// <summary>读取当前相机核心参数与常用可写 GenICam 特性。</summary>
        public Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureConnectedCore();
                    CameraParameterSet parameters = new CameraParameterSet();
                    parameters.ExposureMicroseconds = GetDoubleFeature("ExposureTime", double.NaN);
                    string gainNode = _camera.IMV_FeatureIsAvailable("Gain") ? "Gain" : "GainRaw";
                    CameraFeatureValue gain = TryReadFeature(gainNode);
                    double value;
                    parameters.Gain = gain != null && double.TryParse(gain.Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value) ? value : double.NaN;
                    parameters.TriggerMode = _cachedTriggerMode = GetTriggerMode();
                    return parameters;
                },
                cancellationToken);
        }

        /// <summary>按节点可用性与范围校验后批量下发相机参数。</summary>
        public Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (double.IsNaN(parameters.ExposureMicroseconds) || double.IsInfinity(parameters.ExposureMicroseconds) || parameters.ExposureMicroseconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(parameters.ExposureMicroseconds));

            return RunSerializedAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureConnectedCore();
                    SetDoubleFeatureIfAvailable("ExposureTime", parameters.ExposureMicroseconds);
                    if (!double.IsNaN(parameters.Gain) && !double.IsInfinity(parameters.Gain))
                    {
                        string gainNode = _camera.IMV_FeatureIsAvailable("Gain") ? "Gain" : "GainRaw";
                        SetDoubleFeatureIfAvailable(gainNode, parameters.Gain);
                    }
                    if (!double.IsNaN(parameters.Gamma) && !double.IsInfinity(parameters.Gamma) && parameters.Gamma > 0.0)
                    {
                        SetDoubleFeatureIfAvailable("Gamma", parameters.Gamma);
                    }
                    if (!double.IsNaN(parameters.Saturation) && !double.IsInfinity(parameters.Saturation) &&
                        parameters.Saturation >= 0.0 && parameters.Saturation <= long.MaxValue)
                    {
                        SetIntFeatureIfAvailable("Saturation", (long)Math.Round(parameters.Saturation, MidpointRounding.AwayFromZero));
                    }
                    SetBoolFeatureIfAvailable("ReverseX", parameters.ReverseX);
                    SetBoolFeatureIfAvailable("ReverseY", parameters.ReverseY);
                    if (!string.IsNullOrWhiteSpace(parameters.TriggerMode))
                    {
                        SetTriggerMode(parameters.TriggerMode);
                    }
                    if (parameters.Features != null)
                        foreach (CameraFeatureValue feature in parameters.Features)
                            ApplyFeature(feature);
                    _cachedTriggerMode = GetTriggerMode();
                    return true;
                },
                cancellationToken);
        }

        private double GetDoubleFeature(string name, double fallback)
        {
            if (!_camera.IMV_FeatureIsAvailable(name)) return fallback;
            IMVDefine.IMV_EFeatureType featureType = IMVDefine.IMV_EFeatureType.featureUndefined;
            if (_camera.IMV_GetFeatureType(name, ref featureType) &&
                featureType == IMVDefine.IMV_EFeatureType.featureInt)
            {
                long integerValue = 0;
                CheckResult(_camera.IMV_GetIntFeatureValue(name, ref integerValue), "Get " + name);
                return integerValue;
            }

            double value = fallback;
            CheckResult(_camera.IMV_GetDoubleFeatureValue(name, ref value), "Get " + name);
            return value;
        }

        private long GetIntFeature(string name, long fallback)
        {
            if (!_camera.IMV_FeatureIsAvailable(name)) return fallback;
            long value = fallback;
            CheckResult(_camera.IMV_GetIntFeatureValue(name, ref value), "Get " + name);
            return value;
        }

        private bool GetBoolFeature(string name)
        {
            if (!_camera.IMV_FeatureIsAvailable(name)) return false;
            bool value = false;
            CheckResult(_camera.IMV_GetBoolFeatureValue(name, ref value), "Get " + name);
            return value;
        }

        private List<CameraFeatureValue> ReadWritableFeatures()
        {
            string[] names =
            {
                "ExposureAuto", "GainAuto", "BalanceWhiteAuto", "AcquisitionFrameRateEnable",
                "ExposureTime", "GainRaw", "Gain", "Gamma", "Saturation", "BlackLevel", "BlackLevelRaw",
                "AcquisitionFrameRate", "OffsetX", "OffsetY", "Width", "Height",
                "BalanceRatioRed", "BalanceRatioGreen", "BalanceRatioBlue", "ReverseX", "ReverseY",
                "PixelFormat", "TriggerMode", "TriggerSource", "AcquisitionMode"
            };
            List<CameraFeatureValue> result = new List<CameraFeatureValue>();
            foreach (string name in names)
            {
                if (!_camera.IMV_FeatureIsAvailable(name) || !_camera.IMV_FeatureIsWriteable(name)) continue;
                CameraFeatureValue feature = TryReadFeature(name);
                if (feature != null) result.Add(feature);
            }
            return result;
        }

        private CameraFeatureValue TryReadFeature(string name)
        {
            IMVDefine.IMV_EFeatureType featureType = IMVDefine.IMV_EFeatureType.featureUndefined;
            if (!_camera.IMV_GetFeatureType(name, ref featureType)) return null;
            string type = featureType.ToString();
            CameraFeatureValue feature = new CameraFeatureValue
            {
                Name = name,
                DisplayName = GetFeatureDisplayName(name),
                IsWritable = true,
                Kind = type.IndexOf("Bool", StringComparison.OrdinalIgnoreCase) >= 0
                    ? CameraFeatureKind.Boolean
                    : type.IndexOf("Enum", StringComparison.OrdinalIgnoreCase) >= 0
                        ? CameraFeatureKind.Enumeration
                        : type.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0
                            ? CameraFeatureKind.Integer
                            : CameraFeatureKind.Number,
                Unit = GetFeatureUnit(name)
            };

            if (feature.Kind == CameraFeatureKind.Boolean)
            {
                bool value = false;
                if (_camera.IMV_GetBoolFeatureValue(name, ref value) != IMVDefine.IMV_OK) return null;
                feature.Value = value ? "true" : "false";
                return feature;
            }
            if (feature.Kind == CameraFeatureKind.Enumeration)
            {
                IMVDefine.IMV_String value = new IMVDefine.IMV_String();
                if (_camera.IMV_GetEnumFeatureSymbol(name, ref value) != IMVDefine.IMV_OK) return null;
                feature.Value = value.str;
                feature.EnumValues.Add(value.str);
                return feature;
            }
            if (feature.Kind == CameraFeatureKind.Integer)
            {
                long value = 0;
                if (_camera.IMV_GetIntFeatureValue(name, ref value) != IMVDefine.IMV_OK) return null;
                long minimum = 0;
                long maximum = 0;
                feature.HasRange = _camera.IMV_GetIntFeatureMin(name, ref minimum) == IMVDefine.IMV_OK &&
                                   _camera.IMV_GetIntFeatureMax(name, ref maximum) == IMVDefine.IMV_OK;
                feature.Minimum = minimum;
                feature.Maximum = maximum;
                feature.Increment = 1.0;
                feature.Value = value.ToString(CultureInfo.InvariantCulture);
                return feature;
            }

            double numericValue = 0.0;
            if (_camera.IMV_GetDoubleFeatureValue(name, ref numericValue) != IMVDefine.IMV_OK) return null;
            double numericMinimum = 0.0;
            double numericMaximum = 0.0;
            feature.HasRange = _camera.IMV_GetDoubleFeatureMin(name, ref numericMinimum) == IMVDefine.IMV_OK &&
                               _camera.IMV_GetDoubleFeatureMax(name, ref numericMaximum) == IMVDefine.IMV_OK;
            feature.Minimum = numericMinimum;
            feature.Maximum = numericMaximum;
            feature.Increment = 0.001;
            feature.Value = numericValue.ToString("0.########", CultureInfo.InvariantCulture);
            return feature;
        }

        private void ApplyFeature(CameraFeatureValue feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Name) ||
                !_camera.IMV_FeatureIsAvailable(feature.Name) || !_camera.IMV_FeatureIsWriteable(feature.Name)) return;
            if (feature.Kind == CameraFeatureKind.Boolean)
            {
                bool value;
                if (bool.TryParse(feature.Value, out value)) SetBoolFeatureIfAvailable(feature.Name, value);
                return;
            }
            if (feature.Kind == CameraFeatureKind.Enumeration)
            {
                SetEnumFeatureIfAvailable(feature.Name, feature.Value);
                return;
            }
            double valueAsDouble;
            if (!double.TryParse(feature.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out valueAsDouble) ||
                double.IsNaN(valueAsDouble) || double.IsInfinity(valueAsDouble)) return;
            if (feature.Kind == CameraFeatureKind.Integer)
            {
                double rounded = Math.Round(valueAsDouble, MidpointRounding.AwayFromZero);
                if (rounded < long.MinValue || rounded > long.MaxValue) return;
                SetIntFeatureIfAvailable(feature.Name, (long)rounded);
            }
            else
            {
                SetDoubleFeatureIfAvailable(feature.Name, valueAsDouble);
            }
        }

        private static string GetFeatureDisplayName(string name)
        {
            switch (name)
            {
                case "ExposureAuto": return "自动曝光";
                case "GainAuto": return "自动增益";
                case "BalanceWhiteAuto": return "自动白平衡";
                case "BalanceRatioRed": return "白平衡 R";
                case "BalanceRatioGreen": return "白平衡 G";
                case "BalanceRatioBlue": return "白平衡 B";
                case "AcquisitionFrameRate": return "采集帧率";
                case "BlackLevel":
                case "BlackLevelRaw": return "黑电平";
                case "OffsetX": return "ROI X 偏移";
                case "OffsetY": return "ROI Y 偏移";
                case "PixelFormat": return "像素格式";
                default: return name;
            }
        }

        private static string GetFeatureUnit(string name)
        {
            if (string.Equals(name, "ExposureTime", StringComparison.OrdinalIgnoreCase)) return "us";
            if (string.Equals(name, "AcquisitionFrameRate", StringComparison.OrdinalIgnoreCase)) return "fps";
            return string.Empty;
        }

        private void SetDoubleFeatureIfAvailable(string name, double value)
        {
            if (!_camera.IMV_FeatureIsAvailable(name) || !_camera.IMV_FeatureIsWriteable(name)) return;
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            IMVDefine.IMV_EFeatureType featureType = IMVDefine.IMV_EFeatureType.featureUndefined;
            if (_camera.IMV_GetFeatureType(name, ref featureType) &&
                featureType == IMVDefine.IMV_EFeatureType.featureInt)
            {
                if (value < long.MinValue || value > long.MaxValue) return;

                long integerValue = checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
                long minimum = long.MinValue;
                long maximum = long.MaxValue;
                bool hasMinimum = _camera.IMV_GetIntFeatureMin(name, ref minimum) == IMVDefine.IMV_OK;
                bool hasMaximum = _camera.IMV_GetIntFeatureMax(name, ref maximum) == IMVDefine.IMV_OK;
                if (!hasMinimum || !hasMaximum || integerValue < minimum || integerValue > maximum) return;

                CheckResult(_camera.IMV_SetIntFeatureValue(name, integerValue), "Set " + name);
                return;
            }

            if (featureType == IMVDefine.IMV_EFeatureType.featureFloat)
            {
                double minimum = 0.0;
                double maximum = 0.0;
                bool hasMinimum = _camera.IMV_GetDoubleFeatureMin(name, ref minimum) == IMVDefine.IMV_OK;
                bool hasMaximum = _camera.IMV_GetDoubleFeatureMax(name, ref maximum) == IMVDefine.IMV_OK;
                if (!hasMinimum || !hasMaximum || value < minimum || value > maximum) return;
            }

            // 部分大华相机型号虽暴露 GainRaw 节点但未提供可用浮点取值范围，此时保留相机当前/出厂增益值。
            if (string.Equals(name, "GainRaw", StringComparison.OrdinalIgnoreCase))
            {
                double current = 0.0;
                if (_camera.IMV_GetDoubleFeatureValue(name, ref current) != IMVDefine.IMV_OK)
                    return;
            }

            CheckResult(_camera.IMV_SetDoubleFeatureValue(name, value), "Set " + name);
        }

        private void SetIntFeatureIfAvailable(string name, long value)
        {
            if (!_camera.IMV_FeatureIsAvailable(name) || !_camera.IMV_FeatureIsWriteable(name)) return;
            long minimum = long.MinValue;
            long maximum = long.MaxValue;
            bool hasMinimum = _camera.IMV_GetIntFeatureMin(name, ref minimum) == IMVDefine.IMV_OK;
            bool hasMaximum = _camera.IMV_GetIntFeatureMax(name, ref maximum) == IMVDefine.IMV_OK;
            if (!hasMinimum || !hasMaximum || value < minimum || value > maximum) return;
            CheckResult(_camera.IMV_SetIntFeatureValue(name, value), "Set " + name);
        }

        private void SetBoolFeatureIfAvailable(string name, bool value)
        {
            if (_camera.IMV_FeatureIsAvailable(name) && _camera.IMV_FeatureIsWriteable(name))
                CheckResult(_camera.IMV_SetBoolFeatureValue(name, value), "Set " + name);
        }

        private string GetTriggerMode()
        {
            if (!_camera.IMV_FeatureIsAvailable("TriggerMode")) return "Continuous";
            IMVDefine.IMV_String mode = new IMVDefine.IMV_String();
            CheckResult(_camera.IMV_GetEnumFeatureSymbol("TriggerMode", ref mode), "Get TriggerMode");
            if (string.Equals(mode.str, "Off", StringComparison.OrdinalIgnoreCase)) return "Continuous";
            if (_camera.IMV_FeatureIsAvailable("TriggerSource"))
            {
                IMVDefine.IMV_String source = new IMVDefine.IMV_String();
                CheckResult(_camera.IMV_GetEnumFeatureSymbol("TriggerSource", ref source), "Get TriggerSource");
                return string.Equals(source.str, "Software", StringComparison.OrdinalIgnoreCase) ? "Software" : "Hardware";
            }
            return "Software";
        }

        private void SetTriggerMode(string triggerMode)
        {
            if (string.IsNullOrWhiteSpace(triggerMode) || !_camera.IMV_FeatureIsAvailable("TriggerMode")) return;
            if (string.Equals(triggerMode, "Continuous", StringComparison.OrdinalIgnoreCase))
            {
                CheckResult(_camera.IMV_SetEnumFeatureSymbol("TriggerMode", "Off"), "Set continuous trigger mode");
                _cachedTriggerMode = "Continuous";
                LocalDebugLog.Write("INFO", "camera.dahua", "TriggerMode set to Continuous (TriggerMode=Off).");
                return;
            }

            CheckResult(_camera.IMV_SetEnumFeatureSymbol("TriggerMode", "On"), "Enable trigger mode");
            bool hardware = string.Equals(triggerMode, "Hardware", StringComparison.OrdinalIgnoreCase);
            string source = hardware ? "Line1" : "Software";
            if (_camera.IMV_FeatureIsAvailable("TriggerSource"))
                CheckResult(_camera.IMV_SetEnumFeatureSymbol("TriggerSource", source), "Set trigger source");
            _cachedTriggerMode = hardware ? "Hardware" : "Software";
            LocalDebugLog.Write("INFO", "camera.dahua", "TriggerMode set to " + _cachedTriggerMode + " (source=" + source + ").");
        }

        /// <summary>触发并采集一帧图像（先清空驱动残帧以确保严格对应本次触发），转换为托管 Bitmap。</summary>
        public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () => CaptureCore(cancellationToken, false),
                cancellationToken);
        }

        /// <summary>按实时预览低延迟路径采集一帧图像（连续模式下直接取最新流帧，减少冗余 SDK 往返）。</summary>
        public Task<CameraFrame> CapturePreviewAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () => CaptureCore(cancellationToken, true),
                cancellationToken);
        }

        /// <summary>等待正在执行的 SDK 操作结束后释放相机句柄与转换缓冲区。</summary>
        public void Dispose()
        {
            lock (_stateSync)
            {
                if (_disposed || _disposeRequested) return;
                _disposeRequested = true;
            }

            _sdkGate.Wait();
            try
            {
                ReleaseCameraCore();
                lock (_stateSync)
                {
                    _disposed = true;
                }
            }
            finally
            {
                _sdkGate.Release();
            }
        }

        // 所有 IMV SDK 调用统一经 _sdkGate 串行执行，避免多线程并发访问非线程安全句柄
        private async Task<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _sdkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(operation).ConfigureAwait(false);
            }
            finally
            {
                _sdkGate.Release();
            }
        }

        private IReadOnlyList<CameraDescriptor> EnumerateCore(CancellationToken cancellationToken)
        {
            List<CameraDescriptor> descriptors = new List<CameraDescriptor>();
            IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
            int result = MyCamera.IMV_EnumDevices(
                ref deviceList,
                (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);
            CheckResult(result, "Enumerate devices");

            int structureSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
            for (uint index = 0; index < deviceList.nDevNum; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr address = IntPtr.Add(deviceList.pDevInfo, checked((int)index * structureSize));
                IMVDefine.IMV_DeviceInfo info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                    address,
                    typeof(IMVDefine.IMV_DeviceInfo));
                string ipAddress = TryGetIpAddress(info);
                descriptors.Add(new CameraDescriptor
                {
                    Id = GetStableDeviceId(info, ipAddress, index),
                    DisplayName = BuildDisplayName(info, ipAddress)
                });
            }

            return descriptors.AsReadOnly();
        }

        private void ConnectCore(string requestedDeviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int previousCleanupError = ReleaseCameraCore();
            if (previousCleanupError != IMVDefine.IMV_OK)
            {
                LocalDebugLog.Write(
                    "WARN",
                    "camera.dahua",
                    "Previous camera cleanup returned SDK code " + previousCleanupError + "; proceeding with reconnect.");
            }

            ResolvedDevice resolved = ResolveDevice(requestedDeviceId, cancellationToken);
            _camera = new MyCamera();
            try
            {
                int result = _camera.IMV_CreateHandle(resolved.Mode, resolved.Index, resolved.Identifier);
                CheckResult(result, "Create handle");
                _handleCreated = true;

                cancellationToken.ThrowIfCancellationRequested();
                CheckResult(_camera.IMV_Open(), "Open camera");
                _cameraOpened = true;

                CheckResult(_camera.IMV_SetBufferCount(DriverBufferCount), "Set stream buffer count");
                _cachedTriggerMode = GetTriggerMode();

                cancellationToken.ThrowIfCancellationRequested();
                CheckResult(_camera.IMV_StartGrabbing(), "Start grabbing");
                _streamStarted = true;

                lock (_stateSync)
                {
                    _connectedDeviceId = resolved.StableId;
                    _isConnected = true;
                }
                LocalDebugLog.Write("INFO", "camera.dahua", "Connected and grabbing started: deviceId='" + resolved.StableId + "', mode=" + resolved.Mode + ".");
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("ERROR", "camera.dahua", "ConnectCore failed for '" + requestedDeviceId + "'", exception);
                ReleaseCameraCore();
                throw;
            }
        }

        private CameraFrame CaptureCore(CancellationToken cancellationToken, bool isPreview)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnectedCore();

            bool softwareTrigger = isPreview
                ? string.Equals(_cachedTriggerMode, "Software", StringComparison.OrdinalIgnoreCase)
                : IsSoftwareTriggerEnabled();

            // 生产抓拍必须清空驱动残帧以确保严格对应本次触发；实时预览连续流跳过清缓冲以维持满帧率丝滑采集
            if (!isPreview || softwareTrigger)
            {
                int clearResult = _camera.IMV_ClearFrameBuffer();
                if (clearResult != IMVDefine.IMV_OK && clearResult != IMVDefine.IMV_NOT_SUPPORT)
                    throw new CameraDeviceException("Clear frame buffer", clearResult);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (softwareTrigger)
            {
                CheckResult(
                    _camera.IMV_ExecuteCommandFeature("TriggerSoftware"),
                    "Execute software trigger");
            }

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            int releaseResult = IMVDefine.IMV_OK;
            Bitmap bitmap = null;
            int timeoutMs = isPreview
                ? Math.Min(_captureTimeoutMilliseconds, string.Equals(_cachedTriggerMode, "Hardware", StringComparison.OrdinalIgnoreCase) ? 500 : 1200)
                : _captureTimeoutMilliseconds;
            int getFrameResult = _camera.IMV_GetFrame(
                ref frame,
                checked((uint)Math.Max(100, timeoutMs)));
            if (getFrameResult != IMVDefine.IMV_OK)
                throw new CameraDeviceException("Get frame (timeout " + timeoutMs + " ms)", getFrameResult);

            try
            {
                if (frame.frameInfo.status != 0)
                    throw new CameraDeviceException("Validate frame status", unchecked((int)frame.frameInfo.status));

                bitmap = ConvertFrameToBitmap(frame);
            }
            finally
            {
                // IMV_GetFrame 成功后无论转换成功与否，都必须成对调用 IMV_ReleaseFrame 归还驱动帧缓冲。
                try
                {
                    releaseResult = _camera.IMV_ReleaseFrame(ref frame);
                }
                catch
                {
                    releaseResult = IMVDefine.IMV_ERROR;
                }
            }

            if (releaseResult != IMVDefine.IMV_OK)
            {
                if (bitmap != null) bitmap.Dispose();
                throw new CameraDeviceException("Release frame", releaseResult);
            }

            // The preview loop owns its final frame even if stop was requested during IMV_GetFrame.
            if (!isPreview && cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            long sequence = ++_sequence;
            return new CameraFrame(bitmap, sequence, DateTime.UtcNow);
        }

        private bool IsSoftwareTriggerEnabled()
        {
            if (!_camera.IMV_FeatureIsAvailable("TriggerMode"))
            {
                _cachedTriggerMode = "Continuous";
                return false;
            }
            IMVDefine.IMV_String mode = new IMVDefine.IMV_String();
            CheckResult(_camera.IMV_GetEnumFeatureSymbol("TriggerMode", ref mode), "Get TriggerMode before capture");
            if (string.Equals(mode.str, "Off", StringComparison.OrdinalIgnoreCase))
            {
                _cachedTriggerMode = "Continuous";
                return false;
            }
            if (!_camera.IMV_FeatureIsAvailable("TriggerSource"))
            {
                _cachedTriggerMode = "Software";
                return true;
            }
            IMVDefine.IMV_String source = new IMVDefine.IMV_String();
            CheckResult(_camera.IMV_GetEnumFeatureSymbol("TriggerSource", ref source), "Get TriggerSource before capture");
            bool isSoftware = string.Equals(source.str, "Software", StringComparison.OrdinalIgnoreCase);
            _cachedTriggerMode = isSoftware ? "Software" : "Hardware";
            return isSoftware;
        }

        private Bitmap ConvertFrameToBitmap(IMVDefine.IMV_Frame frame)
        {
            int width = checked((int)frame.frameInfo.width);
            int height = checked((int)frame.frameInfo.height);
            if (width <= 0 || height <= 0 || frame.pData == IntPtr.Zero)
                throw new CameraDeviceException("Validate frame data", IMVDefine.IMV_INVALID_FRAME);

            // 统一由 SDK 执行 Bayer 去马赛克及像素格式转换，输出 Mono8 或 BGR8 供 Bitmap 拷贝。
            bool monochrome = IsMonoPixelFormat(frame.frameInfo.pixelFormat);
            int channels = monochrome ? 1 : 3;
            IMVDefine.IMV_EPixelType targetFormat = monochrome
                ? IMVDefine.IMV_EPixelType.gvspPixelMono8
                : IMVDefine.IMV_EPixelType.gvspPixelBGR8;
            IntPtr pixels = GetConvertedPixels(frame, targetFormat, channels);
            return CopyToBitmap(pixels, width, height, channels);
        }

        private IntPtr GetConvertedPixels(
            IMVDefine.IMV_Frame frame,
            IMVDefine.IMV_EPixelType targetFormat,
            int channelCount)
        {
            int width = checked((int)frame.frameInfo.width);
            int height = checked((int)frame.frameInfo.height);
            int requiredSize = GetRequiredBufferSize(width, height, channelCount);
            EnsureConversionBuffer(requiredSize);

            IMVDefine.IMV_PixelConvertParam convert = new IMVDefine.IMV_PixelConvertParam
            {
                nWidth = frame.frameInfo.width,
                nHeight = frame.frameInfo.height,
                ePixelFormat = frame.frameInfo.pixelFormat,
                pSrcData = frame.pData,
                nSrcDataLen = frame.frameInfo.size,
                nPaddingX = frame.frameInfo.paddingX,
                nPaddingY = frame.frameInfo.paddingY,
                eBayerDemosaic = IMVDefine.IMV_EBayerDemosaic.demosaicBilinear,
                eDstPixelFormat = targetFormat,
                pDstBuf = _conversionBuffer,
                nDstBufSize = checked((uint)requiredSize)
            };
            CheckResult(_camera.IMV_PixelConvert(ref convert), "Convert pixel format");
            if (convert.nDstDataLen != checked((uint)requiredSize))
                throw new CameraDeviceException("Validate converted frame length", IMVDefine.IMV_INSUFFICIENT_MEMORY);
            return _conversionBuffer;
        }

        private static unsafe Bitmap CopyToBitmap(IntPtr source, int width, int height, int channelCount)
        {
            PixelFormat pixelFormat = channelCount == 1
                ? PixelFormat.Format8bppIndexed
                : PixelFormat.Format24bppRgb;
            Bitmap bitmap = new Bitmap(width, height, pixelFormat);
            try
            {
                BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    pixelFormat);
                try
                {
                    int rowBytes = checked(width * channelCount);
                    // GDI+ Bitmap 每行按 4 字节对齐，直接按行拷贝 SDK 输出以省去中间托管行缓冲区。
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)source + checked(y * rowBytes),
                            (byte*)data.Scan0 + checked(y * data.Stride),
                            Math.Abs(data.Stride),
                            rowBytes);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }

            if (channelCount == 1)
            {
                ColorPalette palette = bitmap.Palette;
                for (int index = 0; index < palette.Entries.Length; index++)
                    palette.Entries[index] = Color.FromArgb(index, index, index);
                bitmap.Palette = palette;
            }

            return bitmap;
        }

        private void EnsureConversionBuffer(int requiredSize)
        {
            if (_conversionBuffer != IntPtr.Zero && _conversionBufferSize >= requiredSize) return;

            if (_conversionBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_conversionBuffer);
                _conversionBuffer = IntPtr.Zero;
                _conversionBufferSize = 0;
            }

            _conversionBuffer = Marshal.AllocHGlobal(requiredSize);
            if (_conversionBuffer == IntPtr.Zero)
                throw new OutOfMemoryException("Unable to allocate the Dahua pixel conversion buffer.");
            _conversionBufferSize = requiredSize;
        }

        // 按“停止取流 -> 关闭相机 -> 销毁句柄 -> 释放转换缓冲”逆序清理底层资源
        private int ReleaseCameraCore()
        {
            int firstError = IMVDefine.IMV_OK;
            MyCamera camera = _camera;
            if (camera != null)
            {
                if (_streamStarted)
                    RecordCleanupResult(TrySdkCall(camera.IMV_StopGrabbing), ref firstError);
                _streamStarted = false;

                if (_cameraOpened)
                    RecordCleanupResult(TrySdkCall(camera.IMV_Close), ref firstError);
                _cameraOpened = false;

                if (_handleCreated)
                    RecordCleanupResult(TrySdkCall(camera.IMV_DestroyHandle), ref firstError);
                _handleCreated = false;
            }

            _camera = null;
            if (_conversionBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_conversionBuffer);
                _conversionBuffer = IntPtr.Zero;
                _conversionBufferSize = 0;
            }

            lock (_stateSync)
            {
                _isConnected = false;
                _connectedDeviceId = null;
            }

            return firstError;
        }

        private ResolvedDevice ResolveDevice(string requestedDeviceId, CancellationToken cancellationToken)
        {
            string requested = string.IsNullOrWhiteSpace(requestedDeviceId)
                ? null
                : requestedDeviceId.Trim();
            IPAddress parsedAddress;
            if (requested != null && IPAddress.TryParse(requested, out parsedAddress) &&
                parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return new ResolvedDevice
                {
                    Mode = IMVDefine.IMV_ECreateHandleMode.modeByIPAddress,
                    Identifier = requested,
                    StableId = requested
                };
            }

            IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
            CheckResult(
                MyCamera.IMV_EnumDevices(
                    ref deviceList,
                    (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll),
                "Enumerate devices before connect");
            if (deviceList.nDevNum == 0)
                throw new InvalidOperationException("No Dahua camera was found.");

            int structureSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
            ResolvedDevice first = null;
            for (uint index = 0; index < deviceList.nDevNum; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr address = IntPtr.Add(deviceList.pDevInfo, checked((int)index * structureSize));
                IMVDefine.IMV_DeviceInfo info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                    address,
                    typeof(IMVDefine.IMV_DeviceInfo));
                string ipAddress = TryGetIpAddress(info);
                string stableId = GetStableDeviceId(info, ipAddress, index);
                ResolvedDevice candidate = CreateResolvedDevice(info, stableId, ipAddress, checked((int)index));
                if (first == null) first = candidate;

                if (requested == null ||
                    EqualsOrdinalIgnoreCase(requested, stableId) ||
                    EqualsOrdinalIgnoreCase(requested, info.cameraKey) ||
                    EqualsOrdinalIgnoreCase(requested, info.serialNumber) ||
                    EqualsOrdinalIgnoreCase(requested, info.cameraName) ||
                    EqualsOrdinalIgnoreCase(requested, ipAddress))
                {
                    return candidate;
                }
            }

            if (requested == null) return first;
            throw new InvalidOperationException("Dahua camera '" + requested + "' was not found.");
        }

        private static ResolvedDevice CreateResolvedDevice(
            IMVDefine.IMV_DeviceInfo info,
            string stableId,
            string ipAddress,
            int index)
        {
            if (!string.IsNullOrWhiteSpace(info.cameraKey))
            {
                return new ResolvedDevice
                {
                    Mode = IMVDefine.IMV_ECreateHandleMode.modeByCameraKey,
                    Identifier = info.cameraKey,
                    Index = index,
                    StableId = stableId
                };
            }

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                return new ResolvedDevice
                {
                    Mode = IMVDefine.IMV_ECreateHandleMode.modeByIPAddress,
                    Identifier = ipAddress,
                    Index = index,
                    StableId = stableId
                };
            }

            return new ResolvedDevice
            {
                Mode = IMVDefine.IMV_ECreateHandleMode.modeByIndex,
                Identifier = string.Empty,
                Index = index,
                StableId = stableId
            };
        }

        private static string GetStableDeviceId(IMVDefine.IMV_DeviceInfo info, string ipAddress, uint index)
        {
            if (!string.IsNullOrWhiteSpace(info.cameraKey)) return info.cameraKey;
            if (!string.IsNullOrWhiteSpace(info.serialNumber)) return info.serialNumber;
            if (!string.IsNullOrWhiteSpace(ipAddress)) return ipAddress;
            return "index:" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildDisplayName(IMVDefine.IMV_DeviceInfo info, string ipAddress)
        {
            List<string> parts = new List<string>();
            string name = !string.IsNullOrWhiteSpace(info.cameraName)
                ? info.cameraName
                : info.modelName;
            parts.Add(string.IsNullOrWhiteSpace(name) ? "Dahua Camera" : name);
            if (!string.IsNullOrWhiteSpace(info.serialNumber)) parts.Add("SN " + info.serialNumber);
            if (!string.IsNullOrWhiteSpace(ipAddress)) parts.Add(ipAddress);
            return string.Join(" | ", parts.ToArray());
        }

        private static string TryGetIpAddress(IMVDefine.IMV_DeviceInfo info)
        {
            if (info.nCameraType != IMVDefine.IMV_ECameraType.typeGigeCamera ||
                info.deviceSpecificInfo.gigeDeviceInfo == null)
                return null;

            byte[] bytes = info.deviceSpecificInfo.gigeDeviceInfo;
            int size = Marshal.SizeOf(typeof(IMVDefine.IMV_GigEDeviceInfo));
            if (bytes.Length < size) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, buffer, size);
                IMVDefine.IMV_GigEDeviceInfo gigE = (IMVDefine.IMV_GigEDeviceInfo)Marshal.PtrToStructure(
                    buffer,
                    typeof(IMVDefine.IMV_GigEDeviceInfo));
                return gigE.ipAddress;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void SetEnumFeatureIfAvailable(string featureName, string value)
        {
            if (_camera.IMV_FeatureIsAvailable(featureName))
                CheckResult(_camera.IMV_SetEnumFeatureSymbol(featureName, value), "Set " + featureName);
        }

        private void EnsureConnectedCore()
        {
            if (_camera == null || !_handleCreated || !_cameraOpened || !_streamStarted)
                throw new InvalidOperationException("The Dahua camera is not connected and streaming.");

            if (!_camera.IMV_IsOpen() || !_camera.IMV_IsGrabbing())
            {
                lock (_stateSync)
                {
                    _isConnected = false;
                }
                throw new InvalidOperationException("The Dahua camera connection or stream has been lost.");
            }
        }

        private static bool IsMonoPixelFormat(IMVDefine.IMV_EPixelType pixelType)
        {
            switch (pixelType)
            {
                case IMVDefine.IMV_EPixelType.gvspPixelMono1p:
                case IMVDefine.IMV_EPixelType.gvspPixelMono2p:
                case IMVDefine.IMV_EPixelType.gvspPixelMono4p:
                case IMVDefine.IMV_EPixelType.gvspPixelMono1e:
                case IMVDefine.IMV_EPixelType.gvspPixelMono1c:
                case IMVDefine.IMV_EPixelType.gvspPixelMono8:
                case IMVDefine.IMV_EPixelType.gvspPixelMono8S:
                case IMVDefine.IMV_EPixelType.gvspPixelMono10:
                case IMVDefine.IMV_EPixelType.gvspPixelMono10Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelMono12:
                case IMVDefine.IMV_EPixelType.gvspPixelMono12Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelMono14:
                case IMVDefine.IMV_EPixelType.gvspPixelMono16:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsColorPixelFormat(IMVDefine.IMV_EPixelType pixelType)
        {
            switch (pixelType)
            {
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR8:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG8:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB8:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG8:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR10:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG10:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB10:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG10:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR12:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG12:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB12:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG12:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR10Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG10Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB10Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG10Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR12Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG12Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB12Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG12Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG10p:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG12p:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGR16:
                case IMVDefine.IMV_EPixelType.gvspPixelBayRG16:
                case IMVDefine.IMV_EPixelType.gvspPixelBayGB16:
                case IMVDefine.IMV_EPixelType.gvspPixelBayBG16:
                case IMVDefine.IMV_EPixelType.gvspPixelYUV411_8_UYYVYY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr411_8_CbYYCrYY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr601_411_8_CbYYCrYY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr709_411_8_CbYYCrYY:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB8:
                case IMVDefine.IMV_EPixelType.gvspPixelBGR8:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB8Planar:
                case IMVDefine.IMV_EPixelType.gvspPixelRGBA8:
                case IMVDefine.IMV_EPixelType.gvspPixelBGRA8:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB565P:
                case IMVDefine.IMV_EPixelType.gvspPixelBGR565P:
                case IMVDefine.IMV_EPixelType.gvspPixelYUV422_8:
                case IMVDefine.IMV_EPixelType.gvspPixelYUV422_8_UYVY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr422_8:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr601_422_8:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr709_422_8:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr422_8_CbYCrY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr601_422_8_CbYCrY:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr709_422_8_CbYCrY:
                case IMVDefine.IMV_EPixelType.gvspPixelYUV8_UYV:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr8CbYCr:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr601_8_CbYCr:
                case IMVDefine.IMV_EPixelType.gvspPixelYCbCr709_8_CbYCr:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB10V1Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB10P32:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB12V1Packed:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB10:
                case IMVDefine.IMV_EPixelType.gvspPixelBGR10:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB12:
                case IMVDefine.IMV_EPixelType.gvspPixelBGR12:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB10Planar:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB12Planar:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB16Planar:
                case IMVDefine.IMV_EPixelType.gvspPixelRGB16:
                    return true;
                default:
                    return false;
            }
        }

        private static int GetRequiredBufferSize(int width, int height, int channelCount)
        {
            long required = (long)width * height * channelCount;
            if (required <= 0 || required > int.MaxValue)
                throw new InvalidOperationException("The camera frame is too large to allocate.");
            return (int)required;
        }

        private static void CheckResult(int result, string operation)
        {
            if (result != IMVDefine.IMV_OK)
                throw new CameraDeviceException(operation, result);
        }

        private static int TrySdkCall(Func<int> operation)
        {
            try
            {
                return operation();
            }
            catch
            {
                return IMVDefine.IMV_ERROR;
            }
        }

        private static void RecordCleanupResult(int result, ref int firstError)
        {
            if (firstError == IMVDefine.IMV_OK && result != IMVDefine.IMV_OK)
                firstError = result;
        }

        private static bool EqualsOrdinalIgnoreCase(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private void ThrowIfDisposed()
        {
            lock (_stateSync)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(DahuaCameraDevice));
            }
        }

        private sealed class ResolvedDevice
        {
            public IMVDefine.IMV_ECreateHandleMode Mode { get; set; }
            public int Index { get; set; }
            public string Identifier { get; set; }
            public string StableId { get; set; }
        }
    }
}
