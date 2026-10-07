using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// Stores readable job metadata and the HALCON model as separate entries in one package.
    /// </summary>
    public static class JobPackageSerializer
    {
        private const string JobEntryName = "job.json";
        private const string ModelEntryName = "model.bin";

        public static void Save(string path, CalibrationJob job)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存路径为空。", "path");
            if (job == null) throw new ArgumentNullException("job");

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("保存目录无效。", "path");
            Directory.CreateDirectory(directory);

            CalibrationJob metadata = job.DeepClone();
            byte[] modelData = metadata.Template == null || metadata.Template.ModelData == null
                ? null
                : (byte[])metadata.Template.ModelData.Clone();
            if (metadata.Template != null)
            {
                bool hasModelData = modelData != null && modelData.Length > 0;
                bool declaresModel = !string.IsNullOrWhiteSpace(metadata.Template.ModelHash) ||
                                     !metadata.Template.IsModelDirty;
                if (!hasModelData && declaresModel)
                    throw new InvalidDataException("Job 元数据声明存在 HALCON 模型，但模型数据为空。 ");

                if (hasModelData)
                {
                    string actualHash = TemplateDefinition.ComputeModelHash(modelData);
                    if (!string.IsNullOrWhiteSpace(metadata.Template.ModelHash) &&
                        !string.Equals(metadata.Template.ModelHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("HALCON 模型哈希校验失败，无法保存不一致的 Job。 ");
                    if (string.IsNullOrWhiteSpace(metadata.Template.ModelHash))
                        metadata.Template.ModelHash = actualHash;
                }

                // The binary is stored in model.bin; never duplicate it in JSON.
                metadata.Template.ModelData = null;
            }

            string tempPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    ZipArchiveEntry jobEntry = archive.CreateEntry(JobEntryName, CompressionLevel.Optimal);
                    using (Stream entryStream = jobEntry.Open())
                    {
                        DataContractJsonSerializer serializer = CreateSerializer();
                        serializer.WriteObject(entryStream, metadata);
                    }

                    if (modelData != null && modelData.Length > 0)
                    {
                        ZipArchiveEntry modelEntry = archive.CreateEntry(ModelEntryName, CompressionLevel.NoCompression);
                        using (Stream entryStream = modelEntry.Open())
                            entryStream.Write(modelData, 0, modelData.Length);
                    }
                }

                if (File.Exists(fullPath))
                {
                    string backupPath = fullPath + ".bak";
                    File.Replace(tempPath, fullPath, backupPath, true);
                    try { File.Delete(backupPath); } catch { }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        public static CalibrationJob Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("加载路径为空。", "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("标定 Job 文件不存在。", fullPath);

            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                ZipArchiveEntry jobEntry = archive.GetEntry(JobEntryName);
                if (jobEntry == null) throw new InvalidDataException("Job 包缺少 job.json。 ");

                CalibrationJob job;
                using (Stream entryStream = jobEntry.Open())
                    job = (CalibrationJob)CreateSerializer().ReadObject(entryStream);

                if (job == null) throw new InvalidDataException("Job 元数据为空。 ");
                if (job.SchemaVersion <= 0 || job.SchemaVersion > CalibrationJob.CurrentSchemaVersion)
                    throw new InvalidDataException("不支持的 Job SchemaVersion: " + job.SchemaVersion + "。 ");

                TemplateDefinition metadataTemplate = job.Template;
                bool metadataHasEmbeddedModel = metadataTemplate != null &&
                    metadataTemplate.ModelData != null && metadataTemplate.ModelData.Length > 0;
                bool metadataDeclaresModel = metadataTemplate != null &&
                    (metadataHasEmbeddedModel || !string.IsNullOrWhiteSpace(metadataTemplate.ModelHash) ||
                     !metadataTemplate.IsModelDirty);
                ZipArchiveEntry modelEntry = archive.GetEntry(ModelEntryName);
                if (modelEntry == null && metadataDeclaresModel)
                    throw new InvalidDataException("Job 包缺少声明的 HALCON 模型文件 model.bin。 ");

                if (modelEntry != null)
                {
                    byte[] modelData;
                    using (Stream entryStream = modelEntry.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        entryStream.CopyTo(buffer);
                        modelData = buffer.ToArray();
                    }

                    if (modelData.Length == 0)
                        throw new InvalidDataException("Job 包中的 model.bin 为空。 ");

                    bool hadTemplateMetadata = job.Template != null;
                    if (job.Template == null) job.Template = new TemplateDefinition();
                    string expectedHash = job.Template.ModelHash;
                    string actualHash = TemplateDefinition.ComputeModelHash(modelData);
                    if (string.IsNullOrWhiteSpace(expectedHash))
                        throw new InvalidDataException("Job 元数据缺少 HALCON 模型哈希，无法验证 model.bin 完整性。 ");
                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("HALCON 模型哈希校验失败，Job 文件可能已损坏。 ");

                    string halconVersion = job.Template.HalconVersion;
                    bool metadataWasDirty = hadTemplateMetadata && job.Template.IsModelDirty;
                    job.Template.SetModelData(modelData, halconVersion);
                    // SetModelData marks a template clean. Preserve an explicit
                    // dirty flag when the package intentionally carries an old
                    // model alongside changed settings.
                    if (metadataWasDirty) job.Template.IsModelDirty = true;
                }

                NormalizeLoadedJob(job);
                return job;
            }
        }

        public static JobPersistenceResult TrySave(string path, CalibrationJob job)
        {
            try
            {
                Save(path, job);
                return new JobPersistenceResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Job 保存成功。",
                    Path = Path.GetFullPath(path),
                    Job = job == null ? null : job.DeepClone()
                };
            }
            catch (Exception ex)
            {
                return new JobPersistenceResult
                {
                    Success = false,
                    ErrorCode = CalibrationErrorCode.SerializationError,
                    Message = "Job 保存失败：" + ex.Message,
                    Path = path
                };
            }
        }

        public static JobPersistenceResult TryLoad(string path)
        {
            try
            {
                CalibrationJob job = Load(path);
                return new JobPersistenceResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Job 加载成功。",
                    Path = Path.GetFullPath(path),
                    Job = job
                };
            }
            catch (Exception ex)
            {
                return new JobPersistenceResult
                {
                    Success = false,
                    ErrorCode = CalibrationErrorCode.SerializationError,
                    Message = "Job 加载失败：" + ex.Message,
                    Path = path
                };
            }
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(typeof(CalibrationJob), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                SerializeReadOnlyTypes = false
            });
        }

        private static void NormalizeLoadedJob(CalibrationJob job)
        {
            if (job.Template == null) job.Template = new TemplateDefinition();
            if (job.Template.MaskRegions == null) job.Template.MaskRegions = new System.Collections.Generic.List<TemplateMaskShape>();
            if (job.Template.EraseStrokes == null) job.Template.EraseStrokes = new System.Collections.Generic.List<TemplateEraseStroke>();
            if (job.Template.ShapeParameters == null) job.Template.ShapeParameters = new ShapeModelParameters();
            if (job.Template.NccParameters == null) job.Template.NccParameters = new NccModelParameters();
            if (job.Template.MatchParameters == null) job.Template.MatchParameters = new MatchParameters();
            if (job.Template.ModelReference == null) job.Template.ModelReference = new ImagePose();
            if (job.SolverOptions == null) job.SolverOptions = new CalibrationSolverOptions();
            if (job.Samples == null) job.Samples = new System.Collections.Generic.List<CalibrationSample>();
            if (string.IsNullOrWhiteSpace(job.JobId)) job.JobId = Guid.NewGuid().ToString("N");
            if (job.SchemaVersion <= 0) job.SchemaVersion = CalibrationJob.CurrentSchemaVersion;
        }
    }
}
