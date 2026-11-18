using System;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// 从有效模板图像域中自动分析得到的阈值与对比度参数。
    /// </summary>
    internal struct AdaptiveTemplateThresholds
    {
        public double EdgeLowThreshold;
        public double EdgeHighThreshold;
        public int ContrastLow;
        public int ContrastHigh;
        public int MinContrast;
        public double EstimatedNoise;
    }

    /// <summary>
    /// 基于 HALCON 原生参数估计与局部梯度/噪声统计，自适应计算模板建模阈值，
    /// 避免在低对比度、局部反光或亮度波动场景下人工反复试凑 Canny 与对比度参数。
    /// </summary>
    internal static class HalconAutoThresholdResolver
    {
        private const double FallbackEdgeLow = 12.0;
        private const double FallbackEdgeHigh = 28.0;
        private const int FallbackContrastLow = 12;
        private const int FallbackContrastHigh = 24;
        private const int FallbackMinContrast = 5;

        /// <summary>
        /// 分析当前有效模板图像域，输出自适应 Canny 双阈值以及 Shape 模型滞后对比度/最小对比度。
        /// </summary>
        public static AdaptiveTemplateThresholds Resolve(
            HImage reducedImage,
            ShapeModelParameters shapeParameters,
            string metric)
        {
            if (reducedImage == null) throw new ArgumentNullException(nameof(reducedImage));
            ShapeModelParameters parameters = shapeParameters ?? new ShapeModelParameters();

            double detLow = FallbackEdgeLow;
            double detHigh = FallbackEdgeHigh;
            double detMinContrast = FallbackMinContrast;
            TryDetermineShapeContrast(reducedImage, parameters, metric, out detLow, out detHigh, out detMinContrast);

            double noise = EstimateNoiseFloor(reducedImage);
            double sobelP985 = EstimateStrongEdgeSobel(reducedImage);

            // 结合 Foerstner 噪声底噪、HALCON 估计的对比度及 ROI 内 98.5% 边缘梯度，
            // 兼顾弱边缘（如哑光 PCB/丝印）检出率与抑制基板纹理碎边缘。
            double noiseFloor = Math.Max(4.0, noise * 2.8);
            double sobelCap = Math.Min(sobelP985 * 0.15, 24.0);
            double edgeHigh = Math.Max(detHigh, Math.Max(noiseFloor * 1.35, sobelCap));
            edgeHigh = Clamp(Math.Round(edgeHigh, 1), 8.0, 140.0);

            double edgeLow = Math.Max(detLow, Math.Max(4.0, Math.Min(edgeHigh * 0.55, noiseFloor * 0.85)));
            edgeLow = Math.Min(edgeHigh * 0.75, edgeLow);
            edgeLow = Math.Round(Math.Max(3.0, edgeLow), 1);
            if (edgeLow >= edgeHigh - 1.0)
            {
                edgeLow = Math.Round(Math.Max(2.0, edgeHigh * 0.55), 1);
            }

            int contrastHigh = (int)Math.Round(Clamp(Math.Max(detHigh, Math.Max(8.0, noise * 3.0)), 6.0, 160.0));
            int contrastLow = (int)Math.Round(Clamp(
                Math.Max(detLow, Math.Min(contrastHigh * 0.65, contrastHigh - 1.0)),
                3.0,
                Math.Max(3.0, contrastHigh - 1.0)));

            int minContrast = (int)Math.Ceiling(Math.Max(detMinContrast, Math.Max(3.0, noise * 1.6)));
            minContrast = Math.Max(3, Math.Min(40, minContrast));

            return new AdaptiveTemplateThresholds
            {
                EdgeLowThreshold = edgeLow,
                EdgeHighThreshold = edgeHigh,
                ContrastLow = contrastLow,
                ContrastHigh = contrastHigh,
                MinContrast = minContrast,
                EstimatedNoise = noise
            };
        }

        private static void TryDetermineShapeContrast(
            HImage reducedImage,
            ShapeModelParameters parameters,
            string metric,
            out double contrastLow,
            out double contrastHigh,
            out double minContrast)
        {
            contrastLow = FallbackEdgeLow;
            contrastHigh = FallbackEdgeHigh;
            minContrast = FallbackMinContrast;

            try
            {
                using (HTuple autoParam = new HTuple("auto"))
                using (HTuple numLevels = parameters.NumLevels <= 0 ? new HTuple("auto") : new HTuple(parameters.NumLevels))
                using (HTuple scaleOne = new HTuple(1.0))
                using (HTuple allParam = new HTuple("all"))
                {
                    HTuple values;
                    using (HTuple names = reducedImage.DetermineShapeModelParams(
                        numLevels,
                        parameters.AngleStartRad,
                        parameters.AngleExtentRad,
                        scaleOne,
                        scaleOne,
                        string.IsNullOrWhiteSpace(parameters.Optimization) ? "auto" : parameters.Optimization,
                        string.IsNullOrWhiteSpace(metric) ? "ignore_local_polarity" : metric,
                        autoParam,
                        autoParam,
                        allParam,
                        out values))
                    using (values)
                    {
                        for (int index = 0; index < names.Length && index < values.Length; index++)
                        {
                            string key = names[index].S;
                            if (string.Equals(key, "contrast_low", StringComparison.Ordinal))
                                contrastLow = Math.Max(2.0, values[index].D);
                            else if (string.Equals(key, "contrast_high", StringComparison.Ordinal))
                                contrastHigh = Math.Max(contrastLow + 1.0, values[index].D);
                            else if (string.Equals(key, "min_contrast", StringComparison.Ordinal))
                                minContrast = Math.Max(2.0, values[index].D);
                        }
                    }
                }
            }
            catch (HOperatorException)
            {
                // 极小或平坦 ROI 下 HALCON 自动估计可能失败，回退至保守默认值再由梯度与兜底逻辑收敛。
            }
        }

        private static double EstimateNoiseFloor(HImage reducedImage)
        {
            try
            {
                double noise = reducedImage.EstimateNoise("foerstner", 20.0);
                if (double.IsNaN(noise) || double.IsInfinity(noise) || noise < 0.0) return 0.0;
                return Math.Min(35.0, noise);
            }
            catch (HOperatorException)
            {
                return 0.0;
            }
        }

        private static double EstimateStrongEdgeSobel(HImage reducedImage)
        {
            try
            {
                using (HRegion domain = reducedImage.GetDomain())
                using (HImage sobel = reducedImage.SobelAmp("sum_abs", 3))
                {
                    HTuple relative;
                    using (HTuple absolute = sobel.GrayHisto(domain, out relative))
                    using (relative)
                    {
                        long total = 0;
                        for (int i = 0; i < absolute.Length; i++)
                        {
                            total += absolute[i].L;
                        }
                        if (total <= 0) return 0.0;

                        long target = (long)Math.Ceiling(total * 0.985);
                        long cumulative = 0;
                        for (int i = 0; i < absolute.Length; i++)
                        {
                            cumulative += absolute[i].L;
                            if (cumulative >= target) return i;
                        }
                    }
                }
            }
            catch (HOperatorException)
            {
            }
            return 0.0;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
