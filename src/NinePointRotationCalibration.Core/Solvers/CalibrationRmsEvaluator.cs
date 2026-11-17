using System;
using System.Globalization;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 标定 RMS 误差评估与分级标准器（结合像素平动、旋转中心与旋转角度三项指标给出 0-100 评分）。
    /// </summary>
    public static class CalibrationRmsEvaluator
    {
        /// <summary>默认平动 XY RMS 优秀上限（像素）。</summary>
        public const double DefaultExcellentPixelRms = 0.20;
        /// <summary>默认平动 XY RMS 良好上限（像素）。</summary>
        public const double DefaultGoodPixelRms = 0.50;
        /// <summary>默认平动 XY RMS 合格上限（像素）。</summary>
        public const double DefaultAcceptablePixelRms = 1.00;

        /// <summary>默认旋转中心 RMS 优秀上限（像素）。</summary>
        public const double DefaultExcellentCenterRms = 0.30;
        /// <summary>默认旋转中心 RMS 良好上限（像素）。</summary>
        public const double DefaultGoodCenterRms = 0.60;
        /// <summary>默认旋转中心 RMS 合格上限（像素）。</summary>
        public const double DefaultAcceptableCenterRms = 1.00;

        /// <summary>默认旋转角度 RMS 优秀上限（度）。</summary>
        public const double DefaultExcellentAngleRms = 0.05;
        /// <summary>默认旋转角度 RMS 良好上限（度）。</summary>
        public const double DefaultGoodAngleRms = 0.15;
        /// <summary>默认旋转角度 RMS 合格上限（度）。</summary>
        public const double DefaultAcceptableAngleRms = 0.50;

        /// <summary>根据标定求解结果与容差配置生成 RMS 误差质量评估报告。</summary>
        public static CalibrationRmsAssessment Evaluate(
            CalibrationResult result,
            CalibrationSolverOptions options = null)
        {
            double xyLimit = PositiveOrDefault(options == null ? 0.0 : options.MaxRmsResidualPixels, DefaultAcceptablePixelRms);
            double centerLimit = PositiveOrDefault(options == null ? 0.0 : options.MaxRotationCenterRmsPixels, DefaultAcceptableCenterRms);
            double angleLimit = PositiveOrDefault(options == null ? 0.0 : options.MaxAngleRmsDegrees, DefaultAcceptableAngleRms);

            double xyExcellent = Math.Min(DefaultExcellentPixelRms, xyLimit * 0.25);
            double xyGood = Math.Min(DefaultGoodPixelRms, xyLimit * 0.60);
            double centerExcellent = Math.Min(DefaultExcellentCenterRms, centerLimit * 0.30);
            double centerGood = Math.Min(DefaultGoodCenterRms, centerLimit * 0.60);
            double angleExcellent = Math.Min(DefaultExcellentAngleRms, angleLimit * 0.25);
            double angleGood = Math.Min(DefaultGoodAngleRms, angleLimit * 0.50);

            string criteria = string.Format(
                CultureInfo.InvariantCulture,
                "XY 合理标准: ≤{0:0.##}px 优(90-100) | ≤{1:0.##}px 良(75-89) | ≤{2:0.##}px 合格(60-74) | &gt;{2:0.##}px 不合格；旋中≤{3:0.##}px / 角≤{4:0.##}°",
                xyExcellent,
                xyGood,
                xyLimit,
                centerLimit,
                angleLimit).Replace("&gt;", ">");

            CalibrationRmsAssessment assessment = new CalibrationRmsAssessment
            {
                TranslationLimitPixels = xyLimit,
                RotationCenterLimitPixels = centerLimit,
                RotationAngleLimitDegrees = angleLimit,
                CriteriaText = criteria
            };

            if (result == null)
            {
                assessment.Grade = CalibrationRmsGrade.Unknown;
                assessment.GradeLabel = GetGradeLabel(CalibrationRmsGrade.Unknown);
                assessment.Summary = "尚未执行标定计算。";
                assessment.Recommendation = "请完成 9+5 点采图后点击「计算并保存」。";
                return assessment;
            }

            bool hasXyRms = Numeric.IsFinite(result.RmsResidualPixels) && result.RmsResidualPixels >= 0.0 && result.InlierCount > 0;
            bool hasStageRms = Numeric.IsFinite(result.RmsResidualStage) && result.RmsResidualStage >= 0.0;
            bool hasRotation = result.Rotation != null && result.Rotation.InlierCount > 0 &&
                Numeric.IsFinite(result.Rotation.CenterRmsResidualPixels) &&
                Numeric.IsFinite(result.Rotation.AngleRmsDegrees);

            assessment.TranslationRmsPixels = hasXyRms ? result.RmsResidualPixels : double.NaN;
            assessment.TranslationRmsStage = hasStageRms ? result.RmsResidualStage : double.NaN;
            assessment.RotationCenterRmsPixels = hasRotation ? result.Rotation.CenterRmsResidualPixels : double.NaN;
            assessment.RotationAngleRmsDegrees = hasRotation ? result.Rotation.AngleRmsDegrees : double.NaN;

            if (!hasXyRms)
            {
                assessment.Grade = CalibrationRmsGrade.Unqualified;
                assessment.TranslationGrade = CalibrationRmsGrade.Unqualified;
                assessment.GradeLabel = GetGradeLabel(CalibrationRmsGrade.Unqualified);
                assessment.Score = 0;
                assessment.IsReasonable = false;
                assessment.Summary = "不合格 (0 分) · 缺少有效九点仿射计算结果";
                assessment.Recommendation = string.IsNullOrWhiteSpace(result.Message)
                    ? "有效九点样本不足或几何退化，请重新采集平移点。"
                    : result.Message;
                return assessment;
            }

            CalibrationRmsGrade xyGrade;
            int xyScore = ScoreMetric(result.RmsResidualPixels, xyExcellent, xyGood, xyLimit, out xyGrade);
            if (!result.TranslationSuccess && xyGrade != CalibrationRmsGrade.Unqualified)
            {
                xyGrade = CalibrationRmsGrade.Unqualified;
                xyScore = Math.Min(59, xyScore);
            }
            assessment.TranslationGrade = xyGrade;

            CalibrationRmsGrade centerGrade = CalibrationRmsGrade.Unknown;
            CalibrationRmsGrade angleGrade = CalibrationRmsGrade.Unknown;
            int centerScore = xyScore;
            int angleScore = xyScore;
            if (hasRotation)
            {
                centerScore = ScoreMetric(result.Rotation.CenterRmsResidualPixels, centerExcellent, centerGood, centerLimit, out centerGrade);
                angleScore = ScoreMetric(result.Rotation.AngleRmsDegrees, angleExcellent, angleGood, angleLimit, out angleGrade);
            }
            assessment.RotationCenterGrade = centerGrade;
            assessment.RotationAngleGrade = angleGrade;

            // 短板原则：综合评级取各已评价维度中的最差等级；若求解失败则整体判定为不合格
            CalibrationRmsGrade overallGrade = xyGrade;
            if (hasRotation)
            {
                overallGrade = WorstGrade(overallGrade, centerGrade);
                overallGrade = WorstGrade(overallGrade, angleGrade);
            }
            if (!result.Success)
            {
                overallGrade = CalibrationRmsGrade.Unqualified;
            }

            int rawScore = hasRotation
                ? (int)Math.Round((0.50 * xyScore) + (0.30 * centerScore) + (0.20 * angleScore))
                : xyScore;

            int minBand;
            int maxBand;
            GetScoreBand(overallGrade, out minBand, out maxBand);
            int clampedScore = Math.Max(minBand, Math.Min(maxBand, rawScore));

            assessment.Grade = overallGrade;
            assessment.GradeLabel = GetGradeLabel(overallGrade);
            assessment.Score = clampedScore;
            assessment.IsReasonable = result.Success && overallGrade != CalibrationRmsGrade.Unqualified;

            assessment.Summary = string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1} 分) · XY {2:0.###} px{3}",
                assessment.GradeLabel,
                assessment.Score,
                result.RmsResidualPixels,
                hasRotation
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        " · 旋中 {0:0.###} px · 角度 {1:0.###}°",
                        result.Rotation.CenterRmsResidualPixels,
                        result.Rotation.AngleRmsDegrees)
                    : string.Empty);

            assessment.Recommendation = BuildRecommendation(overallGrade, result, xyGrade, centerGrade, angleGrade);
            return assessment;
        }

        /// <summary>返回枚举等级对应的简洁中文标签。</summary>
        public static string GetGradeLabel(CalibrationRmsGrade grade)
        {
            switch (grade)
            {
                case CalibrationRmsGrade.Excellent: return "优";
                case CalibrationRmsGrade.Good: return "良";
                case CalibrationRmsGrade.Acceptable: return "合格";
                case CalibrationRmsGrade.Unqualified: return "不合格";
                default: return "待计算";
            }
        }

        private static int ScoreMetric(
            double value,
            double excellentMax,
            double goodMax,
            double limitMax,
            out CalibrationRmsGrade grade)
        {
            if (!Numeric.IsFinite(value) || value < 0.0)
            {
                grade = CalibrationRmsGrade.Unqualified;
                return 0;
            }

            if (value <= excellentMax)
            {
                grade = CalibrationRmsGrade.Excellent;
                double ratio = excellentMax <= 1e-12 ? 0.0 : value / excellentMax;
                return ClampScore(100.0 - (10.0 * ratio), 90, 100);
            }

            if (value <= goodMax)
            {
                grade = CalibrationRmsGrade.Good;
                double span = Math.Max(1e-12, goodMax - excellentMax);
                double ratio = (value - excellentMax) / span;
                return ClampScore(89.0 - (14.0 * ratio), 75, 89);
            }

            if (value <= limitMax)
            {
                grade = CalibrationRmsGrade.Acceptable;
                double span = Math.Max(1e-12, limitMax - goodMax);
                double ratio = (value - goodMax) / span;
                return ClampScore(74.0 - (14.0 * ratio), 60, 74);
            }

            grade = CalibrationRmsGrade.Unqualified;
            double overRatio = (value - limitMax) / Math.Max(1e-6, limitMax);
            return ClampScore(59.0 - (40.0 * overRatio), 0, 59);
        }

        private static int ClampScore(double value, int min, int max)
        {
            int rounded = (int)Math.Round(value);
            return Math.Max(min, Math.Min(max, rounded));
        }

        private static CalibrationRmsGrade WorstGrade(CalibrationRmsGrade left, CalibrationRmsGrade right)
        {
            if (left == CalibrationRmsGrade.Unknown) return right;
            if (right == CalibrationRmsGrade.Unknown) return left;
            return (int)left >= (int)right ? left : right;
        }

        private static void GetScoreBand(CalibrationRmsGrade grade, out int min, out int max)
        {
            switch (grade)
            {
                case CalibrationRmsGrade.Excellent:
                    min = 90; max = 100; break;
                case CalibrationRmsGrade.Good:
                    min = 75; max = 89; break;
                case CalibrationRmsGrade.Acceptable:
                    min = 60; max = 74; break;
                default:
                    min = 0; max = 59; break;
            }
        }

        private static string BuildRecommendation(
            CalibrationRmsGrade overall,
            CalibrationResult result,
            CalibrationRmsGrade xyGrade,
            CalibrationRmsGrade centerGrade,
            CalibrationRmsGrade angleGrade)
        {
            switch (overall)
            {
                case CalibrationRmsGrade.Excellent:
                    return "标定处于亚像素高精度区间，可直接用于生产定位与双吸嘴旋转纠偏。";
                case CalibrationRmsGrade.Good:
                    return "标定精度处于合理生产区间，可正常投产；若需更高贴装一致性可微调焦距与模板对比度。";
                case CalibrationRmsGrade.Acceptable:
                    if (xyGrade == CalibrationRmsGrade.Acceptable)
                        return "已合格但平动 RMS 接近上限：建议检查镜头边缘畸变、标定板停稳延时或剔除高残差点位。";
                    if (centerGrade == CalibrationRmsGrade.Acceptable || angleGrade == CalibrationRmsGrade.Acceptable)
                        return "已合格但旋转 RMS 偏大：建议增大 R 轴校准跨度或检查主轴刚性/背隙。";
                    return "标定精度刚过合格线，建议复测确认稳定性。";
                default:
                    if (result != null && !result.TranslationSuccess)
                        return "XY 平动标定超差或样本不足：请检查列表中红色高亮高残差点位、移动步距与坐标轴方向后重标。";
                    if (result != null && result.Rotation != null && !result.Rotation.Success)
                        return "旋转标定超差或角度跨度不足：请增大 R 步距、保持工作物稳固并重采 10-14 号旋转点。";
                    return "误差超出合理范围，请排查外点样本后重新标定。";
            }
        }

        private static double PositiveOrDefault(double value, double fallback)
        {
            return Numeric.IsFinite(value) && value > 0.0 ? value : fallback;
        }
    }
}
