namespace NinePointRotationCalibration
{
    /// <summary>标定、模板匹配及运行时流程的错误码。</summary>
    public enum CalibrationErrorCode
    {
        None = 0,
        ParameterInvalid = 1000,
        TemplateMissing = 1100,
        TemplateModelDirty = 1101,
        TemplateMatchFailed = 1200,
        MatchScoreTooLow = 1201,
        InsufficientSamples = 2000,
        StaleSamples = 2001,
        DegenerateGeometry = 2100,
        ResidualTooLarge = 2200,
        RotationInsufficient = 3000,
        RotationDegenerate = 3100,
        RotationResidualTooLarge = 3200,
        SerializationError = 4000,
        HalconRuntimeError = 5000,
        InternalError = 9000
    }

    /// <summary>标定样本类型（平移九点样本或旋转中心样本）。</summary>
    public enum CalibrationSampleKind
    {
        Translation = 0,
        Rotation = 1
    }

    /// <summary>图像角度相对机械轴角度的旋转方向关系。</summary>
    public enum RotationDirection
    {
        Unknown = 0,
        /// <summary>同向旋转（乘子为 +1）。</summary>
        Same = 1,
        /// <summary>反向旋转（乘子为 -1）。</summary>
        Opposite = -1
    }

    /// <summary>旋转运动的物理参考约定。</summary>
    public enum RotationConvention
    {
        Unknown = 0,
        WorkpieceRotates = 1,
        CameraRotates = 2,
        StageRotates = 3
    }

    /// <summary>机械坐标线性单位。</summary>
    public enum LinearUnit
    {
        Millimeter = 0,
        Micrometer = 1,
        Inch = 2,
        Custom = 99
    }

    /// <summary>HALCON 模板匹配算法类型（形状轮廓或 NCC 灰度相关性）。</summary>
    public enum TemplateModelType
    {
        Shape = 0,
        Ncc = 1
    }

    /// <summary>模板 ROI 或掩膜区域几何形状。</summary>
    public enum RegionKind
    {
        /// <summary>轴对齐矩形。</summary>
        Rectangle1 = 0,
        /// <summary>带旋转角度的矩形。</summary>
        Rectangle2 = 1,
        Circle = 2,
        Polygon = 3,
        Freehand = 4
    }

    /// <summary>掩膜或笔刷对模板有效区域的布尔操作方式。</summary>
    public enum MaskOperation
    {
        Include = 0,
        Exclude = 1
    }

    /// <summary>任务校验问题的严重级别。</summary>
    public enum ValidationSeverity
    {
        Information = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>标定任务校验的覆盖范围。</summary>
    public enum CalibrationValidationScope
    {
        Configuration = 0,
        Translation = 1,
        Full = 2
    }

    /// <summary>标定 RMS 误差综合评级。</summary>
    public enum CalibrationRmsGrade
    {
        /// <summary>尚未计算或缺少有效 RMS 数据。</summary>
        Unknown = 0,
        /// <summary>优（90-100 分）：亚像素精度高，推荐直接用于生产。</summary>
        Excellent = 1,
        /// <summary>良（75-89 分）：误差处于正常合理区间，可稳定生产。</summary>
        Good = 2,
        /// <summary>合格（60-74 分）：处于允许上限临界区，建议观察或微调优化。</summary>
        Acceptable = 3,
        /// <summary>不合格（0-59 分）：超过合理上限或求解失败，需重新标定。</summary>
        Unqualified = 4
    }
}
