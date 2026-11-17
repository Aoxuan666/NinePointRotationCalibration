using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>视觉工作站运行时配置项（含通信、相机、标定及数据文件存储路径）。</summary>
    [Serializable]
    public sealed class StationOptions
    {
        public const int CurrentConfigurationVersion = 1;

        [OptionalField]
        private int _configurationVersion;

        [OptionalField]
        private bool _cameraEnabled;

        [OptionalField]
        private bool _calibrationEnabled;

        public StationOptions()
        {
            _configurationVersion = CurrentConfigurationVersion;
            _cameraEnabled = true;
            _calibrationEnabled = true;
            TcpBindAddress = "0.0.0.0";
            TcpPort = 9100;
            TcpMaxMessageBytes = 1024 * 1024;
            MotionTimeoutMilliseconds = 30000;
            CameraProvider = "Mock";
            CameraLibraryDirectory = @"D:\ZM336AH\ZM336AH\lib";
            DahuaSdkAssemblyPath = @"C:\Program Files\HuarayTech\MV Viewer\Development\DOTNET DLL\IMV\DOTNET_4.0\x64\MVSDK_Net.dll";
            CameraSerialNumber = string.Empty;
            CameraExposureMicroseconds = 10000;
            CameraGain = 0;
            CameraGamma = 1.0;
            CameraTriggerMode = "Software";
            CameraFeatures = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            CameraTimeoutMilliseconds = 3000;
            TranslationStepX = 10.0;
            TranslationStepY = 10.0;
            RotationStepDegrees = 5.0;
            ExpectedBoardCount = 6;
            PreviewIntervalMilliseconds = 200;
            ArchiveImages = false;
            string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            CalibrationJobPath = Path.Combine(data, "calibration.nprcal");
            PositioningJobPath = Path.Combine(data, "positioning.nprcal");
            TeachingPath = Path.Combine(data, "teaching.json");
            DetectionProfilePath = Path.Combine(data, "detection-profile.json");
            ImageArchiveDirectory = Path.Combine(data, "images");
        }

        public int ConfigurationVersion
        {
            get { return _configurationVersion; }
            set { _configurationVersion = value; }
        }

        /// <summary>是否启用相机采集链路。</summary>
        public bool CameraEnabled
        {
            get { return _cameraEnabled; }
            set { _cameraEnabled = value; }
        }

        /// <summary>是否允许执行九点与旋转标定流程。</summary>
        public bool CalibrationEnabled
        {
            get { return _calibrationEnabled; }
            set { _calibrationEnabled = value; }
        }

        public string TcpBindAddress { get; set; }
        public int TcpPort { get; set; }
        public int TcpMaxMessageBytes { get; set; }
        public int MotionTimeoutMilliseconds { get; set; }
        public string CameraProvider { get; set; }
        public string CameraLibraryDirectory { get; set; }
        public string DahuaSdkAssemblyPath { get; set; }
        public string CameraSerialNumber { get; set; }
        public double CameraExposureMicroseconds { get; set; }
        public double CameraGain { get; set; }
        public double CameraGamma { get; set; }
        public string CameraTriggerMode { get; set; }
        public Dictionary<string, string> CameraFeatures { get; set; }
        public int CameraTimeoutMilliseconds { get; set; }
        public double TranslationStepX { get; set; }
        public double TranslationStepY { get; set; }
        public double RotationStepDegrees { get; set; }
        public int ExpectedBoardCount { get; set; }
        public int PreviewIntervalMilliseconds { get; set; }
        public bool ArchiveImages { get; set; }
        public string CalibrationJobPath { get; set; }
        public string PositioningJobPath { get; set; }
        public string TeachingPath { get; set; }
        public string DetectionProfilePath { get; set; }
        public string ImageArchiveDirectory { get; set; }

        /// <summary>创建包含独立相机特性字典的配置深拷贝。</summary>
        public StationOptions DeepClone()
        {
            StationOptions clone = (StationOptions)MemberwiseClone();
            clone.CameraFeatures = CameraFeatures == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(CameraFeatures, StringComparer.OrdinalIgnoreCase);
            return clone;
        }

        /// <summary>反序列化加载后补全缺失默认值并迁移旧版配置。</summary>
        public bool NormalizeAfterLoad()
        {
            bool changed = false;
            if (CameraGamma <= 0.0)
            {
                CameraGamma = 1.0;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(CameraTriggerMode))
            {
                CameraTriggerMode = "Software";
                changed = true;
            }
            if (CameraFeatures == null)
            {
                CameraFeatures = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                changed = true;
            }
            if (_configurationVersion >= CurrentConfigurationVersion) return changed;

            // V0 旧版配置不含相机与标定启用开关，迁移时默认置为启用，避免升级后误关闭原有站点功能。
            _cameraEnabled = true;
            _calibrationEnabled = true;
            _configurationVersion = CurrentConfigurationVersion;
            return true;
        }
    }
}
