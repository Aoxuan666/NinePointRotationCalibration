using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 将可读的 Job 元数据（job.json）、HALCON 二进制模型（model.bin）与模板原始参考底图（reference-image.png）打包保存到同一 ZIP 文件，并在加载时校验完整性。
    /// </summary>
    public static class JobPackageSerializer
    {
        private const string JobEntryName = "job.json";
        private const string ModelEntryName = "model.bin";
        private const string ReferenceImageEntryName = "reference-image.png";

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
            byte[] referenceImageData = metadata.Template == null || metadata.Template.ReferenceImageData == null
                ? null
                : metadata.Template.ReferenceImageData;
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

                // 模型二进制与模板参考底图分别写入 model.bin / reference-image.png，避免在 job.json 中重复存储或 Base64 膨胀。
                metadata.Template.ModelData = null;
                metadata.Template.ReferenceImageData = null;
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

                        if (referenceImageData != null && referenceImageData.Length > 0)
                        {
                            ZipArchiveEntry imageEntry = archive.CreateEntry(ReferenceImageEntryName, CompressionLevel.NoCompression);
                            using (Stream entryStream = imageEntry.Open())
                                entryStream.Write(referenceImageData, 0, referenceImageData.Length);
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

                ZipArchiveEntry referenceImageEntry = archive.GetEntry(ReferenceImageEntryName);
                if (referenceImageEntry != null)
                {
                    byte[] referenceImageData;
                    using (Stream entryStream = referenceImageEntry.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        entryStream.CopyTo(buffer);
                        referenceImageData = buffer.ToArray();
                    }

                    if (referenceImageData.Length > 0)
                    {
                        if (job.Template == null) job.Template = new TemplateDefinition();
                        job.Template.ReferenceImageData = referenceImageData;
                    }
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

        /// <summary>将模板参考底图无损编码为紧凑 PNG 字节流（灰度图自动压缩为 8bpp 灰度 PNG，彩图压缩为 24bpp RGB PNG）。</summary>
        public static byte[] EncodeReferenceImagePng(Bitmap source)
        {
            if (source == null) return null;
            using (MemoryStream stream = new MemoryStream())
            {
                Bitmap compact = TryConvertToCompactLosslessBitmap(source);
                if (compact != null)
                {
                    using (compact)
                    {
                        compact.Save(stream, ImageFormat.Png);
                    }
                }
                else
                {
                    source.Save(stream, ImageFormat.Png);
                }
                return stream.ToArray();
            }
        }

        /// <summary>将持久化的模板参考底图 PNG 字节流解码为独立的托管 Bitmap；无效时返回 null。</summary>
        public static Bitmap TryDecodeReferenceImage(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0) return null;
            try
            {
                using (MemoryStream stream = new MemoryStream(imageData, false))
                using (Image decoded = Image.FromStream(stream, false, false))
                {
                    return new Bitmap(decoded);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap TryConvertToCompactLosslessBitmap(Bitmap source)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0) return null;
            if (source.PixelFormat == PixelFormat.Format8bppIndexed) return null;
            if (source.PixelFormat != PixelFormat.Format24bppRgb &&
                source.PixelFormat != PixelFormat.Format32bppRgb &&
                source.PixelFormat != PixelFormat.Format32bppArgb &&
                source.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                return null;
            }

            try
            {
                int width = source.Width;
                int height = source.Height;
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] srcBytes;
                int srcStride;
                try
                {
                    srcStride = srcData.Stride;
                    if (srcStride <= 0) return null;
                    srcBytes = new byte[srcStride * height];
                    Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);
                }
                finally
                {
                    source.UnlockBits(srcData);
                }

                bool isGrayscale = true;
                for (int y = 0; y < height && isGrayscale; y++)
                {
                    int rowStart = y * srcStride;
                    int rowEnd = rowStart + (width * 3);
                    for (int p = rowStart; p < rowEnd; p += 3)
                    {
                        byte b = srcBytes[p];
                        if (b != srcBytes[p + 1] || b != srcBytes[p + 2])
                        {
                            isGrayscale = false;
                            break;
                        }
                    }
                }

                if (isGrayscale)
                {
                    Bitmap gray = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
                    try
                    {
                        ColorPalette palette = gray.Palette;
                        for (int i = 0; i < 256; i++)
                        {
                            palette.Entries[i] = Color.FromArgb(255, i, i, i);
                        }
                        gray.Palette = palette;

                        BitmapData dstData = gray.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                        try
                        {
                            int dstStride = dstData.Stride;
                            byte[] dstBytes = new byte[dstStride * height];
                            for (int y = 0; y < height; y++)
                            {
                                int srcRow = y * srcStride;
                                int dstRow = y * dstStride;
                                for (int x = 0; x < width; x++)
                                {
                                    dstBytes[dstRow + x] = srcBytes[srcRow + (x * 3)];
                                }
                            }
                            Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
                        }
                        finally
                        {
                            gray.UnlockBits(dstData);
                        }
                        return gray;
                    }
                    catch
                    {
                        gray.Dispose();
                        throw;
                    }
                }

                if (source.PixelFormat == PixelFormat.Format24bppRgb) return null;

                Bitmap rgb24 = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                try
                {
                    BitmapData dstData = rgb24.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int dstStride = dstData.Stride;
                        byte[] dstBytes = new byte[dstStride * height];
                        int rowBytes = width * 3;
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.BlockCopy(srcBytes, y * srcStride, dstBytes, y * dstStride, rowBytes);
                        }
                        Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
                    }
                    finally
                    {
                        rgb24.UnlockBits(dstData);
                    }
                    return rgb24;
                }
                catch
                {
                    rgb24.Dispose();
                    throw;
                }
            }
            catch
            {
                return null;
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
            else job.SolverOptions.MigrateLegacyDefaultPixelThresholds();
            if (job.Samples == null) job.Samples = new System.Collections.Generic.List<CalibrationSample>();
            if (string.IsNullOrWhiteSpace(job.JobId)) job.JobId = Guid.NewGuid().ToString("N");
            if (job.SchemaVersion <= 0) job.SchemaVersion = CalibrationJob.CurrentSchemaVersion;
        }
    }
}
