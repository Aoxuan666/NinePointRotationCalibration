namespace NinePointRotationCalibration
{
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

    public enum CalibrationSampleKind
    {
        Translation = 0,
        Rotation = 1
    }

    public enum RotationDirection
    {
        Unknown = 0,
        Same = 1,
        Opposite = -1
    }

    public enum RotationConvention
    {
        Unknown = 0,
        WorkpieceRotates = 1,
        CameraRotates = 2,
        StageRotates = 3
    }

    public enum LinearUnit
    {
        Millimeter = 0,
        Micrometer = 1,
        Inch = 2,
        Custom = 99
    }

    public enum TemplateModelType
    {
        Shape = 0,
        Ncc = 1
    }

    public enum RegionKind
    {
        Rectangle1 = 0,
        Rectangle2 = 1,
        Circle = 2,
        Polygon = 3,
        Freehand = 4
    }

    public enum MaskOperation
    {
        Include = 0,
        Exclude = 1
    }

    public enum ValidationSeverity
    {
        Information = 0,
        Warning = 1,
        Error = 2
    }

    public enum CalibrationValidationScope
    {
        Configuration = 0,
        Translation = 1,
        Full = 2
    }
}
