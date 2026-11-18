using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// 负责 HALCON 模板句柄的生命周期管理与匹配计算。
    /// 由于原生模型句柄非线程安全，单实例调用通过锁串行化。
    /// </summary>
    public sealed class HalconTemplateEngine :
        ITemplateMatchingEngine,
        IGrayImageTemplateMatchingEngine,
        IFeaturePreviewTemplateMatchingEngine
    {
        private readonly object _syncRoot = new object();
        private HShapeModel _shapeModel;
        private HNCCModel _nccModel;
        private string _cachedModelKey;
        private int _cachedModelLength = -1;
        private List<List<ImagePoint>> _cachedRelativeContours = new List<List<ImagePoint>>();
        private bool _disposed;

        /// <summary>基于图像域（Shape 或 NCC）创建 HALCON 模板并更新缓存。</summary>
        public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (referenceImage == null)
            {
                return Complete(TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null."), stopwatch);
            }

            if (definition == null)
            {
                return Complete(TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null."), stopwatch);
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                TemplateDefinition output = definition.DeepClone();
                HShapeModel shapeModel = null;
                HNCCModel nccModel = null;

                try
                {
                    ValidateBuildParameters(output);
                    using (HImage gray = HalconImageConverter.ToGrayHImage(referenceImage))
                    using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(output, referenceImage.Width, referenceImage.Height))
                    using (HImage reduced = gray.ReduceDomain(effectiveRegion))
                    {
                        double area;
                        double domainRow;
                        double domainColumn;
                        HalconRegionBuilder.GetAreaCenter(effectiveRegion, out area, out domainRow, out domainColumn);

                        bool anchorWasUninitialized = output.ReferenceImageWidth <= 0
                            && output.ReferenceImageHeight <= 0
                            && output.ReferenceAnchor.Row == 0.0
                            && output.ReferenceAnchor.Column == 0.0
                            && !output.IsReferenceAnchorLocked;

                        output.ReferenceImageWidth = referenceImage.Width;
                        output.ReferenceImageHeight = referenceImage.Height;
                        output.DomainCenter = new ImagePoint(domainRow, domainColumn);
                        output.ModelReference = new ImagePose(domainRow, domainColumn, 0.0);
                        // 首次建模且参考锚点未锁定/初始化时，默认以有效区域质心作为锚点。
                        if (anchorWasUninitialized)
                        {
                            output.ReferenceAnchor = output.DomainCenter;
                        }

                        byte[] modelData;
                        List<List<ImagePoint>> relativeContours;
                        string buildMessage = "Template model was created successfully.";
                        if (output.ModelType == TemplateModelType.Shape)
                        {
                            shapeModel = CreateShapeModel(reduced, output.ShapeParameters, out buildMessage);
                            using (HXLDCont contours = shapeModel.GetShapeModelContours(1))
                            {
                                relativeContours = HalconContourConverter.Extract(contours);
                            }

                            HShapeModel writeModel = shapeModel;
                            modelData = HalconModelFile.Write(".shm", writeModel.WriteShapeModel);
                        }
                        else if (output.ModelType == TemplateModelType.Ncc)
                        {
                            nccModel = CreateNccModel(reduced, output.NccParameters);
                            List<List<ImagePoint>> absoluteContours = HalconRegionBuilder.ExtractRegionContours(effectiveRegion);
                            relativeContours = ToRelative(absoluteContours, output.ModelReference);
                            HNCCModel writeModel = nccModel;
                            modelData = HalconModelFile.Write(".ncm", writeModel.WriteNccModel);
                        }
                        else
                        {
                            throw new ArgumentOutOfRangeException("ModelType", "Unsupported model type: " + output.ModelType + ".");
                        }

                        output.SetModelData(modelData, TryGetHalconVersion());
                        CacheBuiltModel(output, shapeModel, nccModel, relativeContours);
                        shapeModel = null;
                        nccModel = null;

                        List<List<ImagePoint>> referenceContours = HalconContourConverter.Transform(
                            relativeContours,
                            domainRow,
                            domainColumn,
                            0.0);

                        return Complete(new TemplateBuildResult
                        {
                            Success = true,
                            ErrorCode = CalibrationErrorCode.None,
                            Message = buildMessage,
                            Template = output,
                            EffectiveArea = area,
                            ContourSegments = referenceContours
                        }, stopwatch);
                    }
                }
                catch (Exception exception)
                {
                    return Complete(TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)), stopwatch);
                }
                finally
                {
                    if (shapeModel != null) shapeModel.Dispose();
                    if (nccModel != null) nccModel.Dispose();
                }
            }
        }

        /// <summary>示教创建模板的兼容别名，内部直接转调 BuildTemplate。</summary>
        public TemplateBuildResult Teach(Bitmap referenceImage, TemplateDefinition definition)
        {
            return BuildTemplate(referenceImage, definition);
        }

        /// <summary>使用已转换的灰度图快速预览图像域 Shape/NCC 特征轮廓，跳过建模与序列化。</summary>
        public TemplateBuildResult PreviewFeatures(HalconGrayImage grayImage, TemplateDefinition definition)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (grayImage == null)
            {
                return Complete(TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Gray image is null."), stopwatch);
            }

            if (definition == null)
            {
                return Complete(TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null."), stopwatch);
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                TemplateDefinition output = definition.DeepClone();
                try
                {
                    ValidateBuildParameters(output);
                    using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(
                        output,
                        grayImage.Width,
                        grayImage.Height))
                    using (HImage reduced = grayImage.Image.ReduceDomain(effectiveRegion))
                    {
                        double area;
                        double domainRow;
                        double domainColumn;
                        HalconRegionBuilder.GetAreaCenter(effectiveRegion, out area, out domainRow, out domainColumn);

                        bool anchorWasUninitialized = output.ReferenceImageWidth <= 0
                            && output.ReferenceImageHeight <= 0
                            && output.ReferenceAnchor.Row == 0.0
                            && output.ReferenceAnchor.Column == 0.0
                            && !output.IsReferenceAnchorLocked;

                        output.ReferenceImageWidth = grayImage.Width;
                        output.ReferenceImageHeight = grayImage.Height;
                        output.DomainCenter = new ImagePoint(domainRow, domainColumn);
                        output.ModelReference = new ImagePose(domainRow, domainColumn, 0.0);
                        if (anchorWasUninitialized)
                        {
                            output.ReferenceAnchor = output.DomainCenter;
                        }

                        List<List<ImagePoint>> contours;
                        string previewMessage;
                        if (output.ModelType == TemplateModelType.Shape)
                        {
                            contours = InspectShapeContours(reduced, output.ShapeParameters, out previewMessage);
                        }
                        else if (output.ModelType == TemplateModelType.Ncc)
                        {
                            contours = HalconRegionBuilder.ExtractRegionContours(effectiveRegion);
                            previewMessage = "NCC 模板区域特征预览完成。";
                        }
                        else
                        {
                            throw new ArgumentOutOfRangeException("ModelType", "Unsupported model type: " + output.ModelType + ".");
                        }

                        return Complete(new TemplateBuildResult
                        {
                            Success = true,
                            ErrorCode = CalibrationErrorCode.None,
                            Message = previewMessage,
                            Template = output,
                            EffectiveArea = area,
                            ContourSegments = contours
                        }, stopwatch);
                    }
                }
                catch (Exception exception)
                {
                    return Complete(TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)), stopwatch);
                }
            }
        }

        /// <summary>在图像中定位得分最高的单个模板匹配目标。</summary>
        public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            TemplateMatchCollectionResult collection = LocateAll(image, definition, includeContour);
            if (!collection.Success || collection.Matches.Count == 0)
            {
                TemplateMatchResult failure = TemplateMatchResult.Failure(collection.ErrorCode, collection.Message);
                failure.ElapsedMilliseconds = collection.ElapsedMilliseconds;
                return failure;
            }

            return collection.Matches[0];
        }

        /// <summary>在 Bitmap 图像中定位所有满足条件的匹配目标。</summary>
        public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            if (image == null)
            {
                return TemplateMatchCollectionResult.Failure(CalibrationErrorCode.ParameterInvalid, "Input image is null.");
            }

            HalconGrayImage gray;
            try
            {
                gray = HalconGrayImage.FromBitmap(image);
            }
            catch (Exception exception)
            {
                return TemplateMatchCollectionResult.Failure(MapErrorCode(exception), FormatException(exception));
            }

            using (gray)
            {
                return LocateAll(gray, definition, includeContour);
            }
        }

        /// <summary>复用已转换的 HALCON 灰度图定位所有满足条件的匹配目标。</summary>
        public TemplateMatchCollectionResult LocateAll(HalconGrayImage image, TemplateDefinition definition, bool includeContour = true)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (image == null)
            {
                return Complete(TemplateMatchCollectionResult.Failure(CalibrationErrorCode.ParameterInvalid, "Input image is null."), stopwatch);
            }

            if (definition == null)
            {
                return Complete(TemplateMatchCollectionResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null."), stopwatch);
            }

            // 调用方传入私有模板快照，本方法仅做只读访问。
            TemplateDefinition snapshot = definition;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                HTuple rows = null;
                HTuple columns = null;
                HTuple angles = null;
                HTuple scores = null;

                try
                {
                    ValidateLocateParameters(snapshot);
                    EnsureModelLoaded(snapshot);

                    using (HRegion searchRegion = HalconRegionBuilder.BuildSearchRegion(snapshot.SearchRegion, image.Width, image.Height))
                    using (HImage searchImage = image.Image.ReduceDomain(searchRegion))
                    {
                        MatchParameters parameters = snapshot.MatchParameters;
                        TrySetNativeTimeout(snapshot.ModelType, parameters.TimeoutMilliseconds);

                        if (snapshot.ModelType == TemplateModelType.Shape)
                        {
                            _shapeModel.FindShapeModel(
                                searchImage,
                                parameters.AngleStartRad,
                                parameters.AngleExtentRad,
                                parameters.MinScore,
                                parameters.NumMatches,
                                parameters.MaxOverlap,
                                NormalizeShapeSubPixel(parameters.SubPixel),
                                parameters.NumLevels,
                                parameters.Greediness,
                                out rows,
                                out columns,
                                out angles,
                                out scores);
                        }
                        else
                        {
                            _nccModel.FindNccModel(
                                searchImage,
                                parameters.AngleStartRad,
                                parameters.AngleExtentRad,
                                parameters.MinScore,
                                parameters.NumMatches,
                                parameters.MaxOverlap,
                                NormalizeNccSubPixel(parameters.SubPixel),
                                parameters.NumLevels,
                                out rows,
                                out columns,
                                out angles,
                                out scores);
                        }

                        stopwatch.Stop();
                        if (parameters.TimeoutMilliseconds > 0 && stopwatch.ElapsedMilliseconds > parameters.TimeoutMilliseconds)
                        {
                            return Complete(TemplateMatchCollectionResult.Failure(
                                CalibrationErrorCode.TemplateMatchFailed,
                                "Template matching exceeded the configured timeout of " + parameters.TimeoutMilliseconds + " ms."), stopwatch);
                        }

                        int count = Math.Min(Math.Min(rows.Length, columns.Length), Math.Min(angles.Length, scores.Length));
                        if (count == 0)
                        {
                            return Complete(TemplateMatchCollectionResult.Failure(
                                CalibrationErrorCode.TemplateMatchFailed,
                                "No template match met the configured score and search constraints."), stopwatch);
                        }

                        TemplateMatchCollectionResult result = new TemplateMatchCollectionResult
                        {
                            Success = true,
                            ErrorCode = CalibrationErrorCode.None,
                            Message = count == 1 ? "Template located." : count + " template matches located."
                        };

                        for (int index = 0; index < count; index++)
                        {
                            double modelRow = rows[index].D;
                            double modelColumn = columns[index].D;
                            double modelAngle = angles[index].D;
                            ImagePoint anchor = TransformAnchor(snapshot, modelRow, modelColumn, modelAngle);
                            List<List<ImagePoint>> contours = includeContour
                                ? HalconContourConverter.Transform(_cachedRelativeContours, modelRow, modelColumn, modelAngle)
                                : new List<List<ImagePoint>>();

                            TemplateMatchResult match = new TemplateMatchResult
                            {
                                Success = true,
                                ErrorCode = CalibrationErrorCode.None,
                                Message = "Template located.",
                                Anchor = anchor,
                                ModelPose = new ImagePose(modelRow, modelColumn, AngleMath.RadiansToDegrees(modelAngle)),
                                Score = scores[index].D,
                                ContourSegments = contours,
                                Contour = HalconContourConverter.Flatten(contours),
                                ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
                            };
                            result.Matches.Add(match);
                        }

                        return Complete(result, stopwatch);
                    }
                }
                catch (Exception exception)
                {
                    return Complete(TemplateMatchCollectionResult.Failure(MapErrorCode(exception), FormatException(exception)), stopwatch);
                }
                finally
                {
                    if (rows != null) rows.Dispose();
                    if (columns != null) columns.Dispose();
                    if (angles != null) angles.Dispose();
                    if (scores != null) scores.Dispose();
                }
            }
        }

        /// <summary>生成模板有效区域的质心、面积与边界轮廓预览。</summary>
        public TemplateRegionPreviewResult PreviewEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight)
        {
            if (definition == null)
            {
                return new TemplateRegionPreviewResult
                {
                    Success = false,
                    ErrorCode = CalibrationErrorCode.ParameterInvalid,
                    Message = "Template definition is null."
                };
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                try
                {
                    using (HRegion region = HalconRegionBuilder.BuildEffectiveRegion(definition.DeepClone(), imageWidth, imageHeight))
                    {
                        double area;
                        double row;
                        double column;
                        HalconRegionBuilder.GetAreaCenter(region, out area, out row, out column);
                        return new TemplateRegionPreviewResult
                        {
                            Success = true,
                            ErrorCode = CalibrationErrorCode.None,
                            Message = "Effective region generated.",
                            EffectiveArea = area,
                            DomainCenter = new ImagePoint(row, column),
                            ContourSegments = HalconRegionBuilder.ExtractRegionContours(region)
                        };
                    }
                }
                catch (Exception exception)
                {
                    return new TemplateRegionPreviewResult
                    {
                        Success = false,
                        ErrorCode = MapErrorCode(exception),
                        Message = FormatException(exception)
                    };
                }
            }
        }

        /// <summary>清除并释放当前缓存的 HALCON 模板句柄。</summary>
        public void ClearCache()
        {
            lock (_syncRoot)
            {
                if (_disposed) return;
                ClearCacheCore();
            }
        }

        /// <summary>释放缓存的原生模板资源并标记引擎已销毁。</summary>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed) return;
                ClearCacheCore();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void EnsureModelLoaded(TemplateDefinition definition)
        {
            // 生产节拍高频命中缓存：模型在首次加载时已做过 SHA-256 校验，此处按键和字节长度快速复用以省去重复哈希。
            int modelLength = definition.ModelData == null ? 0 : definition.ModelData.Length;
            if (!string.IsNullOrWhiteSpace(definition.ModelHash) && modelLength == _cachedModelLength)
            {
                string declaredKey = ModelKey(definition.ModelType, definition.ModelHash);
                if (string.Equals(declaredKey, _cachedModelKey, StringComparison.Ordinal)
                    && ((definition.ModelType == TemplateModelType.Shape && _shapeModel != null)
                        || (definition.ModelType == TemplateModelType.Ncc && _nccModel != null)))
                {
                    if (definition.ModelType == TemplateModelType.Ncc)
                        _cachedRelativeContours = GetRelativeContours(definition, null);
                    return;
                }
            }

            string actualHash = TemplateDefinition.ComputeModelHash(definition.ModelData);
            if (!string.IsNullOrWhiteSpace(definition.ModelHash)
                && !string.Equals(definition.ModelHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The template model hash does not match ModelData.");
            }

            string key = ModelKey(definition.ModelType, actualHash);
            if (string.Equals(key, _cachedModelKey, StringComparison.Ordinal)
                && ((definition.ModelType == TemplateModelType.Shape && _shapeModel != null)
                    || (definition.ModelType == TemplateModelType.Ncc && _nccModel != null)))
            {
                // NCC 叠加轮廓取决于当前有效区域与模型参考点，而非仅由 model.bin 决定；保留原生句柄同时刷新相对轮廓。
                if (definition.ModelType == TemplateModelType.Ncc)
                    _cachedRelativeContours = GetRelativeContours(definition, null);
                return;
            }

            HShapeModel loadedShape = null;
            HNCCModel loadedNcc = null;
            try
            {
                if (definition.ModelType == TemplateModelType.Shape)
                {
                    loadedShape = HalconModelFile.Read(definition.ModelData, ".shm", path =>
                    {
                        HShapeModel model = new HShapeModel();
                        try
                        {
                            model.ReadShapeModel(path);
                            return model;
                        }
                        catch
                        {
                            model.Dispose();
                            throw;
                        }
                    });
                }
                else if (definition.ModelType == TemplateModelType.Ncc)
                {
                    loadedNcc = HalconModelFile.Read(definition.ModelData, ".ncm", path =>
                    {
                        HNCCModel model = new HNCCModel();
                        try
                        {
                            model.ReadNccModel(path);
                            return model;
                        }
                        catch
                        {
                            model.Dispose();
                            throw;
                        }
                    });
                }
                else
                {
                    throw new ArgumentOutOfRangeException("ModelType", "Unsupported model type: " + definition.ModelType + ".");
                }

                List<List<ImagePoint>> relativeContours = GetRelativeContours(definition, loadedShape);
                ClearCacheCore();
                _shapeModel = loadedShape;
                _nccModel = loadedNcc;
                _cachedModelKey = key;
                _cachedModelLength = modelLength;
                _cachedRelativeContours = relativeContours;
                loadedShape = null;
                loadedNcc = null;
            }
            finally
            {
                if (loadedShape != null) loadedShape.Dispose();
                if (loadedNcc != null) loadedNcc.Dispose();
            }
        }

        private void CacheBuiltModel(
            TemplateDefinition definition,
            HShapeModel shapeModel,
            HNCCModel nccModel,
            List<List<ImagePoint>> relativeContours)
        {
            ClearCacheCore();
            _shapeModel = shapeModel;
            _nccModel = nccModel;
            _cachedModelKey = string.IsNullOrWhiteSpace(definition.ModelHash)
                ? null
                : ModelKey(definition.ModelType, definition.ModelHash);
            _cachedModelLength = definition.ModelData == null ? 0 : definition.ModelData.Length;
            _cachedRelativeContours = relativeContours ?? new List<List<ImagePoint>>();
        }

        private static HTuple ResolveShapeContrast(
            HImage reduced,
            ShapeModelParameters parameters,
            string metric,
            bool forPreview,
            out string statusMessage)
        {
            if (!parameters.AutoThreshold)
            {
                statusMessage = forPreview
                    ? "图像域 Shape 特征预览完成。"
                    : "Template model was created successfully.";
                return new HTuple(parameters.Contrast);
            }

            AdaptiveTemplateThresholds resolved = HalconAutoThresholdResolver.Resolve(reduced, parameters, metric);
            parameters.Contrast = resolved.ContrastHigh;
            parameters.MinContrast = Math.Min(resolved.MinContrast, resolved.ContrastHigh);
            statusMessage = string.Format(
                CultureInfo.InvariantCulture,
                "{0}（自适应滞后对比度 {1}/{2}，最小对比度 {3}）。",
                forPreview ? "图像域 Shape 特征预览完成" : "图像域 Shape 建模完成",
                resolved.ContrastLow,
                resolved.ContrastHigh,
                parameters.MinContrast);
            return new HTuple(resolved.ContrastLow, resolved.ContrastHigh);
        }

        private static List<List<ImagePoint>> InspectShapeContours(
            HImage reduced,
            ShapeModelParameters parameters,
            out string previewMessage)
        {
            string metric = DefaultIfBlank(parameters.Metric, "use_polarity");
            HTuple contrastTuple = ResolveShapeContrast(reduced, parameters, metric, true, out previewMessage);
            List<List<ImagePoint>> contours = TryInspectWithContrast(reduced, contrastTuple);
            if (contours.Count == 0 && parameters.AutoThreshold)
            {
                previewMessage = "图像域 Shape 特征预览完成（已回退至 HALCON 原生 auto 对比度）。";
                using (HShapeModel fallbackModel = CreateShapeModelCore(
                    reduced,
                    parameters,
                    metric,
                    new HTuple("auto"),
                    new HTuple("auto")))
                using (HXLDCont xld = fallbackModel.GetShapeModelContours(1))
                {
                    double area;
                    double row;
                    double column;
                    using (HRegion domain = reduced.GetDomain())
                    {
                        HalconRegionBuilder.GetAreaCenter(domain, out area, out row, out column);
                    }
                    return HalconContourConverter.Transform(HalconContourConverter.Extract(xld), row, column, 0.0);
                }
            }

            if (contours.Count == 0)
            {
                throw new ModelStateException(
                    CalibrationErrorCode.ParameterInvalid,
                    "当前模板区域未检测到足够对比度边缘特征。");
            }

            return contours;
        }

        private static List<List<ImagePoint>> TryInspectWithContrast(HImage reduced, HTuple contrastTuple)
        {
            HRegion modelRegion = null;
            try
            {
                using (contrastTuple)
                using (HImage modelImage = reduced.InspectShapeModel(out modelRegion, 1, contrastTuple))
                {
                    if (modelRegion == null || modelRegion.CountObj() == 0)
                        return new List<List<ImagePoint>>();

                    double area = modelRegion.AreaCenter(out double _, out double _);
                    if (!IsFinite(area) || area <= 0.0)
                        return new List<List<ImagePoint>>();

                    using (HRegion skeleton = modelRegion.Skeleton())
                    using (HXLDCont xld = skeleton.GenContoursSkeletonXld(1, "filter"))
                    {
                        List<List<ImagePoint>> extracted = HalconContourConverter.Extract(xld);
                        if (extracted.Count > 0) return extracted;
                    }

                    return HalconRegionBuilder.ExtractRegionContours(modelRegion);
                }
            }
            catch (HOperatorException)
            {
                return new List<List<ImagePoint>>();
            }
            finally
            {
                if (modelRegion != null) modelRegion.Dispose();
            }
        }

        private static HShapeModel CreateShapeModel(
            HImage reduced,
            ShapeModelParameters parameters,
            out string buildMessage)
        {
            string metric = DefaultIfBlank(parameters.Metric, "use_polarity");
            HTuple contrastTuple = ResolveShapeContrast(reduced, parameters, metric, false, out buildMessage);
            if (!parameters.AutoThreshold)
            {
                return CreateShapeModelCore(
                    reduced,
                    parameters,
                    metric,
                    contrastTuple,
                    new HTuple(parameters.MinContrast));
            }

            try
            {
                return CreateShapeModelCore(
                    reduced,
                    parameters,
                    metric,
                    contrastTuple,
                    new HTuple(parameters.MinContrast));
            }
            catch (HOperatorException)
            {
                // 若滞后阈值过严导致边缘点数不足，回退到 HALCON 原生全自动对比度参数
                buildMessage = "图像域 Shape 建模完成（已回退至 HALCON 原生 auto 对比度）。";
                return CreateShapeModelCore(
                    reduced,
                    parameters,
                    metric,
                    new HTuple("auto"),
                    new HTuple("auto"));
            }
        }

        private static HShapeModel CreateShapeModelCore(
            HImage reduced,
            ShapeModelParameters parameters,
            string metric,
            HTuple contrastTuple,
            HTuple minContrastTuple)
        {
            HShapeModel model = new HShapeModel();
            try
            {
                using (contrastTuple)
                using (minContrastTuple)
                using (HTuple levels = AutoOrPositive(parameters.NumLevels))
                using (HTuple angleStep = AutoOrPositive(parameters.AngleStepRad))
                using (HTuple optimization = new HTuple(DefaultIfBlank(parameters.Optimization, "auto")))
                {
                    model.CreateShapeModel(
                        reduced,
                        levels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        angleStep,
                        optimization,
                        metric,
                        contrastTuple,
                        minContrastTuple);
                }

                return model;
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }

        private static HNCCModel CreateNccModel(HImage reduced, NccModelParameters parameters)
        {
            HNCCModel model = new HNCCModel();
            try
            {
                using (HTuple levels = AutoOrPositive(parameters.NumLevels))
                using (HTuple angleStep = AutoOrPositive(parameters.AngleStepRad))
                {
                    model.CreateNccModel(
                        reduced,
                        levels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        angleStep,
                        DefaultIfBlank(parameters.Metric, "use_polarity"));
                }

                return model;
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }

        private List<List<ImagePoint>> GetRelativeContours(TemplateDefinition definition, HShapeModel loadedShape)
        {
            if (loadedShape != null)
            {
                using (HXLDCont contours = loadedShape.GetShapeModelContours(1))
                {
                    return HalconContourConverter.Extract(contours);
                }
            }

            int width = definition.ReferenceImageWidth;
            int height = definition.ReferenceImageHeight;
            if (width <= 0 || height <= 0)
            {
                return new List<List<ImagePoint>>();
            }

            using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(definition, width, height))
            {
                return ToRelative(HalconRegionBuilder.ExtractRegionContours(effectiveRegion), definition.ModelReference);
            }
        }

        private static List<List<ImagePoint>> ToRelative(IList<List<ImagePoint>> absolute, ImagePose reference)
        {
            // 将图像绝对坐标先平移再逆旋转 -referenceAngle，转换到以 ModelReference 为原点的局部坐标系。
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            if (absolute == null) return result;
            double referenceRow = reference == null ? 0.0 : reference.Row;
            double referenceColumn = reference == null ? 0.0 : reference.Column;
            double referenceAngle = reference == null ? 0.0 : reference.AngleRadians;
            double cosine = Math.Cos(referenceAngle);
            double sine = Math.Sin(referenceAngle);

            foreach (List<ImagePoint> segment in absolute)
            {
                if (segment == null) continue;
                List<ImagePoint> relative = new List<ImagePoint>(segment.Count);
                foreach (ImagePoint point in segment)
                {
                    double deltaRow = point.Row - referenceRow;
                    double deltaColumn = point.Column - referenceColumn;
                    relative.Add(new ImagePoint(
                        (cosine * deltaRow) + (sine * deltaColumn),
                        (-sine * deltaRow) + (cosine * deltaColumn)));
                }

                if (relative.Count > 0) result.Add(relative);
            }

            return result;
        }

        private static ImagePoint TransformAnchor(TemplateDefinition definition, double row, double column, double angle)
        {
            // 先求参考锚点相对于 ModelReference 的局部偏移，再按匹配位姿 (row, column, angle) 刚体变换到当前图像。
            ImagePose reference = definition.ModelReference ?? new ImagePose(definition.DomainCenter.Row, definition.DomainCenter.Column, 0.0);
            double deltaRow = definition.ReferenceAnchor.Row - reference.Row;
            double deltaColumn = definition.ReferenceAnchor.Column - reference.Column;
            double referenceCosine = Math.Cos(reference.AngleRadians);
            double referenceSine = Math.Sin(reference.AngleRadians);
            double localRow = (referenceCosine * deltaRow) + (referenceSine * deltaColumn);
            double localColumn = (-referenceSine * deltaRow) + (referenceCosine * deltaColumn);

            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            return new ImagePoint(
                row + (cosine * localRow) - (sine * localColumn),
                column + (sine * localRow) + (cosine * localColumn));
        }

        private static void ValidateBuildParameters(TemplateDefinition definition)
        {
            if (definition.TemplateRoi == null) throw new ArgumentException("Template ROI is not configured.");
            if (definition.ShapeParameters == null) definition.ShapeParameters = new ShapeModelParameters();
            if (definition.NccParameters == null) definition.NccParameters = new NccModelParameters();
            if (definition.MatchParameters == null) definition.MatchParameters = new MatchParameters();

            if (definition.ModelType == TemplateModelType.Shape)
            {
                ShapeModelParameters parameters = definition.ShapeParameters;
                ValidateAngleRange(parameters.AngleStartRad, parameters.AngleExtentRad, "Shape model");
                if (parameters.NumLevels < 0) throw new ArgumentOutOfRangeException("NumLevels");
                if (parameters.AngleStepRad < 0.0 || !IsFinite(parameters.AngleStepRad)) throw new ArgumentOutOfRangeException("AngleStepRad");
                if (!parameters.AutoThreshold && parameters.Contrast <= 0)
                    throw new ArgumentOutOfRangeException("Contrast");
                if (parameters.Contrast < 0) throw new ArgumentOutOfRangeException("Contrast");
                if (parameters.MinContrast < 0) throw new ArgumentOutOfRangeException("MinContrast");
            }
            else if (definition.ModelType == TemplateModelType.Ncc)
            {
                NccModelParameters parameters = definition.NccParameters;
                ValidateAngleRange(parameters.AngleStartRad, parameters.AngleExtentRad, "NCC model");
                if (parameters.NumLevels < 0) throw new ArgumentOutOfRangeException("NumLevels");
                if (parameters.AngleStepRad < 0.0 || !IsFinite(parameters.AngleStepRad)) throw new ArgumentOutOfRangeException("AngleStepRad");
            }
            else
            {
                throw new ArgumentOutOfRangeException("ModelType");
            }
        }

        private static void ValidateLocateParameters(TemplateDefinition definition)
        {
            if (definition.IsModelDirty)
            {
                throw new ModelStateException(CalibrationErrorCode.TemplateModelDirty, "The template definition changed and the model must be rebuilt.");
            }

            if (definition.ModelData == null || definition.ModelData.Length == 0)
            {
                throw new ModelStateException(CalibrationErrorCode.TemplateMissing, "Template model data is missing.");
            }

            if (definition.MatchParameters == null) throw new ArgumentException("Match parameters are not configured.");
            MatchParameters parameters = definition.MatchParameters;
            ValidateAngleRange(parameters.AngleStartRad, parameters.AngleExtentRad, "Match");
            if (!IsFinite(parameters.MinScore) || parameters.MinScore < 0.0 || parameters.MinScore > 1.0) throw new ArgumentOutOfRangeException("MinScore");
            if (parameters.NumMatches < 0) throw new ArgumentOutOfRangeException("NumMatches");
            if (!IsFinite(parameters.MaxOverlap) || parameters.MaxOverlap < 0.0 || parameters.MaxOverlap > 1.0) throw new ArgumentOutOfRangeException("MaxOverlap");
            if (parameters.NumLevels < 0) throw new ArgumentOutOfRangeException("NumLevels");
            if (!IsFinite(parameters.Greediness) || parameters.Greediness < 0.0 || parameters.Greediness > 1.0) throw new ArgumentOutOfRangeException("Greediness");
            if (parameters.TimeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("TimeoutMilliseconds");
        }

        private void TrySetNativeTimeout(TemplateModelType modelType, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) return;
            try
            {
                using (HTuple name = new HTuple("timeout"))
                using (HTuple value = new HTuple(timeoutMilliseconds))
                {
                    if (modelType == TemplateModelType.Shape) _shapeModel.SetShapeModelParam(name, value);
                    else _nccModel.SetNccModelParam(name, value);
                }
            }
            catch (HOperatorException)
            {
                // 部分 HALCON 19.11 构建未开放原生单模型 timeout 参数；调用方仍会按已用时间执行软超时判定。
            }
        }

        private static string ModelKey(TemplateModelType modelType, string hash)
        {
            return ((int)modelType) + ":" + hash.Trim().ToUpperInvariant();
        }

        private void ClearCacheCore()
        {
            _cachedModelLength = -1;
            if (_shapeModel != null)
            {
                _shapeModel.Dispose();
                _shapeModel = null;
            }

            if (_nccModel != null)
            {
                _nccModel.Dispose();
                _nccModel = null;
            }

            _cachedModelKey = null;
            _cachedRelativeContours = new List<List<ImagePoint>>();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HalconTemplateEngine));
        }

        private static HTuple AutoOrPositive(int value)
        {
            return value <= 0 ? new HTuple("auto") : new HTuple(value);
        }

        private static HTuple AutoOrPositive(double value)
        {
            return value <= 0.0 ? new HTuple("auto") : new HTuple(value);
        }

        private static string NormalizeShapeSubPixel(string value)
        {
            return DefaultIfBlank(value, "least_squares");
        }

        private static string NormalizeNccSubPixel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "true";
            string normalized = value.Trim().ToLowerInvariant();
            return normalized == "false" || normalized == "none" || normalized == "0" ? "false" : "true";
        }

        private static string DefaultIfBlank(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        private static void ValidateAngleRange(double start, double extent, string name)
        {
            if (!IsFinite(start)) throw new ArgumentOutOfRangeException(name + " angle start");
            if (!IsFinite(extent) || Math.Abs(extent) < 1e-12 || Math.Abs(extent) > (Math.PI * 2.0 + 1e-9))
            {
                throw new ArgumentOutOfRangeException(name + " angle extent", "Angle extent must be non-zero and no greater than 2 PI.");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string TryGetHalconVersion()
        {
            try
            {
                using (HTuple version = HSystem.GetSystem("version"))
                {
                    return version.Length > 0 ? version[0].S : null;
                }
            }
            catch
            {
                return "19.11";
            }
        }

        private static CalibrationErrorCode MapErrorCode(Exception exception)
        {
            ModelStateException modelState = exception as ModelStateException;
            if (modelState != null) return modelState.ErrorCode;
            if (exception is HOperatorException) return CalibrationErrorCode.HalconRuntimeError;
            if (exception is ArgumentException || exception is ArgumentOutOfRangeException) return CalibrationErrorCode.ParameterInvalid;
            if (exception is InvalidOperationException) return CalibrationErrorCode.SerializationError;
            return CalibrationErrorCode.InternalError;
        }

        private static string FormatException(Exception exception)
        {
            HOperatorException halcon = exception as HOperatorException;
            if (halcon != null)
            {
                return "HALCON error " + halcon.GetErrorCode() + ": " + halcon.GetErrorMessage();
            }

            return exception.Message;
        }

        private static TemplateBuildResult Complete(TemplateBuildResult result, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private static TemplateMatchCollectionResult Complete(TemplateMatchCollectionResult result, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (result.Matches != null)
            {
                foreach (TemplateMatchResult match in result.Matches)
                {
                    match.ElapsedMilliseconds = result.ElapsedMilliseconds;
                }
            }

            return result;
        }

        private sealed class ModelStateException : InvalidOperationException
        {
            public ModelStateException(CalibrationErrorCode errorCode, string message)
                : base(message)
            {
                ErrorCode = errorCode;
            }

            public CalibrationErrorCode ErrorCode { get; private set; }
        }
    }
}
