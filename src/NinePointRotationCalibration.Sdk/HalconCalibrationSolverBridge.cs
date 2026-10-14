namespace NinePointRotationCalibration.Sdk
{
    // Existing SDK code calls CalibrationSolver without a namespace qualifier.
    // Keeping this bridge in the SDK namespace routes all production
    // calibration entry points through HALCON without changing the public API.
    internal static class CalibrationSolver
    {
        public static CalibrationResult Solve(CalibrationJob job)
        {
            return Halcon.HalconCalibrationSolver.Solve(job);
        }
    }
}
