using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// 使用有序、过滤后的 XLD 边缘轮廓构建新形状模板；
    /// 既有图像域形状模板与 NCC 模板仍委托给传统引擎保持兼容。
    /// </summary>
    public sealed class XldTemplateMatchingEngine :
        ITemplateMatchingEngine,
        IGrayImageTemplateMatchingEngine,
        IFeaturePreviewTemplateMatchingEngine
    {
        private const string XldMetric = "ignore_local_polarity";
        private readonly HalconTemplateEngine _legacyEngine = new HalconTemplateEngine();
        private readonly object _previewCacheSync = new object();
        private PreviewEdgeCacheEntry _previewEdgeCache;
        private bool _disposed;

        /// <summary>根据特征提取模式构建 XLD 轮廓模板或回退到传统图像域模板。</summary>
        public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            ThrowIfDisposed();
            if (definition == null || definition.ModelType != TemplateModelType.Shape)
            {
                return _legacyEngine.BuildTemplate(referenceImage, definition);
            }

            ShapeFeatureExtractionSettings extraction = definition.ShapeParameters == null
                ? null
                : definition.ShapeParameters.FeatureExtraction;
            if (extraction == null || extraction.Mode == ShapeFeatureExtractionMode.LegacyImageDomain)
            {
                return _legacyEngine.BuildTemplate(referenceImage, definition);
            }

            if (extraction.Mode != ShapeFeatureExtractionMode.EdgeContours)
            {
                return TemplateBuildResult.Failure(
                    CalibrationErrorCode.ParameterInvalid,
                    "Unsupported shape feature extraction mode: " + extraction.Mode + ".");
            }

            return BuildXldShapeTemplate(referenceImage, definition);
        }

        /// <summary>使用已转换的灰度图快速提取 XLD 或传统模式特征预览，跳过耗时建模与磁盘写入。</summary>
        public TemplateBuildResult PreviewFeatures(HalconGrayImage grayImage, TemplateDefinition definition)
        {
            ThrowIfDisposed();
            if (grayImage == null)
            {
                return TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Gray image is null.");
            }
            if (definition == null)
            {
                return TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null.");
            }

            if (definition.ModelType != TemplateModelType.Shape)
            {
                return _legacyEngine.PreviewFeatures(grayImage, definition);
            }

            ShapeFeatureExtractionSettings extraction = definition.ShapeParameters == null
                ? null
                : definition.ShapeParameters.FeatureExtraction;
            if (extraction == null || extraction.Mode == ShapeFeatureExtractionMode.LegacyImageDomain)
            {
                return _legacyEngine.PreviewFeatures(grayImage, definition);
            }

            if (extraction.Mode != ShapeFeatureExtractionMode.EdgeContours)
            {
                return TemplateBuildResult.Failure(
                    CalibrationErrorCode.ParameterInvalid,
                    "Unsupported shape feature extraction mode: " + extraction.Mode + ".");
            }

            return PreviewXldFast(grayImage, definition);
        }

        /// <summary>在图像中定位得分最高的单个模板匹配目标。</summary>
        public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            ThrowIfDisposed();
            return _legacyEngine.Locate(image, definition, includeContour);
        }

        /// <summary>在 Bitmap 图像中定位所有满足条件的匹配目标。</summary>
        public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            ThrowIfDisposed();
            return _legacyEngine.LocateAll(image, definition, includeContour);
        }

        /// <summary>复用已转换的 HALCON 灰度图定位所有满足条件的匹配目标。</summary>
        public TemplateMatchCollectionResult LocateAll(HalconGrayImage image, TemplateDefinition definition, bool includeContour = true)
        {
            ThrowIfDisposed();
            return _legacyEngine.LocateAll(image, definition, includeContour);
        }

        /// <summary>生成模板有效区域的质心、面积与边界轮廓预览。</summary>
        public TemplateRegionPreviewResult PreviewEffectiveRegion(
            TemplateDefinition definition,
            int imageWidth,
            int imageHeight)
        {
            ThrowIfDisposed();
            return _legacyEngine.PreviewEffectiveRegion(definition, imageWidth, imageHeight);
        }

        /// <summary>清除底层引擎缓存的 HALCON 模板句柄。</summary>
        public void ClearCache()
        {
            ThrowIfDisposed();
            lock (_previewCacheSync) _previewEdgeCache = null;
            _legacyEngine.ClearCache();
        }

        /// <summary>释放引擎持有的 HALCON 模型资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_previewCacheSync) _previewEdgeCache = null;
            _legacyEngine.Dispose();
        }

        private static TemplateBuildResult BuildXldShapeTemplate(
            Bitmap referenceImage,
            TemplateDefinition definition)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (referenceImage == null)
            {
                return Complete(
                    TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null."),
                    stopwatch);
            }

            if (definition == null)
            {
                return Complete(
                    TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null."),
                    stopwatch);
            }

            try
            {
                using (HImage gray = HalconImageConverter.ToGrayHImage(referenceImage))
                {
                    return BuildOrPreviewXldCore(
                        gray,
                        referenceImage.Width,
                        referenceImage.Height,
                        definition,
                        true,
                        stopwatch);
                }
            }
            catch (Exception exception)
            {
                return Complete(
                    TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)),
                    stopwatch);
            }
        }

        /// <summary>
        /// 快速预览路径：通过 ROI 局部裁剪提速 Canny，并对同一图像+ROI+提取参数缓存未掩膜基础轮廓；
        /// 仅修改掩膜（画笔/矩形/圆形/多边形/反选）时完全跳过阈值分析与 Canny，仅做一次向量化点区域过滤（约 1~3ms）。
        /// </summary>
        private TemplateBuildResult PreviewXldFast(HalconGrayImage grayImage, TemplateDefinition definition)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                TemplateDefinition output = definition.DeepClone();
                if (output.MatchParameters == null)
                {
                    output.MatchParameters = new MatchParameters();
                }

                ValidateShapeParameters(output);
                ShapeFeatureExtractionSettings extraction = Normalize(output.ShapeParameters.FeatureExtraction);
                output.ShapeParameters.FeatureExtraction = extraction;
                output.ShapeParameters.Metric = XldMetric;
                bool usedAutoThreshold = extraction.AutoThreshold || output.ShapeParameters.AutoThreshold;
                bool hasMasks = HasAnyMasks(output);

                PreviewCacheKey cacheKey = PreviewCacheKey.Create(grayImage, output.TemplateRoi, extraction, usedAutoThreshold);
                PreviewEdgeCacheEntry cached;
                lock (_previewCacheSync)
                {
                    cached = _previewEdgeCache != null && _previewEdgeCache.Key.Equals(cacheKey)
                        ? _previewEdgeCache
                        : null;
                }

                if (cached == null)
                {
                    TemplateDefinition unmasked = output.DeepClone();
                    unmasked.MaskRegions = new List<TemplateMaskShape>();
                    unmasked.EraseStrokes = new List<TemplateEraseStroke>();
                    unmasked.IsMaskInverted = false;

                    using (HRegion baseRegion = HalconRegionBuilder.BuildEffectiveRegion(
                        unmasked,
                        grayImage.Width,
                        grayImage.Height))
                    {
                        int cropRow, cropCol, cropW, cropH;
                        HalconRegionBuilder.GetRegionBoundingBox(
                            baseRegion,
                            grayImage.Width,
                            grayImage.Height,
                            8,
                            out cropRow,
                            out cropCol,
                            out cropW,
                            out cropH);
                        using (HImage cropped = grayImage.Image.CropPart(cropRow, cropCol, cropW, cropH))
                        using (HRegion localRegion = baseRegion.MoveRegion(-cropRow, -cropCol))
                        using (HImage reduced = cropped.ReduceDomain(localRegion))
                        {
                            double resolvedLow = extraction.EdgeLowThreshold;
                            double resolvedHigh = extraction.EdgeHighThreshold;
                            int resolvedContrast = output.ShapeParameters.Contrast;
                            int resolvedMinContrast = output.ShapeParameters.MinContrast;
                            if (usedAutoThreshold)
                            {
                                AdaptiveTemplateThresholds resolved = HalconAutoThresholdResolver.Resolve(
                                    reduced,
                                    output.ShapeParameters,
                                    XldMetric,
                                    true);
                                if (extraction.AutoThreshold)
                                {
                                    extraction.EdgeLowThreshold = resolved.EdgeLowThreshold;
                                    extraction.EdgeHighThreshold = resolved.EdgeHighThreshold;
                                }
                                if (output.ShapeParameters.AutoThreshold)
                                {
                                    output.ShapeParameters.MinContrast = resolved.MinContrast;
                                    output.ShapeParameters.Contrast = Math.Max(resolved.ContrastHigh, resolved.MinContrast);
                                }
                            }

                            RegionDefinition localRoi = OffsetRegion(output.TemplateRoi, -cropRow, -cropCol);
                            List<List<ImagePoint>> baseContours = XldFeatureExtractor.Extract(
                                reduced,
                                extraction,
                                localRoi);
                            if (cropRow != 0 || cropCol != 0)
                            {
                                baseContours = OffsetContours(baseContours, cropRow, cropCol);
                            }

                            resolvedLow = extraction.EdgeLowThreshold;
                            resolvedHigh = extraction.EdgeHighThreshold;
                            resolvedContrast = output.ShapeParameters.Contrast;
                            resolvedMinContrast = output.ShapeParameters.MinContrast;

                            double baseArea, baseRow, baseCol;
                            HalconRegionBuilder.GetAreaCenter(baseRegion, out baseArea, out baseRow, out baseCol);

                            cached = new PreviewEdgeCacheEntry(
                                cacheKey,
                                baseContours,
                                resolvedLow,
                                resolvedHigh,
                                resolvedContrast,
                                resolvedMinContrast,
                                baseArea,
                                baseRow,
                                baseCol);
                            lock (_previewCacheSync)
                            {
                                _previewEdgeCache = cached;
                            }
                        }
                    }
                }

                if (extraction.AutoThreshold)
                {
                    extraction.EdgeLowThreshold = cached.EdgeLowThreshold;
                    extraction.EdgeHighThreshold = cached.EdgeHighThreshold;
                }
                if (output.ShapeParameters.AutoThreshold)
                {
                    output.ShapeParameters.Contrast = cached.Contrast;
                    output.ShapeParameters.MinContrast = cached.MinContrast;
                }

                double effectiveArea = cached.BaseArea;
                double domainRow = cached.BaseDomainRow;
                double domainColumn = cached.BaseDomainColumn;
                List<List<ImagePoint>> resultContours;
                if (hasMasks)
                {
                    using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(
                        output,
                        grayImage.Width,
                        grayImage.Height))
                    {
                        HalconRegionBuilder.GetAreaCenter(
                            effectiveRegion,
                            out effectiveArea,
                            out domainRow,
                            out domainColumn);
                        resultContours = FilterContoursByRegion(cached.BaseContours, effectiveRegion);
                    }

                    if (resultContours.Count == 0)
                    {
                        return Complete(
                            TemplateBuildResult.Failure(
                                CalibrationErrorCode.ParameterInvalid,
                                "掩膜过滤后当前模板区域未剩余可用特征点。"),
                            stopwatch);
                    }
                }
                else
                {
                    resultContours = CloneContours(cached.BaseContours);
                }

                bool anchorWasUninitialized = output.ReferenceImageWidth <= 0
                    && output.ReferenceImageHeight <= 0
                    && output.ReferenceAnchor.Row == 0.0
                    && output.ReferenceAnchor.Column == 0.0
                    && !output.IsReferenceAnchorLocked;
                output.ReferenceImageWidth = grayImage.Width;
                output.ReferenceImageHeight = grayImage.Height;
                output.DomainCenter = new ImagePoint(domainRow, domainColumn);
                ImagePoint templateCenter = new ImagePoint(
                    output.TemplateRoi.CenterRow,
                    output.TemplateRoi.CenterColumn);
                output.ModelReference = new ImagePose(templateCenter.Row, templateCenter.Column, 0.0);
                if (anchorWasUninitialized)
                {
                    output.ReferenceAnchor = templateCenter;
                }

                string message = usedAutoThreshold
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "已预览 {0} 段有序 XLD 轮廓（自适应阈值 Canny {1:0.#}/{2:0.#}，最小对比度 {3}）。",
                        resultContours.Count,
                        extraction.EdgeLowThreshold,
                        extraction.EdgeHighThreshold,
                        output.ShapeParameters.MinContrast)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "Previewed {0} ordered XLD contour segments.",
                        resultContours.Count);

                return Complete(new TemplateBuildResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = message,
                    Template = output,
                    EffectiveArea = effectiveArea,
                    ContourSegments = resultContours
                }, stopwatch);
            }
            catch (Exception exception)
            {
                return Complete(
                    TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)),
                    stopwatch);
            }
        }

        private static bool HasAnyMasks(TemplateDefinition definition)
        {
            return definition.IsMaskInverted ||
                   (definition.MaskRegions != null && definition.MaskRegions.Count > 0) ||
                   (definition.EraseStrokes != null && definition.EraseStrokes.Count > 0);
        }

        private static List<List<ImagePoint>> FilterContoursByRegion(
            IList<List<ImagePoint>> contours,
            HRegion region)
        {
            List<List<ImagePoint>> filtered = new List<List<ImagePoint>>();
            if (contours == null || contours.Count == 0) return filtered;

            int r1, c1, r2, c2;
            region.SmallestRectangle1(out r1, out c1, out r2, out c2);
            int width = c2 - c1 + 1;
            int height = r2 - r1 + 1;
            if (width <= 0 || height <= 0) return filtered;

            byte[] mask = new byte[width * height];
            HTuple runRows = null;
            HTuple colBegins = null;
            HTuple colEnds = null;
            try
            {
                region.GetRegionRuns(out runRows, out colBegins, out colEnds);
                int runCount = Math.Min(runRows.Length, Math.Min(colBegins.Length, colEnds.Length));
                for (int i = 0; i < runCount; i++)
                {
                    int localRow = runRows[i].I - r1;
                    if (localRow < 0 || localRow >= height) continue;
                    int startCol = Math.Max(0, colBegins[i].I - c1);
                    int endCol = Math.Min(width - 1, colEnds[i].I - c1);
                    int rowBase = localRow * width;
                    for (int c = startCol; c <= endCol; c++)
                    {
                        mask[rowBase + c] = 1;
                    }
                }
            }
            finally
            {
                if (runRows != null) runRows.Dispose();
                if (colBegins != null) colBegins.Dispose();
                if (colEnds != null) colEnds.Dispose();
            }

            for (int i = 0; i < contours.Count; i++)
            {
                List<ImagePoint> seg = contours[i];
                if (seg == null) continue;
                List<ImagePoint> current = null;
                for (int j = 0; j < seg.Count; j++)
                {
                    int lr = (int)Math.Round(seg[j].Row) - r1;
                    int lc = (int)Math.Round(seg[j].Column) - c1;
                    bool isInside = lr >= 0 && lr < height && lc >= 0 && lc < width && mask[(lr * width) + lc] != 0;
                    if (isInside)
                    {
                        if (current == null) current = new List<ImagePoint>();
                        current.Add(seg[j]);
                    }
                    else if (current != null)
                    {
                        if (current.Count >= 2) filtered.Add(current);
                        current = null;
                    }
                }
                if (current != null && current.Count >= 2) filtered.Add(current);
            }

            return filtered;
        }

        private static RegionDefinition OffsetRegion(RegionDefinition source, int rowOffset, int columnOffset)
        {
            RegionDefinition copy = source.DeepClone();
            copy.CenterRow += rowOffset;
            copy.CenterColumn += columnOffset;
            if (copy.Points != null)
            {
                for (int i = 0; i < copy.Points.Count; i++)
                {
                    copy.Points[i] = new ImagePoint(copy.Points[i].Row + rowOffset, copy.Points[i].Column + columnOffset);
                }
            }
            return copy;
        }

        private static List<List<ImagePoint>> OffsetContours(IList<List<ImagePoint>> contours, int rowOffset, int columnOffset)
        {
            List<List<ImagePoint>> shifted = new List<List<ImagePoint>>(contours.Count);
            for (int i = 0; i < contours.Count; i++)
            {
                List<ImagePoint> seg = contours[i];
                if (seg == null || seg.Count == 0) continue;
                List<ImagePoint> copy = new List<ImagePoint>(seg.Count);
                for (int j = 0; j < seg.Count; j++)
                {
                    copy.Add(new ImagePoint(seg[j].Row + rowOffset, seg[j].Column + columnOffset));
                }
                shifted.Add(copy);
            }
            return shifted;
        }

        private static List<List<ImagePoint>> CloneContours(IList<List<ImagePoint>> contours)
        {
            List<List<ImagePoint>> copy = new List<List<ImagePoint>>(contours.Count);
            for (int i = 0; i < contours.Count; i++)
            {
                if (contours[i] != null) copy.Add(new List<ImagePoint>(contours[i]));
            }
            return copy;
        }

        private static TemplateBuildResult BuildOrPreviewXld(
            HImage gray,
            int imageWidth,
            int imageHeight,
            TemplateDefinition definition,
            bool buildModel)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (gray == null)
            {
                return Complete(
                    TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Gray image is null."),
                    stopwatch);
            }

            return BuildOrPreviewXldCore(
                gray,
                imageWidth,
                imageHeight,
                definition,
                buildModel,
                stopwatch);
        }

        private static TemplateBuildResult BuildOrPreviewXldCore(
            HImage gray,
            int imageWidth,
            int imageHeight,
            TemplateDefinition definition,
            bool buildModel,
            Stopwatch stopwatch)
        {
            HShapeModel shapeModel = null;
            try
            {
                TemplateDefinition output = definition.DeepClone();
                if (output.MatchParameters == null)
                {
                    output.MatchParameters = new MatchParameters();
                }

                ValidateShapeParameters(output);
                ShapeFeatureExtractionSettings extraction = Normalize(output.ShapeParameters.FeatureExtraction);
                output.ShapeParameters.FeatureExtraction = extraction;
                output.ShapeParameters.Metric = XldMetric;

                using (HRegion effectiveRegion = HalconRegionBuilder.BuildEffectiveRegion(
                    output,
                    imageWidth,
                    imageHeight))
                {
                    double effectiveArea;
                    double domainRow;
                    double domainColumn;
                    HalconRegionBuilder.GetAreaCenter(
                        effectiveRegion,
                        out effectiveArea,
                        out domainRow,
                        out domainColumn);

                    bool anchorWasUninitialized = output.ReferenceImageWidth <= 0
                        && output.ReferenceImageHeight <= 0
                        && output.ReferenceAnchor.Row == 0.0
                        && output.ReferenceAnchor.Column == 0.0
                        && !output.IsReferenceAnchorLocked;

                    output.ReferenceImageWidth = imageWidth;
                    output.ReferenceImageHeight = imageHeight;
                    output.DomainCenter = new ImagePoint(domainRow, domainColumn);
                    // 模型参考中心必须固定为用户定义的 ROI 中心；
                    // 旋转 ROI 被图像边缘裁剪或存在掩膜挖孔时，有效域质心会发生偏移，导致特征预览漂移。
                    ImagePoint templateCenter = new ImagePoint(
                        output.TemplateRoi.CenterRow,
                        output.TemplateRoi.CenterColumn);
                    output.ModelReference = new ImagePose(
                        templateCenter.Row,
                        templateCenter.Column,
                        0.0);
                    if (anchorWasUninitialized)
                    {
                        output.ReferenceAnchor = templateCenter;
                    }

                    bool usedAutoThreshold = extraction.AutoThreshold || output.ShapeParameters.AutoThreshold;
                    int cropRow, cropCol, cropW, cropH;
                    HalconRegionBuilder.GetRegionBoundingBox(
                        effectiveRegion,
                        imageWidth,
                        imageHeight,
                        8,
                        out cropRow,
                        out cropCol,
                        out cropW,
                        out cropH);
                    List<List<ImagePoint>> sampledContours;
                    using (HImage cropped = gray.CropPart(cropRow, cropCol, cropW, cropH))
                    using (HRegion localRegion = effectiveRegion.MoveRegion(-cropRow, -cropCol))
                    using (HImage reduced = cropped.ReduceDomain(localRegion))
                    {
                        if (usedAutoThreshold)
                        {
                            AdaptiveTemplateThresholds resolved = HalconAutoThresholdResolver.Resolve(
                                reduced,
                                output.ShapeParameters,
                                output.ShapeParameters.Metric,
                                !buildModel);
                            if (extraction.AutoThreshold)
                            {
                                extraction.EdgeLowThreshold = resolved.EdgeLowThreshold;
                                extraction.EdgeHighThreshold = resolved.EdgeHighThreshold;
                            }
                            if (output.ShapeParameters.AutoThreshold)
                            {
                                output.ShapeParameters.MinContrast = resolved.MinContrast;
                                output.ShapeParameters.Contrast = Math.Max(resolved.ContrastHigh, resolved.MinContrast);
                            }
                        }

                        RegionDefinition localRoi = OffsetRegion(output.TemplateRoi, -cropRow, -cropCol);
                        sampledContours = XldFeatureExtractor.Extract(
                            reduced,
                            extraction,
                            localRoi);
                        if (cropRow != 0 || cropCol != 0)
                        {
                            sampledContours = OffsetContours(sampledContours, cropRow, cropCol);
                        }
                    }
                    if (buildModel)
                    {
                        using (HXLDCont xldContours = XldFeatureExtractor.CreateXld(sampledContours))
                        {
                            shapeModel = CreateShapeModel(xldContours, output.ShapeParameters);
                        }

                        SetModelOrigin(
                            shapeModel,
                            sampledContours,
                            templateCenter.Row,
                            templateCenter.Column);

                        HShapeModel writeModel = shapeModel;
                        byte[] modelData = HalconModelFile.Write(".shm", writeModel.WriteShapeModel);
                        output.SetModelData(modelData, TryGetHalconVersion());
                    }

                    string actionPrefix = buildModel ? "已提取" : "已预览";
                    string message = usedAutoThreshold
                        ? string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} {1} 段有序 XLD 轮廓（自适应阈值 Canny {2:0.#}/{3:0.#}，最小对比度 {4}）。",
                            actionPrefix,
                            sampledContours.Count,
                            extraction.EdgeLowThreshold,
                            extraction.EdgeHighThreshold,
                            output.ShapeParameters.MinContrast)
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            buildModel
                                ? "Template model was created from {0} ordered XLD contour segments."
                                : "Previewed {0} ordered XLD contour segments.",
                            sampledContours.Count);

                    return Complete(new TemplateBuildResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Message = message,
                        Template = output,
                        EffectiveArea = effectiveArea,
                        // 编辑器预览保留图像坐标下的原始采样点；GetShapeModelContours() 返回的是应用模型原点后的局部坐标，直接显示会偏移。
                        ContourSegments = sampledContours
                    }, stopwatch);
                }
            }
            catch (Exception exception)
            {
                return Complete(
                    TemplateBuildResult.Failure(MapErrorCode(exception), FormatException(exception)),
                    stopwatch);
            }
            finally
            {
                if (shapeModel != null) shapeModel.Dispose();
            }
        }

        private static HShapeModel CreateShapeModel(
            HXLDCont contours,
            ShapeModelParameters parameters)
        {
            HShapeModel model = new HShapeModel();
            try
            {
                using (HTuple levels = AutoOrPositive(parameters.NumLevels))
                using (HTuple angleStep = AutoOrPositive(parameters.AngleStepRad))
                using (HTuple optimization = new HTuple(DefaultIfBlank(parameters.Optimization, "auto")))
                {
                    model.CreateShapeModelXld(
                        contours,
                        levels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        angleStep,
                        optimization,
                        parameters.Metric,
                        parameters.MinContrast);
                }

                return model;
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }

        private static void SetModelOrigin(
            HShapeModel model,
            IList<List<ImagePoint>> contours,
            double targetRow,
            double targetColumn)
        {
            bool hasPoint = false;
            double minRow = double.MaxValue;
            double maxRow = double.MinValue;
            double minColumn = double.MaxValue;
            double maxColumn = double.MinValue;

            foreach (List<ImagePoint> segment in contours ?? new List<List<ImagePoint>>())
            {
                if (segment == null) continue;
                foreach (ImagePoint point in segment)
                {
                    hasPoint = true;
                    minRow = Math.Min(minRow, point.Row);
                    maxRow = Math.Max(maxRow, point.Row);
                    minColumn = Math.Min(minColumn, point.Column);
                    maxColumn = Math.Max(maxColumn, point.Column);
                }
            }

            if (!hasPoint)
            {
                throw new XldFeatureExtractionException("No valid XLD feature points were extracted.");
            }

            double defaultOriginRow = (minRow + maxRow) * 0.5;
            double defaultOriginColumn = (minColumn + maxColumn) * 0.5;
            // CreateShapeModelXld 默认以轮廓外接框中心为原点，此处偏移使其对齐到模板 ROI 中心。
            model.SetShapeModelOrigin(
                targetRow - defaultOriginRow,
                targetColumn - defaultOriginColumn);
        }

        private static ShapeFeatureExtractionSettings Normalize(ShapeFeatureExtractionSettings source)
        {
            ShapeFeatureExtractionSettings defaults = new ShapeFeatureExtractionSettings();
            ShapeFeatureExtractionSettings output = source == null
                ? defaults
                : source.DeepClone();

            output.Mode = ShapeFeatureExtractionMode.EdgeContours;
            if (!IsFinite(output.EdgeSigma) || output.EdgeSigma <= 0.0)
                output.EdgeSigma = defaults.EdgeSigma;
            if (!output.AutoThreshold)
            {
                if (!IsFinite(output.EdgeLowThreshold) || output.EdgeLowThreshold <= 0.0)
                    output.EdgeLowThreshold = defaults.EdgeLowThreshold;
                if (!IsFinite(output.EdgeHighThreshold) || output.EdgeHighThreshold <= 0.0)
                    output.EdgeHighThreshold = defaults.EdgeHighThreshold;
                if (output.EdgeHighThreshold <= output.EdgeLowThreshold)
                {
                    throw new ArgumentException(
                        "Edge high threshold must be greater than the low threshold.",
                        "FeatureExtraction.EdgeHighThreshold");
                }
            }
            if (!IsFinite(output.MinimumContourLengthPixels) || output.MinimumContourLengthPixels <= 0.0)
                output.MinimumContourLengthPixels = defaults.MinimumContourLengthPixels;
            if (!IsFinite(output.PointSpacingPixels) || output.PointSpacingPixels <= 0.0)
                output.PointSpacingPixels = defaults.PointSpacingPixels;
            if (output.MaximumPointCount < 4)
                output.MaximumPointCount = defaults.MaximumPointCount;
            return output;
        }

        private static void ValidateShapeParameters(TemplateDefinition definition)
        {
            if (definition.TemplateRoi == null)
                throw new ArgumentException("Template ROI is not configured.");
            if (definition.ShapeParameters == null)
                throw new ArgumentException("Shape model parameters are not configured.");

            ShapeModelParameters parameters = definition.ShapeParameters;
            ValidateAngleRange(parameters.AngleStartRad, parameters.AngleExtentRad);
            if (parameters.NumLevels < 0) throw new ArgumentOutOfRangeException("NumLevels");
            if (!IsFinite(parameters.AngleStepRad) || parameters.AngleStepRad < 0.0)
                throw new ArgumentOutOfRangeException("AngleStepRad");
            if (parameters.MinContrast < 0) throw new ArgumentOutOfRangeException("MinContrast");
        }

        private static void ValidateAngleRange(double start, double extent)
        {
            if (!IsFinite(start)) throw new ArgumentOutOfRangeException("AngleStartRad");
            if (!IsFinite(extent)
                || Math.Abs(extent) < 1e-12
                || Math.Abs(extent) > Math.PI * 2.0 + 1e-9)
            {
                throw new ArgumentOutOfRangeException(
                    "AngleExtentRad",
                    "Angle extent must be non-zero and no greater than 2 PI.");
            }
        }

        private static HTuple AutoOrPositive(int value)
        {
            return value <= 0 ? new HTuple("auto") : new HTuple(value);
        }

        private static HTuple AutoOrPositive(double value)
        {
            return value <= 0.0 ? new HTuple("auto") : new HTuple(value);
        }

        private static string DefaultIfBlank(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
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
            if (exception is HOperatorException) return CalibrationErrorCode.HalconRuntimeError;
            if (exception is XldFeatureExtractionException) return CalibrationErrorCode.ParameterInvalid;
            if (exception is ArgumentException) return CalibrationErrorCode.ParameterInvalid;
            if (exception is InvalidOperationException) return CalibrationErrorCode.SerializationError;
            return CalibrationErrorCode.InternalError;
        }

        private static string FormatException(Exception exception)
        {
            HOperatorException halcon = exception as HOperatorException;
            return halcon == null
                ? exception.Message
                : "HALCON error " + halcon.GetErrorCode() + ": " + halcon.GetErrorMessage();
        }

        private static TemplateBuildResult Complete(TemplateBuildResult result, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(XldTemplateMatchingEngine));
        }

        private sealed class PreviewEdgeCacheEntry
        {
            public PreviewEdgeCacheEntry(
                PreviewCacheKey key,
                List<List<ImagePoint>> baseContours,
                double edgeLowThreshold,
                double edgeHighThreshold,
                int contrast,
                int minContrast,
                double baseArea,
                double baseDomainRow,
                double baseDomainColumn)
            {
                Key = key;
                BaseContours = baseContours;
                EdgeLowThreshold = edgeLowThreshold;
                EdgeHighThreshold = edgeHighThreshold;
                Contrast = contrast;
                MinContrast = minContrast;
                BaseArea = baseArea;
                BaseDomainRow = baseDomainRow;
                BaseDomainColumn = baseDomainColumn;
            }

            public PreviewCacheKey Key { get; private set; }
            public List<List<ImagePoint>> BaseContours { get; private set; }
            public double EdgeLowThreshold { get; private set; }
            public double EdgeHighThreshold { get; private set; }
            public int Contrast { get; private set; }
            public int MinContrast { get; private set; }
            public double BaseArea { get; private set; }
            public double BaseDomainRow { get; private set; }
            public double BaseDomainColumn { get; private set; }
        }

        private struct PreviewCacheKey : IEquatable<PreviewCacheKey>
        {
            public long ImageCacheId;
            public int Width;
            public int Height;
            public RegionKind RoiKind;
            public double CenterRow;
            public double CenterColumn;
            public double PhiRadians;
            public double Length1;
            public double Length2;
            public double Radius;
            public bool AutoThreshold;
            public double EdgeSigma;
            public double ManualLow;
            public double ManualHigh;
            public double MinContourLength;
            public double PointSpacing;
            public int MaxPointCount;

            public static PreviewCacheKey Create(
                HalconGrayImage grayImage,
                RegionDefinition roi,
                ShapeFeatureExtractionSettings extraction,
                bool autoThreshold)
            {
                return new PreviewCacheKey
                {
                    ImageCacheId = grayImage.CacheId,
                    Width = grayImage.Width,
                    Height = grayImage.Height,
                    RoiKind = roi.Kind,
                    CenterRow = Math.Round(roi.CenterRow, 4),
                    CenterColumn = Math.Round(roi.CenterColumn, 4),
                    PhiRadians = Math.Round(roi.PhiRadians, 6),
                    Length1 = Math.Round(roi.Length1, 4),
                    Length2 = Math.Round(roi.Length2, 4),
                    Radius = Math.Round(roi.Radius, 4),
                    AutoThreshold = autoThreshold,
                    EdgeSigma = Math.Round(extraction.EdgeSigma, 4),
                    ManualLow = autoThreshold ? 0.0 : Math.Round(extraction.EdgeLowThreshold, 4),
                    ManualHigh = autoThreshold ? 0.0 : Math.Round(extraction.EdgeHighThreshold, 4),
                    MinContourLength = Math.Round(extraction.MinimumContourLengthPixels, 4),
                    PointSpacing = Math.Round(extraction.PointSpacingPixels, 4),
                    MaxPointCount = extraction.MaximumPointCount
                };
            }

            public bool Equals(PreviewCacheKey other)
            {
                return ImageCacheId == other.ImageCacheId &&
                       Width == other.Width &&
                       Height == other.Height &&
                       RoiKind == other.RoiKind &&
                       CenterRow == other.CenterRow &&
                       CenterColumn == other.CenterColumn &&
                       PhiRadians == other.PhiRadians &&
                       Length1 == other.Length1 &&
                       Length2 == other.Length2 &&
                       Radius == other.Radius &&
                       AutoThreshold == other.AutoThreshold &&
                       EdgeSigma == other.EdgeSigma &&
                       ManualLow == other.ManualLow &&
                       ManualHigh == other.ManualHigh &&
                       MinContourLength == other.MinContourLength &&
                       PointSpacing == other.PointSpacing &&
                       MaxPointCount == other.MaxPointCount;
            }
        }
    }

    /// <summary>从有效模板区域提取、筛选并按比例抽样有序 XLD 边缘轮廓。</summary>
    internal static class XldFeatureExtractor
    {
        /// <summary>使用 Canny 亚像素边缘提取并对轮廓进行长度过滤、评分排序与比例抽样。</summary>
        public static List<List<ImagePoint>> Extract(
            HImage reducedImage,
            ShapeFeatureExtractionSettings settings,
            RegionDefinition templateRoi)
        {
            if (reducedImage == null) throw new ArgumentNullException(nameof(reducedImage));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (templateRoi == null) throw new ArgumentNullException(nameof(templateRoi));

            List<List<ImagePoint>> sampled = TryExtractWithThresholds(
                reducedImage,
                settings,
                templateRoi,
                settings.EdgeLowThreshold,
                settings.EdgeHighThreshold);
            if (sampled != null)
            {
                return sampled;
            }

            if (settings.AutoThreshold)
            {
                // 若主自适应阈值在极弱对比或细线条场景未抓到足够轮廓，自动放宽一次门限并回写实测阈值
                double relaxedLow = Math.Max(2.0, Math.Round(settings.EdgeLowThreshold * 0.55, 1));
                double relaxedHigh = Math.Max(relaxedLow + 2.0, Math.Round(settings.EdgeHighThreshold * 0.55, 1));
                sampled = TryExtractWithThresholds(
                    reducedImage,
                    settings,
                    templateRoi,
                    relaxedLow,
                    relaxedHigh);
                if (sampled != null)
                {
                    settings.EdgeLowThreshold = relaxedLow;
                    settings.EdgeHighThreshold = relaxedHigh;
                    return sampled;
                }
            }

            throw new XldFeatureExtractionException(
                "No usable Canny XLD contours were found in the effective template ROI.");
        }

        private static List<List<ImagePoint>> TryExtractWithThresholds(
            HImage reducedImage,
            ShapeFeatureExtractionSettings settings,
            RegionDefinition templateRoi,
            double edgeLowThreshold,
            double edgeHighThreshold)
        {
            // 通过 Canny 亚像素边缘提取并剔除过短碎段，再按长度与局部覆盖面积综合评分降序筛选。
            double contourSelectionLength = Math.Max(
                settings.MinimumContourLengthPixels,
                Math.Max(6.0, settings.PointSpacingPixels * 1.5));
            using (HTuple low = new HTuple(edgeLowThreshold))
            using (HTuple high = new HTuple(edgeHighThreshold))
            using (HXLDCont edges = reducedImage.EdgesSubPix("canny", settings.EdgeSigma, low, high))
            using (HXLDCont selected = edges.SelectShapeXld(
                "contlength",
                "and",
                contourSelectionLength,
                1.0e30))
            {
                List<ContourSource> sources = ClipContoursToRoi(
                        HalconContourConverter.Extract(selected),
                        templateRoi)
                    .Select((points, index) => CreateContourSource(points, index, templateRoi))
                    .Where(item => item.Points != null
                        && item.Points.Count >= 4
                        && item.Length >= Math.Max(8.0, settings.PointSpacingPixels * 2.0))
                    .OrderByDescending(item => item.Score)
                    .ToList();

                if (sources.Count == 0)
                {
                    return null;
                }

                double modelPointSpacing = Math.Max(0.5, settings.PointSpacingPixels * 0.15);
                List<List<ImagePoint>> sampled = BuildProportionalPointListFromSources(
                    sources,
                    modelPointSpacing,
                    settings.MaximumPointCount);

                if (sampled.Count == 0 || CountPoints(sampled) < 4)
                {
                    return null;
                }

                return sampled;
            }
        }

        private static IEnumerable<List<ImagePoint>> ClipContoursToRoi(
            IEnumerable<List<ImagePoint>> contours,
            RegionDefinition roi)
        {
            foreach (List<ImagePoint> contour in contours ?? Enumerable.Empty<List<ImagePoint>>())
            {
                if (contour == null || contour.Count < 2)
                {
                    continue;
                }

                List<ImagePoint> inside = new List<ImagePoint>();
                foreach (ImagePoint point in contour)
                {
                    if (IsInsideRoi(point, roi))
                    {
                        inside.Add(point);
                    }
                }

                // HALCON 的缩减定义域通常已裁剪掉外部边缘；
                // 当少数采样点略超出 ROI 时仍保留整条有序候选轮廓，避免在边界处切碎导致板边丢失。
                if (inside.Count >= 2)
                {
                    yield return inside;
                }
            }
        }

        private static bool IsInsideRoi(ImagePoint point, RegionDefinition roi)
        {
            // 转换到 ROI 局部坐标系（PhiRadians 为 HALCON 逆时针弧度角）判断点是否落在容差范围内。
            double deltaRow = point.Row - roi.CenterRow;
            double deltaColumn = point.Column - roi.CenterColumn;
            double cosine = Math.Cos(roi.PhiRadians);
            double sine = Math.Sin(roi.PhiRadians);
            double localRow = (cosine * deltaRow) + (sine * deltaColumn);
            double localColumn = (-sine * deltaRow) + (cosine * deltaColumn);
            const double tolerance = 1.5;
            return Math.Abs(localColumn) <= roi.Length1 + tolerance
                && Math.Abs(localRow) <= roi.Length2 + tolerance;
        }

        private static void GetLocalCoordinates(
            ImagePoint point,
            RegionDefinition roi,
            out double localRow,
            out double localColumn)
        {
            double deltaRow = point.Row - roi.CenterRow;
            double deltaColumn = point.Column - roi.CenterColumn;
            double cosine = Math.Cos(roi.PhiRadians);
            double sine = Math.Sin(roi.PhiRadians);
            localRow = (cosine * deltaRow) + (sine * deltaColumn);
            localColumn = (-sine * deltaRow) + (cosine * deltaColumn);
        }

        private static ContourSource CreateContourSource(
            List<ImagePoint> points,
            int index,
            RegionDefinition roi)
        {
            ContourSource source = new ContourSource
            {
                Index = index,
                Points = points,
                Length = GetLength(points)
            };
            if (points == null || points.Count == 0)
            {
                return source;
            }

            double minRow = double.PositiveInfinity;
            double maxRow = double.NegativeInfinity;
            double minColumn = double.PositiveInfinity;
            double maxColumn = double.NegativeInfinity;
            foreach (ImagePoint point in points)
            {
                double localRow;
                double localColumn;
                GetLocalCoordinates(point, roi, out localRow, out localColumn);
                minRow = Math.Min(minRow, localRow);
                maxRow = Math.Max(maxRow, localRow);
                minColumn = Math.Min(minColumn, localColumn);
                maxColumn = Math.Max(maxColumn, localColumn);
            }

            source.Coverage = Math.Max(0.0, maxRow - minRow)
                * Math.Max(0.0, maxColumn - minColumn);
            return source;
        }

        /// <summary>将有序点段集合拼接构造为单个 HALCON HXLDCont 多对象容器。</summary>
        public static HXLDCont CreateXld(IList<List<ImagePoint>> segments)
        {
            HXLDCont combined = null;
            try
            {
                foreach (List<ImagePoint> segment in segments ?? new List<List<ImagePoint>>())
                {
                    if (segment == null || segment.Count < 2) continue;
                    double[] rows = segment.Select(point => point.Row).ToArray();
                    double[] columns = segment.Select(point => point.Column).ToArray();
                    using (HTuple rowTuple = new HTuple(rows))
                    using (HTuple columnTuple = new HTuple(columns))
                    using (HXLDCont contour = new HXLDCont(rowTuple, columnTuple))
                    {
                        if (combined == null)
                        {
                            combined = contour.CopyObj(1, -1);
                        }
                        else
                        {
                            HXLDCont next = combined.ConcatObj(contour);
                            combined.Dispose();
                            combined = next;
                        }
                    }
                }

                if (combined == null)
                {
                    throw new XldFeatureExtractionException(
                        "No XLD contours are available for model creation.");
                }

                return combined;
            }
            catch
            {
                if (combined != null) combined.Dispose();
                throw;
            }
        }

        /// <summary>按最小点距与最大点数上限对单条有序轮廓进行等距重采样。</summary>
        internal static List<ImagePoint> ResampleOrderedContour(
            IList<ImagePoint> points,
            double spacing,
            int maximumCount = int.MaxValue)
        {
            List<ImagePoint> result = new List<ImagePoint>();
            if (points == null || points.Count == 0 || maximumCount <= 0)
            {
                return result;
            }

            double minimumDistance = Math.Max(0.0, spacing);
            double minimumDistanceSquared = minimumDistance * minimumDistance;
            result.Add(points[0]);
            for (int index = 1; index < points.Count; index++)
            {
                ImagePoint point = points[index];
                double distance = result[result.Count - 1].DistanceTo(point);
                if (distance * distance >= minimumDistanceSquared)
                {
                    result.Add(point);
                }
            }

            if (result.Count > maximumCount)
            {
                if (maximumCount == 1)
                {
                    return new List<ImagePoint> { result[0] };
                }

                List<ImagePoint> limited = new List<ImagePoint>(maximumCount);
                double step = (double)(result.Count - 1) / (maximumCount - 1);
                for (int index = 0; index < maximumCount; index++)
                {
                    limited.Add(result[(int)Math.Round(index * step)]);
                }

                result = limited;
            }

            return result;
        }

        private static List<List<ImagePoint>> BuildProportionalPointListFromSources(
            IList<ContourSource> sources,
            double spacing,
            int maximumPointCount)
        {
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            if (sources == null || sources.Count == 0 || maximumPointCount < 2)
            {
                return result;
            }

            int maximumSegmentCount = Math.Max(
                16,
                Math.Min(2000, maximumPointCount / 4));
            List<ContourSource> ordered = sources
                .Where(source => source != null
                    && source.Points != null
                    && source.Points.Count >= 2)
                .OrderByDescending(source => source.Score)
                .Take(maximumSegmentCount)
                .ToList();
            if (ordered.Count == 0)
            {
                return result;
            }

            double totalLength = ordered.Sum(source => Math.Max(1.0, source.Length));
            foreach (ContourSource source in ordered)
            {
                int maximumForSegment = Math.Max(
                    4,
                    (int)Math.Round(
                        maximumPointCount * Math.Max(1.0, source.Length) / totalLength));
                List<ImagePoint> sampled = ResampleOrderedContour(
                    source.Points,
                    spacing,
                    maximumForSegment);
                if (sampled.Count < 2)
                {
                    continue;
                }

                result.Add(sampled);
                if (CountPoints(result) >= maximumPointCount)
                {
                    break;
                }
            }

            return LimitPointCountProportionally(result, maximumPointCount);
        }

        private static List<List<ImagePoint>> LimitPointCountProportionally(
            IList<List<ImagePoint>> segments,
            int maximumPointCount)
        {
            int total = CountPoints(segments);
            if (total <= maximumPointCount)
            {
                return segments.Select(segment => new List<ImagePoint>(segment)).ToList();
            }

            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            foreach (List<ImagePoint> segment in segments)
            {
                int keep = Math.Max(
                    2,
                    (int)Math.Round((double)segment.Count * maximumPointCount / total));
                List<ImagePoint> sampled = ResampleOrderedContour(segment, 1.0, keep);
                if (sampled.Count >= 2)
                {
                    result.Add(sampled);
                }
            }

            return result;
        }

        private static double GetLength(IList<ImagePoint> points)
        {
            double length = 0.0;
            if (points == null) return length;
            for (int index = 1; index < points.Count; index++)
            {
                length += points[index - 1].DistanceTo(points[index]);
            }

            return length;
        }

        private static int CountPoints(IEnumerable<List<ImagePoint>> segments)
        {
            return segments == null ? 0 : segments.Sum(segment => segment == null ? 0 : segment.Count);
        }

        private sealed class ContourSource
        {
            public int Index { get; set; }
            public List<ImagePoint> Points { get; set; }
            public double Length { get; set; }
            public double Coverage { get; set; }
            public double Score { get { return Length + Coverage * 0.05; } }
        }
    }

    /// <summary>XLD 特征提取或轮廓过滤失败异常。</summary>
    internal sealed class XldFeatureExtractionException : InvalidOperationException
    {
        public XldFeatureExtractionException(string message)
            : base(message)
        {
        }
    }
}
