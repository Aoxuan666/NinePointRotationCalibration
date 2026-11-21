using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
#if HIKVISION_SDK
using Seprays.Camera;
using MvsCamera = MvCamCtrl.NET.MyCamera;
#endif

namespace NinePointRotationCalibration.Station.Devices
{
    /// <summary>基于海康 MVS（Seprays.Camera）的工业相机驱动封装；未配置 SDK 时编译为占位实现。</summary>
    public sealed class HikvisionCameraDevice : ICameraDevice, ICameraParameterDevice
    {
#if HIKVISION_SDK
        private readonly SemaphoreSlim _sdkGate = new SemaphoreSlim(1, 1);
        private readonly object _stateSync = new object();
        private CameraMVS _camera;
        private volatile bool _disposed;
        private bool _reverseX;
        private bool _reverseY;
        private string _triggerMode = "Software";
        private string _connectedDeviceId;
        private long _sequence;

        public string ProviderName { get { return "Hikvision MVS"; } }
        public bool IsConnected { get { lock (_stateSync) return _camera != null && _camera.m_bIsOpen; } }
        public string ConnectedDeviceId { get { lock (_stateSync) return _connectedDeviceId; } }

        /// <summary>串行枚举当前在线的海康相机序列号列表。</summary>
        public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync<IReadOnlyList<CameraDescriptor>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CameraMVS enumerator = new CameraMVS();
                try
                {
                    int error = 0;
                    List<string> serialNumbers = enumerator.EnumCamrea(ref error);
                    EnsureSuccess(serialNumbers != null, error, "Enumerate cameras", enumerator);
                    List<CameraDescriptor> devices = new List<CameraDescriptor>();
                    foreach (string serialNumber in serialNumbers)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(serialNumber)) continue;
                        devices.Add(new CameraDescriptor
                        {
                            Id = serialNumber,
                            DisplayName = "Hikvision MVS / " + serialNumber
                        });
                    }
                    return devices.AsReadOnly();
                }
                finally
                {
                    // Seprays.Camera 枚举时会持有非托管资源，使用完毕需显式释放
                    enumerator.FreeHGlobal();
                }
            }, cancellationToken);
        }

        /// <summary>按序列号连接并读取设备的触发模式。</summary>
        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A camera serial number is required.", nameof(deviceId));
            return RunSerializedAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DisconnectCore();
                CameraMVS camera = new CameraMVS();
                try
                {
                    int error = 0;
                    EnsureSuccess(camera.Open(deviceId.Trim(), ref error), error, "Open camera", camera);
                    string mode = ReadTriggerMode(camera);
                    lock (_stateSync)
                    {
                        _camera = camera;
                        _connectedDeviceId = deviceId.Trim();
                        _triggerMode = mode;
                        _reverseX = false;
                        _reverseY = false;
                    }
                    LocalDebugLog.Write("INFO", "camera.hikvision", "Connected Hikvision camera: serial='" + deviceId.Trim() + "'.");
                    return true;
                }
                catch (Exception exception)
                {
                    LocalDebugLog.WriteException("ERROR", "camera.hikvision", "Failed to open Hikvision camera '" + deviceId + "'", exception);
                    try { camera.Close(); } catch { }
                    try { camera.FreeHGlobal(); } catch { }
                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>断开相机并释放 Seprays.Camera 非托管句柄。</summary>
        public Task DisconnectAsync()
        {
            return RunSerializedAsync(() => { DisconnectCore(); return true; }, CancellationToken.None);
        }

        /// <summary>设置相机曝光时间（微秒）。</summary>
        public Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
        {
            if (double.IsNaN(exposureMicroseconds) || double.IsInfinity(exposureMicroseconds) || exposureMicroseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(exposureMicroseconds));
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                int error = 0;
                EnsureSuccess(camera.SetExposureTime((float)exposureMicroseconds, ref error), error, "Set exposure", camera);
                return true;
            }, cancellationToken);
        }

        /// <summary>读取当前海康相机参数并映射为通用参数集。</summary>
        public Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                int error = 0;
                float exposure = 0;
                EnsureSuccess(camera.GetExposureTime(ref exposure, ref error), error, "Get exposure", camera);
                MvsCamera.MVCC_FLOATVALUE gain = new MvsCamera.MVCC_FLOATVALUE();
                int gainResult = GetMvsHandle(camera).MV_CC_GetFloatValue_NET("Gain", ref gain);
                string triggerMode = ReadTriggerMode(camera);
                lock (_stateSync) _triggerMode = triggerMode;
                return new CameraParameterSet
                {
                    ExposureMicroseconds = exposure,
                    Gain = gainResult == 0 ? gain.fCurValue : double.NaN,
                    TriggerMode = triggerMode
                };
            }, cancellationToken);
        }

        /// <summary>将通用参数集批量写入当前海康相机。</summary>
        public Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (double.IsNaN(parameters.ExposureMicroseconds) || double.IsInfinity(parameters.ExposureMicroseconds) ||
                parameters.ExposureMicroseconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(parameters.ExposureMicroseconds));
            if (!double.IsNaN(parameters.Saturation) &&
                (double.IsInfinity(parameters.Saturation) || parameters.Saturation < 0 || parameters.Saturation > uint.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(parameters.Saturation));
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                int error = 0;
                EnsureSuccess(camera.SetExposureTime((float)parameters.ExposureMicroseconds, ref error), error, "Set exposure", camera);
                if (!double.IsNaN(parameters.Gain) && !double.IsInfinity(parameters.Gain))
                {
                    if (!TrySetOptionalFloat(camera, new[] { "SetGain", "SetGainValue" }, (float)parameters.Gain))
                    {
                        try { GetMvsHandle(camera).MV_CC_SetFloatValue_NET("Gain", (float)parameters.Gain); }
                        catch { }
                    }
                }
                if (!double.IsNaN(parameters.Gamma) && !double.IsInfinity(parameters.Gamma) && parameters.Gamma > 0.0)
                {
                    // 先开启 Gamma 使能再写入 Gamma 值；黑白或不支持 Gamma 节点时容错跳过以免阻断后续配置。
                    error = 0;
                    if (camera.SetGammaEnable(true, ref error))
                    {
                        error = 0;
                        camera.SetGamma((float)parameters.Gamma, ref error);
                    }
                }
                if (!double.IsNaN(parameters.Saturation) && parameters.Saturation > 0.0)
                {
                    // 黑白工业相机通常不含 Saturation 节点，按可选特性写入避免导致整组参数下发失败。
                    error = 0;
                    camera.SetSaturation((uint)parameters.Saturation, ref error);
                }
                error = 0;
                EnsureSuccess(camera.SetReverseX(parameters.ReverseX, ref error), error, "Set reverse X", camera);
                error = 0;
                EnsureSuccess(camera.SetReverseY(parameters.ReverseY, ref error), error, "Set reverse Y", camera);
                if (!string.IsNullOrWhiteSpace(parameters.TriggerMode))
                {
                    SetTriggerModeCore(camera, parameters.TriggerMode);
                }
                lock (_stateSync)
                {
                    if (!string.IsNullOrWhiteSpace(parameters.TriggerMode))
                        _triggerMode = NormalizeTriggerMode(parameters.TriggerMode);
                    _reverseX = parameters.ReverseX;
                    _reverseY = parameters.ReverseY;
                }
                return true;
            }, cancellationToken);
        }

        private static void AddFeature(CameraParameterSet parameters, string name, string displayName, CameraFeatureKind kind, string value, string unit)
        {
            parameters.Features.Add(new CameraFeatureValue
            {
                Name = name,
                DisplayName = displayName,
                Kind = kind,
                Value = value,
                IsWritable = true,
                Unit = unit
            });
        }

        // 通过反射探测可选增益读写方法，以兼容不同版本的 Seprays.Camera 封装
        private static bool TryGetOptionalFloat(CameraMVS camera, string[] methodNames, out float value)
        {
            value = 0;
            foreach (string methodName in methodNames)
            {
                MethodInfo method = camera.GetType().GetMethod(methodName);
                if (method == null) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || !parameters[0].ParameterType.IsByRef) continue;
                object[] args = { 0.0f, 0 };
                try
                {
                    object result = method.Invoke(camera, args);
                    if (result is bool && !(bool)result) continue;
                    value = Convert.ToSingle(args[0], CultureInfo.InvariantCulture);
                    return true;
                }
                catch (TargetInvocationException) { }
                catch (ArgumentException) { }
            }
            return false;
        }

        private static bool TrySetOptionalFloat(CameraMVS camera, string[] methodNames, float value)
        {
            foreach (string methodName in methodNames)
            {
                MethodInfo method;
                try { method = camera.GetType().GetMethod(methodName); }
                catch (AmbiguousMatchException) { continue; }
                if (method == null) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || !parameters[1].ParameterType.IsByRef) continue;
                object[] args = { value, 0 };
                try
                {
                    object result = method.Invoke(camera, args);
                    if (result is bool && !(bool)result) continue;
                    return true;
                }
                catch (TargetInvocationException) { }
                catch (ArgumentException) { }
            }
            return false;
        }

        /// <summary>触发（软触发模式下发软触发命令）并抓取一帧图像。</summary>
        public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                cancellationToken.ThrowIfCancellationRequested();
                string triggerMode;
                lock (_stateSync) triggerMode = _triggerMode;
                // 软触发模式下需先执行一次 SoftTriggerOnce，再调用 GrabOneImage 取图
                if (string.Equals(triggerMode, "Software", StringComparison.OrdinalIgnoreCase))
                {
                    int error = 0;
                    EnsureSuccess(camera.SoftTriggerOnce(ref error), error, "Software trigger", camera);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Bitmap image = null;
                int captureError = 0;
                bool captured = camera.GrabOneImage(out image, ref captureError);
                if (!captured || image == null)
                {
                    if (image != null) image.Dispose();
                    throw CreateDeviceException("Grab image", captureError, camera);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    image.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new CameraFrame(image, Interlocked.Increment(ref _sequence), DateTime.UtcNow);
            }, cancellationToken);
        }

        /// <summary>串行关闭海康相机并释放非托管资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _sdkGate.Wait();
            try
            {
                if (_disposed) return;
                DisconnectCore();
                _disposed = true;
            }
            finally { _sdkGate.Release(); }
        }

        private async Task<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HikvisionCameraDevice));
            await _sdkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HikvisionCameraDevice));
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally { _sdkGate.Release(); }
        }

        private CameraMVS GetConnectedCamera()
        {
            lock (_stateSync)
            {
                if (_camera == null || !_camera.m_bIsOpen)
                    throw new InvalidOperationException("The Hikvision camera is not connected.");
                return _camera;
            }
        }

        private void DisconnectCore()
        {
            CameraMVS camera;
            string previousId;
            lock (_stateSync)
            {
                camera = _camera;
                previousId = _connectedDeviceId;
                _camera = null;
                _connectedDeviceId = null;
            }
            if (camera == null) return;
            try
            {
                try { camera.Close(); }
                catch (Exception ex) { LocalDebugLog.WriteException("WARN", "camera.hikvision", "Close failed during disconnect", ex); }
            }
            finally
            {
                try { camera.FreeHGlobal(); }
                catch (Exception ex) { LocalDebugLog.WriteException("WARN", "camera.hikvision", "FreeHGlobal failed during disconnect", ex); }
            }
            LocalDebugLog.Write("INFO", "camera.hikvision", "Disconnected Hikvision camera: serial='" + previousId + "'.");
        }

        // Seprays.Camera 2.0 exposes no trigger/gain getters. Use the already-open SDK handle.
        private static MvsCamera GetMvsHandle(CameraMVS camera)
        {
            FieldInfo field = typeof(CameraMVS).GetField("camera", BindingFlags.Instance | BindingFlags.NonPublic);
            MvsCamera handle = field == null ? null : field.GetValue(camera) as MvsCamera;
            if (handle == null) throw new NotSupportedException("当前海康相机组件无法读取 MVS 设备节点。");
            return handle;
        }

        private static string ReadTriggerMode(CameraMVS camera)
        {
            MvsCamera handle = GetMvsHandle(camera);
            MvsCamera.MVCC_ENUMVALUE value = new MvsCamera.MVCC_ENUMVALUE();
            if (handle.MV_CC_GetEnumValue_NET("TriggerMode", ref value) != 0) return "Unknown";
            if (value.nCurValue == (uint)MvsCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF) return "Continuous";
            if (handle.MV_CC_GetEnumValue_NET("TriggerSource", ref value) != 0) return "Unknown";
            return value.nCurValue == (uint)MvsCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE
                ? "Software" : "Hardware";
        }

        private static void SetTriggerModeCore(CameraMVS camera, string mode)
        {
            string normalized = NormalizeTriggerMode(mode);
            TriggerMode sdkMode = string.Equals(normalized, "Continuous", StringComparison.OrdinalIgnoreCase)
                ? TriggerMode.Off
                : string.Equals(normalized, "Hardware", StringComparison.OrdinalIgnoreCase)
                    ? TriggerMode.HardTrigger
                    : TriggerMode.SoftwareTrigger;
            int error = 0;
            EnsureSuccess(camera.SetTriggerMode(sdkMode, ref error), error, "Set trigger mode", camera);
        }

        private static string NormalizeTriggerMode(string mode)
        {
            if (string.Equals(mode, "Continuous", StringComparison.OrdinalIgnoreCase)) return "Continuous";
            if (string.Equals(mode, "Hardware", StringComparison.OrdinalIgnoreCase)) return "Hardware";
            return "Software";
        }

        private static void EnsureSuccess(bool success, int error, string operation, CameraMVS camera)
        {
            if (!success) throw CreateDeviceException(operation, error, camera);
        }

        private static Exception CreateDeviceException(string operation, int error, CameraMVS camera)
        {
            string detail;
            try { detail = camera.GetErrDescription(error); }
            catch { detail = "Unknown SDK error"; }
            return new InvalidOperationException(
                "Hikvision MVS operation '" + operation + "' failed: " + detail + " (0x" + error.ToString("X8") + ").");
        }
#else
        public string ProviderName { get { return "Hikvision MVS (SDK unavailable)"; } }
        public bool IsConnected { get { return false; } }
        public string ConnectedDeviceId { get { return null; } }
        public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public Task ConnectAsync(string id, CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public Task DisconnectAsync() { return Task.CompletedTask; }
        public Task SetExposureAsync(double exposure, CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public Task<CameraParameterSet> GetParametersAsync(CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public Task SetParametersAsync(CameraParameterSet parameters, CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public Task<CameraFrame> CaptureAsync(CancellationToken token) { throw new NotSupportedException("Hikvision MVS SDK files are missing."); }
        public void Dispose() { }
#endif
    }
}
