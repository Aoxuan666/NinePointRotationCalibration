using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    internal static class SolverMath
    {
        public static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            double[] sorted = new double[values.Count];
            for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return (sorted.Length & 1) == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5;
        }

        public static double MadThreshold(IList<double> residuals, double multiplier, double minimumThreshold)
        {
            if (residuals == null || residuals.Count == 0) return minimumThreshold;
            double median = Median(residuals);
            List<double> deviations = new List<double>(residuals.Count);
            for (int i = 0; i < residuals.Count; i++) deviations.Add(Math.Abs(residuals[i] - median));
            double sigma = 1.4826 * Median(deviations);
            if (!Numeric.IsFinite(sigma)) sigma = 0.0;
            return Math.Max(minimumThreshold, median + (multiplier * sigma));
        }

        public static bool SolveLinear(double[,] matrix, double[] right, out double[] solution)
        {
            int count = right == null ? 0 : right.Length;
            solution = null;
            if (matrix == null || matrix.GetLength(0) != count || matrix.GetLength(1) != count || count == 0)
                return false;

            double[,] augmented = new double[count, count + 1];
            double maxEntry = 0.0;
            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < count; column++)
                {
                    augmented[row, column] = matrix[row, column];
                    maxEntry = Math.Max(maxEntry, Math.Abs(matrix[row, column]));
                }

                augmented[row, count] = right[row];
            }

            if (maxEntry <= 0.0 || !Numeric.IsFinite(maxEntry)) return false;
            double tolerance = maxEntry * 1e-12;

            for (int pivotColumn = 0; pivotColumn < count; pivotColumn++)
            {
                int pivotRow = pivotColumn;
                double pivotSize = Math.Abs(augmented[pivotRow, pivotColumn]);
                for (int row = pivotColumn + 1; row < count; row++)
                {
                    double candidate = Math.Abs(augmented[row, pivotColumn]);
                    if (candidate > pivotSize)
                    {
                        pivotSize = candidate;
                        pivotRow = row;
                    }
                }

                if (pivotSize <= tolerance || !Numeric.IsFinite(pivotSize)) return false;
                if (pivotRow != pivotColumn)
                {
                    for (int column = pivotColumn; column <= count; column++)
                    {
                        double swap = augmented[pivotColumn, column];
                        augmented[pivotColumn, column] = augmented[pivotRow, column];
                        augmented[pivotRow, column] = swap;
                    }
                }

                double pivot = augmented[pivotColumn, pivotColumn];
                for (int column = pivotColumn; column <= count; column++) augmented[pivotColumn, column] /= pivot;

                for (int row = 0; row < count; row++)
                {
                    if (row == pivotColumn) continue;
                    double factor = augmented[row, pivotColumn];
                    if (factor == 0.0) continue;
                    for (int column = pivotColumn; column <= count; column++)
                        augmented[row, column] -= factor * augmented[pivotColumn, column];
                }
            }

            solution = new double[count];
            for (int i = 0; i < count; i++)
            {
                solution[i] = augmented[i, count];
                if (!Numeric.IsFinite(solution[i])) return false;
            }

            return true;
        }

        public static double WeightedRms(IList<double> residuals, IList<double> weights)
        {
            if (residuals == null || residuals.Count == 0) return double.NaN;
            double weightedSquares = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < residuals.Count; i++)
            {
                double weight = weights == null ? 1.0 : Math.Max(0.0, weights[i]);
                weightedSquares += weight * residuals[i] * residuals[i];
                totalWeight += weight;
            }

            return totalWeight <= 0.0 ? double.NaN : Math.Sqrt(weightedSquares / totalWeight);
        }

        public static double WeightFromScore(double score, bool enabled)
        {
            if (!enabled) return 1.0;
            double bounded = Numeric.Clamp(score, 0.05, 1.0);
            return bounded * bounded;
        }
    }
}
