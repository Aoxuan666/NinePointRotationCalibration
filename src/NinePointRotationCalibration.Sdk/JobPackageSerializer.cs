using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 将可读的 Job 元数据（job.json）与 HALCON 二进制模型（model.bin）打包保存到同一 ZIP 文件，并在加载时校验完整性。
    /// </summary>
    public static class JobPackageSerializer
    {
        private const string JobEntryName = "job.json";
        private const string ModelEntryName = "model.bin";

        /// <summary>将 Job 元数据与 HALCON 模型二进制打包保存为标定文件；失败时抛出异常。</summary>
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
                    // 保存前计算并核对模型数据的 SHA-256 哈希，确保元数据与二进制一致。
                    string actualHash = TemplateDefinition.ComputeModelHash(modelData);
                    if (!string.IsNullOrWhiteSpace(metadata.Template.ModelHash) &&
                        !string.Equals(metadata.Template.ModelHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("HALCON 模型哈希校验失败，无法保存不一致的 Job。 ");
                    if (string.IsNullOrWhiteSpace(metadata.Template.ModelHash))
                        metadata.Template.ModelHash = actualHash;
                }

                // 模型二进制单独写入 model.bin，避免在 job.json 中重复存储。
                metadata.Template.ModelData = null;
            }

            string tempPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
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
                    stream.Flush(true);
                }

                CommitFile(tempPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static void CommitFile(string tempPath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
            {
                try
                {
                    File.Move(tempPath, destinationPath);
                    return;
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                }
            }

            try
            {
                File.Replace(tempPath, destinationPath, null, true);
            }
            catch (FileNotFoundException)
            {
                File.Move(tempPath, destinationPath);
            }
        }

        /// <summary>从标定文件包加载 Job 并校验模型 SHA-256 哈希；失败时抛出异常。</summary>
        public static CalibrationJob Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("加载路径为空。", "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("标定 Job 文件不存在。", fullPath);

            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
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
                    // 校验 model.bin 的实际 SHA-256 哈希是否与 job.json 中声明一致。
                    string actualHash = TemplateDefinition.ComputeModelHash(modelData);
                    if (string.IsNullOrWhiteSpace(expectedHash))
                        throw new InvalidDataException("Job 元数据缺少 HALCON 模型哈希，无法验证 model.bin 完整性。 ");
                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("HALCON 模型哈希校验失败，Job 文件可能已损坏。 ");

                    string halconVersion = job.Template.HalconVersion;
                    bool metadataWasDirty = hadTemplateMetadata && job.Template.IsModelDirty;
                    job.Template.SetModelData(modelData, halconVersion);
                    // SetModelData 默认会清除脏标记；若包内特意携带了变更后的参数与旧模型，需保留原 IsModelDirty 状态。
                    if (metadataWasDirty) job.Template.IsModelDirty = true;
                }

                NormalizeLoadedJob(job);
                return job;
            }
        }

        /// <summary>尝试保存 Job 到指定路径，并以结构化结果返回成功或失败信息。</summary>
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

        /// <summary>尝试从指定路径加载并校验 Job，并以结构化结果返回状态。</summary>
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
