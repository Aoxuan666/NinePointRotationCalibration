using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// Owns the HALCON model lifecycle. Calls on one instance are serialized because
    /// HALCON model handles are mutable native resources.
    /// </summary>
    public sealed class HalconTemplateEngine : ITemplateMatchingEngine
    {
        private readonly object _syncRoot = new object();
        private HShapeModel _shapeModel;
        private HNCCModel _nccModel;
        private string _cachedModelKey;
        private List<List<ImagePoint>> _cachedRelativeContours = new List<List<ImagePoint>>();
        private bool _disposed;

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
                        if (anchorWasUninitialized)
                        {
                            output.ReferenceAnchor = output.DomainCenter;
                        }

                        byte[] modelData;
                        List<List<ImagePoint>> relativeContours;
                        if (output.ModelType == TemplateModelType.Shape)
                        {
                            shapeModel = CreateShapeModel(reduced, output.ShapeParameters);
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
                            Message = "Template model was created successfully.",
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

        public TemplateBuildResult Teach(Bitmap referenceImage, TemplateDefinition definition)
        {
            return BuildTemplate(referenceImage, definition);
        }

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

        public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true)
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

            TemplateDefinition snapshot = definition.DeepClone();
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

                    using (HImage gray = HalconImageConverter.ToGrayHImage(image))
                    using (HRegion searchRegion = HalconRegionBuilder.BuildSearchRegion(snapshot.SearchRegion, image.Width, image.Height))
                    using (HImage searchImage = gray.ReduceDomain(searchRegion))
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

        public void ClearCache()
        {
            lock (_syncRoot)
            {
                if (_disposed) return;
                ClearCacheCore();
            }
        }

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
            string actualHash = TemplateDefinition.ComputeModelHash(definition.ModelData);
            if (!string.IsNullOrWhiteSpace(definition.ModelHash)
                && !string.Equals(definition.ModelHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The template model hash does not match ModelData.");
            }

            string key = ((int)definition.ModelType) + ":" + actualHash;
            if (string.Equals(key, _cachedModelKey, StringComparison.Ordinal)
                && ((definition.ModelType == TemplateModelType.Shape && _shapeModel != null)
                    || (definition.ModelType == TemplateModelType.Ncc && _nccModel != null)))
            {
                // NCC overlay contours are derived from the current effective
                // region and model reference, not from model.bin alone. Rebuild
                // them when metadata changes while retaining the native handle.
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
            _cachedModelKey = ((int)definition.ModelType) + ":" + definition.ModelHash;
            _cachedRelativeContours = relativeContours ?? new List<List<ImagePoint>>();
        }

        private static HShapeModel CreateShapeModel(HImage reduced, ShapeModelParameters parameters)
        {
            HShapeModel model = new HShapeModel();
            try
            {
                using (HTuple levels = AutoOrPositive(parameters.NumLevels))
                using (HTuple angleStep = AutoOrPositive(parameters.AngleStepRad))
                using (HTuple optimization = new HTuple(DefaultIfBlank(parameters.Optimization, "auto")))
                using (HTuple contrast = new HTuple(parameters.Contrast))
                using (HTuple minContrast = new HTuple(parameters.MinContrast))
                {
                    model.CreateShapeModel(
                        reduced,
                        levels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        angleStep,
                        optimization,
                        DefaultIfBlank(parameters.Metric, "use_polarity"),
                        contrast,
                        minContrast);
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
                if (parameters.Contrast <= 0) throw new ArgumentOutOfRangeException("Contrast");
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
                // Some 19.11 builds do not expose a native per-model timeout.
                // Elapsed time is still enforced as a soft timeout by the caller.
            }
        }

        private void ClearCacheCore()
        {
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
