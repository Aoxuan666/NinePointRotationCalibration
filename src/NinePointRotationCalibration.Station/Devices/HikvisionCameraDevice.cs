using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;
#if HIKVISION_SDK
using Seprays.Camera;
#endif

namespace NinePointRotationCalibration.Station.Devices
{
    public sealed class HikvisionCameraDevice : ICameraDevice, ICameraParameterDevice
    {
#if HIKVISION_SDK
        private readonly SemaphoreSlim _sdkGate = new SemaphoreSlim(1, 1);
        private readonly object _stateSync = new object();
        private CameraMVS _camera;
        private bool _disposed;
        private bool _reverseX;
        private bool _reverseY;
        private string _triggerMode = "Software";
        private string _connectedDeviceId;
        private long _sequence;

        public string ProviderName { get { return "Hikvision MVS"; } }
        public bool IsConnected { get { lock (_stateSync) return _camera != null && _camera.m_bIsOpen; } }
        public string ConnectedDeviceId { get { lock (_stateSync) return _connectedDeviceId; } }

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
                    enumerator.FreeHGlobal();
                }
            }, cancellationToken);
        }

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
                    SetTriggerModeCore(camera, "Software");
                    lock (_stateSync)
                    {
                        _camera = camera;
                        _connectedDeviceId = deviceId.Trim();
                        _triggerMode = "Software";
                        _reverseX = false;
                        _reverseY = false;
                    }
                    return true;
                }
                catch
                {
                    camera.Close();
                    camera.FreeHGlobal();
                    throw;
                }
            }, cancellationToken);
        }

        public Task DisconnectAsync()
        {
            return RunSerializedAsync(() => { DisconnectCore(); return true; }, CancellationToken.None);
        }

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

        public Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                int error = 0;
                float exposure = 0;
                EnsureSuccess(camera.GetExposureTime(ref exposure, ref error), error, "Get exposure", camera);
                float gamma = 1;
                error = 0;
                EnsureSuccess(camera.GetGamma(ref gamma, ref error), error, "Get gamma", camera);
                uint saturation = 0;
                error = 0;
                EnsureSuccess(camera.GetSaturation(ref saturation, ref error), error, "Get saturation", camera);
                float gain = 0;
                TryGetOptionalFloat(camera, new[] { "GetGain", "GetGainValue" }, out gain);
                string triggerMode;
                bool reverseX;
                bool reverseY;
                lock (_stateSync)
                {
                    triggerMode = _triggerMode;
                    reverseX = _reverseX;
                    reverseY = _reverseY;
                }
                CameraParameterSet parameters = new CameraParameterSet
                {
                    ExposureMicroseconds = exposure,
                    Gain = gain,
                    Gamma = gamma,
                    Saturation = saturation,
                    TriggerMode = triggerMode,
                    ReverseX = reverseX,
                    ReverseY = reverseY
                };
                AddFeature(parameters, "ExposureTime", "曝光", CameraFeatureKind.Number, exposure.ToString("0.###", CultureInfo.InvariantCulture), "us");
                AddFeature(parameters, "Gain", "增益", CameraFeatureKind.Number, gain.ToString("0.###", CultureInfo.InvariantCulture), string.Empty);
                AddFeature(parameters, "Gamma", "伽马", CameraFeatureKind.Number, gamma.ToString("0.###", CultureInfo.InvariantCulture), string.Empty);
                AddFeature(parameters, "Saturation", "饱和度", CameraFeatureKind.Integer, saturation.ToString(CultureInfo.InvariantCulture), string.Empty);
                AddFeature(parameters, "ReverseX", "水平翻转", CameraFeatureKind.Boolean, reverseX ? "true" : "false", string.Empty);
                AddFeature(parameters, "ReverseY", "垂直翻转", CameraFeatureKind.Boolean, reverseY ? "true" : "false", string.Empty);
                AddFeature(parameters, "TriggerMode", "触发模式", CameraFeatureKind.Enumeration, triggerMode, string.Empty);
                return parameters;
            }, cancellationToken);
        }

        public Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (parameters.Saturation < 0 || parameters.Saturation > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(parameters.Saturation));
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                int error = 0;
                EnsureSuccess(camera.SetExposureTime((float)parameters.ExposureMicroseconds, ref error), error, "Set exposure", camera);
                TrySetOptionalFloat(camera, new[] { "SetGain", "SetGainValue" }, (float)parameters.Gain);
                error = 0;
                EnsureSuccess(camera.SetGamma((float)parameters.Gamma, ref error), error, "Set gamma", camera);
                error = 0;
                EnsureSuccess(camera.SetGammaEnable(true, ref error), error, "Enable gamma", camera);
                error = 0;
                EnsureSuccess(camera.SetSaturation((uint)parameters.Saturation, ref error), error, "Set saturation", camera);
                error = 0;
                EnsureSuccess(camera.SetReverseX(parameters.ReverseX, ref error), error, "Set reverse X", camera);
                error = 0;
                EnsureSuccess(camera.SetReverseY(parameters.ReverseY, ref error), error, "Set reverse Y", camera);
                SetTriggerModeCore(camera, parameters.TriggerMode);
                lock (_stateSync)
                {
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
                MethodInfo method = camera.GetType().GetMethod(methodName);
                if (method == null) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || !parameters[1].ParameterType.IsByRef) continue;
                object[] args = { value, 0 };
                try
                {
                    object result = method.Invoke(camera, args);
                    if (result is bool) return (bool)result;
                    return true;
                }
                catch (TargetInvocationException) { }
                catch (ArgumentException) { }
            }
            return false;
        }

        public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(() =>
            {
                CameraMVS camera = GetConnectedCamera();
                cancellationToken.ThrowIfCancellationRequested();
                string triggerMode;
                lock (_stateSync) triggerMode = _triggerMode;
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
            lock (_stateSync)
            {
                camera = _camera;
                _camera = null;
                _connectedDeviceId = null;
            }
            if (camera == null) return;
            try { camera.Close(); }
            finally
            {
                camera.FreeHGlobal();
            }
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
