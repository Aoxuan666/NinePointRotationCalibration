using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// HALCON-backed 2D geometry used by calibration and inspection.
    /// Coordinates exposed by the application remain X/column and Y/row.
    /// </summary>
    public static class HalconGeometry
    {
        public static bool TryFitAffine(
            IList<double> sourceX,
            IList<double> sourceY,
            IList<double> targetX,
            IList<double> targetY,
            out AffineMatrix2D matrix)
        {
            matrix = default(AffineMatrix2D);
            if (!SameLength(sourceX, sourceY, targetX, targetY) || sourceX.Count < 3)
                return false;

            try
            {
                using (HTuple px = new HTuple(ToArray(sourceX)))
                using (HTuple py = new HTuple(ToArray(sourceY)))
                using (HTuple qx = new HTuple(ToArray(targetX)))
                using (HTuple qy = new HTuple(ToArray(targetY)))
                {
                    HTuple hom;
                    HOperatorSet.VectorToHomMat2d(px, py, qx, qy, out hom);
                    using (hom) return TryToMatrix(hom, out matrix);
                }
            }
            catch (HOperatorException)
            {
                return false;
            }
        }

        public static bool TryInvert(AffineMatrix2D matrix, out AffineMatrix2D inverse)
        {
            inverse = default(AffineMatrix2D);
            if (!matrix.IsFinite) return false;
            try
            {
                using (HTuple hom = ToTuple(matrix))
                {
                    HTuple inverted;
                    HOperatorSet.HomMat2dInvert(hom, out inverted);
                    using (inverted) return TryToMatrix(inverted, out inverse);
                }
            }
            catch (HOperatorException)
            {
                return false;
            }
        }

        public static Point2D TransformPoint(AffineMatrix2D matrix, Point2D point)
        {
            using (HTuple hom = ToTuple(matrix))
            {
                HTuple outputX;
                HTuple outputY;
                HOperatorSet.AffineTransPoint2d(hom, point.X, point.Y, out outputX, out outputY);
                using (outputX)
                using (outputY)
                    return new Point2D(outputX.D, outputY.D);
            }
        }

        public static Vector2D TransformVector(AffineMatrix2D matrix, Vector2D vector)
        {
            Point2D origin = TransformPoint(matrix, new Point2D(0.0, 0.0));
            Point2D endpoint = TransformPoint(matrix, new Point2D(vector.X, vector.Y));
            return new Vector2D(endpoint.X - origin.X, endpoint.Y - origin.Y);
        }

        public static bool TrySolveLinear(double[,] coefficients, double[] right, out double[] solution)
        {
            solution = null;
            if (coefficients == null || right == null ||
                coefficients.GetLength(0) != coefficients.GetLength(1) ||
                coefficients.GetLength(0) != right.Length || right.Length == 0)
                return false;

            HTuple leftId = null;
            HTuple rightId = null;
            HTuple resultId = null;
            try
            {
                int size = right.Length;
                double[] flat = new double[size * size];
                for (int row = 0; row < size; row++)
                    for (int column = 0; column < size; column++)
                        flat[(row * size) + column] = coefficients[row, column];
                using (HTuple leftValues = new HTuple(flat))
                using (HTuple rightValues = new HTuple(right))
                {
                    HOperatorSet.CreateMatrix(size, size, leftValues, out leftId);
                    HOperatorSet.CreateMatrix(size, 1, rightValues, out rightId);
                }
                HOperatorSet.SolveMatrix(leftId, "general", 1.0e-12, rightId, out resultId);
                HTuple values;
                HOperatorSet.GetFullMatrix(resultId, out values);
                using (values)
                {
                    if (values.Length != size) return false;
                    solution = new double[size];
                    for (int i = 0; i < size; i++)
                    {
                        solution[i] = values[i].D;
                        if (double.IsNaN(solution[i]) || double.IsInfinity(solution[i]))
                            return false;
                    }
                }
                return true;
            }
            catch (HOperatorException)
            {
                solution = null;
                return false;
            }
            finally
            {
                ClearMatrix(resultId);
                ClearMatrix(rightId);
                ClearMatrix(leftId);
            }
        }

        public static ImagePoint TransformRigid(
            ImagePose sourcePose,
            ImagePose targetPose,
            ImagePoint point)
        {
            if (sourcePose == null) sourcePose = new ImagePose();
            if (targetPose == null) targetPose = new ImagePose();
            HTuple hom;
            HOperatorSet.VectorAngleToRigid(
                sourcePose.Column,
                sourcePose.Row,
                DegreesToRadians(-sourcePose.AngleDegrees),
                targetPose.Column,
                targetPose.Row,
                DegreesToRadians(-targetPose.AngleDegrees),
                out hom);
            using (hom)
            {
                HTuple column;
                HTuple row;
                HOperatorSet.AffineTransPoint2d(hom, point.Column, point.Row, out column, out row);
                using (column)
                using (row)
                    return new ImagePoint(row.D, column.D);
            }
        }

        private static HTuple ToTuple(AffineMatrix2D matrix)
        {
            return new HTuple(new[]
            {
                matrix.M11, matrix.M12, matrix.OffsetX,
                matrix.M21, matrix.M22, matrix.OffsetY
            });
        }

        private static bool TryToMatrix(HTuple hom, out AffineMatrix2D matrix)
        {
            matrix = default(AffineMatrix2D);
            if (hom == null || hom.Length < 6) return false;
            matrix = new AffineMatrix2D(
                hom[0].D, hom[1].D, hom[2].D,
                hom[3].D, hom[4].D, hom[5].D);
            return matrix.IsFinite && Math.Abs(matrix.Determinant) > 1e-15;
        }

        private static bool SameLength(params IList<double>[] values)
        {
            if (values == null || values.Length == 0 || values[0] == null) return false;
            int length = values[0].Count;
            foreach (IList<double> value in values)
                if (value == null || value.Count != length) return false;
            return true;
        }

        private static double[] ToArray(IList<double> values)
        {
            double[] output = new double[values.Count];
            for (int i = 0; i < values.Count; i++) output[i] = values[i];
            return output;
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void ClearMatrix(HTuple matrixId)
        {
            if (matrixId == null) return;
            try
            {
                if (matrixId.Length > 0) HOperatorSet.ClearMatrix(matrixId);
            }
            finally
            {
                matrixId.Dispose();
            }
        }
    }
}
