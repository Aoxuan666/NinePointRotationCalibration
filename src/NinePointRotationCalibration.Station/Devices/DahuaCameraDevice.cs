using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MVSDK_Net;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Devices
{
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

    public sealed class DahuaCameraDevice : ICameraDevice
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

        public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () => EnumerateCore(cancellationToken),
                cancellationToken);
        }

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

        public Task DisconnectAsync()
        {
            return RunSerializedAsync(
                () =>
                {
                    int cleanupError = ReleaseCameraCore();
                    if (cleanupError != IMVDefine.IMV_OK)
                        throw new CameraDeviceException("Disconnect", cleanupError);
                    return true;
                },
                CancellationToken.None);
        }

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

                    if (_camera.IMV_FeatureIsAvailable("ExposureAuto"))
                        CheckResult(_camera.IMV_SetEnumFeatureSymbol("ExposureAuto", "Off"), "Disable auto exposure");

                    CheckResult(
                        _camera.IMV_SetDoubleFeatureValue("ExposureTime", exposureMicroseconds),
                        "Set exposure");
                    return true;
                },
                cancellationToken);
        }

        public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            return RunSerializedAsync(
                () => CaptureCore(cancellationToken),
                cancellationToken);
        }

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
                _sdkGate.Dispose();
            }
        }

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
                throw new CameraDeviceException("Release previous camera", previousCleanupError);

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
                SetEnumFeatureIfAvailable("AcquisitionMode", "Continuous");
                CheckResult(
                    _camera.IMV_SetEnumFeatureSymbol("TriggerSource", "Software"),
                    "Set software trigger source");
                SetEnumFeatureIfAvailable("TriggerSelector", "FrameStart");
                CheckResult(
                    _camera.IMV_SetEnumFeatureSymbol("TriggerMode", "On"),
                    "Enable trigger mode");

                cancellationToken.ThrowIfCancellationRequested();
                CheckResult(_camera.IMV_StartGrabbing(), "Start grabbing");
                _streamStarted = true;

                lock (_stateSync)
                {
                    _connectedDeviceId = resolved.StableId;
                    _isConnected = true;
                }
            }
            catch
            {
                ReleaseCameraCore();
                throw;
            }
        }

        private CameraFrame CaptureCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnectedCore();

            int clearResult = _camera.IMV_ClearFrameBuffer();
            if (clearResult != IMVDefine.IMV_OK && clearResult != IMVDefine.IMV_NOT_SUPPORT)
                throw new CameraDeviceException("Clear frame buffer", clearResult);
            cancellationToken.ThrowIfCancellationRequested();
            CheckResult(
                _camera.IMV_ExecuteCommandFeature("TriggerSoftware"),
                "Execute software trigger");

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            bool frameAcquired = false;
            int releaseResult = IMVDefine.IMV_OK;
            Bitmap bitmap = null;
            int getFrameResult = _camera.IMV_GetFrame(
                ref frame,
                checked((uint)_captureTimeoutMilliseconds));
            if (getFrameResult != IMVDefine.IMV_OK)
                throw new CameraDeviceException("Get frame (timeout " + _captureTimeoutMilliseconds + " ms)", getFrameResult);

            frameAcquired = true;
            try
            {
                if (frame.frameInfo.status != 0)
                    throw new CameraDeviceException("Validate frame status", unchecked((int)frame.frameInfo.status));

                bitmap = ConvertFrameToBitmap(frame);
            }
            finally
            {
                if (frameAcquired)
                {
                    try
                    {
                        releaseResult = _camera.IMV_ReleaseFrame(ref frame);
                    }
                    catch
                    {
                        releaseResult = IMVDefine.IMV_ERROR;
                    }
                }
            }

            if (releaseResult != IMVDefine.IMV_OK)
            {
                if (bitmap != null) bitmap.Dispose();
                throw new CameraDeviceException("Release frame", releaseResult);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (bitmap != null) bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            long sequence = ++_sequence;
            return new CameraFrame(bitmap, sequence, DateTime.UtcNow);
        }

        private Bitmap ConvertFrameToBitmap(IMVDefine.IMV_Frame frame)
        {
            int width = checked((int)frame.frameInfo.width);
            int height = checked((int)frame.frameInfo.height);
            if (width <= 0 || height <= 0 || frame.pData == IntPtr.Zero)
                throw new CameraDeviceException("Validate frame data", IMVDefine.IMV_INVALID_FRAME);

            if (IsMonoPixelFormat(frame.frameInfo.pixelFormat))
            {
                IntPtr pixels = GetConvertedPixels(
                    frame,
                    IMVDefine.IMV_EPixelType.gvspPixelMono8,
                    1);
                return CopyToBitmap(pixels, width, height, 1);
            }

            if (IsColorPixelFormat(frame.frameInfo.pixelFormat))
            {
                IntPtr pixels = GetConvertedPixels(
                    frame,
                    IMVDefine.IMV_EPixelType.gvspPixelBGR8,
                    3);
                return CopyToBitmap(pixels, width, height, 3);
            }

            throw new NotSupportedException("Unsupported Dahua pixel format: " + frame.frameInfo.pixelFormat + ".");
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
            if (convert.nDstDataLen > convert.nDstBufSize)
                throw new CameraDeviceException("Validate converted frame length", IMVDefine.IMV_INSUFFICIENT_MEMORY);
            return _conversionBuffer;
        }

        private static Bitmap CopyToBitmap(IntPtr source, int width, int height, int channelCount)
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
                    byte[] row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(source, checked(y * rowBytes)), row, 0, rowBytes);
                        IntPtr destination = data.Stride >= 0
                            ? IntPtr.Add(data.Scan0, checked(y * data.Stride))
                            : IntPtr.Add(data.Scan0, checked((height - 1 - y) * -data.Stride));
                        Marshal.Copy(row, 0, destination, rowBytes);
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
