using System;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// A 2D affine transform: x' = M11*x + M12*y + OffsetX,
    /// y' = M21*x + M22*y + OffsetY.
    /// </summary>
    [Serializable]
    public struct AffineMatrix2D : IEquatable<AffineMatrix2D>
    {
        public AffineMatrix2D(
            double m11, double m12, double offsetX,
            double m21, double m22, double offsetY)
        {
            M11 = m11;
            M12 = m12;
            OffsetX = offsetX;
            M21 = m21;
            M22 = m22;
            OffsetY = offsetY;
        }

        public double M11 { get; set; }
        public double M12 { get; set; }
        public double OffsetX { get; set; }
        public double M21 { get; set; }
        public double M22 { get; set; }
        public double OffsetY { get; set; }

        public static AffineMatrix2D Identity
        {
            get { return new AffineMatrix2D(1.0, 0.0, 0.0, 0.0, 1.0, 0.0); }
        }

        public double Determinant { get { return (M11 * M22) - (M12 * M21); } }
        public bool IsFinite
        {
            get
            {
                return Numeric.IsFinite(M11) && Numeric.IsFinite(M12) && Numeric.IsFinite(OffsetX)
                    && Numeric.IsFinite(M21) && Numeric.IsFinite(M22) && Numeric.IsFinite(OffsetY);
            }
        }

        public Point2D Transform(Point2D point)
        {
            return new Point2D(
                (M11 * point.X) + (M12 * point.Y) + OffsetX,
                (M21 * point.X) + (M22 * point.Y) + OffsetY);
        }

        public Vector2D TransformVector(Vector2D vector)
        {
            return new Vector2D(
                (M11 * vector.X) + (M12 * vector.Y),
                (M21 * vector.X) + (M22 * vector.Y));
        }

        public bool TryInvert(out AffineMatrix2D inverse)
        {
            double determinant = Determinant;
            double scale = Math.Max(1.0, Math.Max(Math.Abs(M11) + Math.Abs(M12), Math.Abs(M21) + Math.Abs(M22)));
            if (!IsFinite || Math.Abs(determinant) <= 1e-14 * scale * scale)
            {
                inverse = default(AffineMatrix2D);
                return false;
            }

            double i11 = M22 / determinant;
            double i12 = -M12 / determinant;
            double i21 = -M21 / determinant;
            double i22 = M11 / determinant;
            inverse = new AffineMatrix2D(
                i11,
                i12,
                -((i11 * OffsetX) + (i12 * OffsetY)),
                i21,
                i22,
                -((i21 * OffsetX) + (i22 * OffsetY)));
            return true;
        }

        public AffineMatrix2D Inverse()
        {
            AffineMatrix2D inverse;
            if (!TryInvert(out inverse))
            {
                throw new InvalidOperationException("The affine matrix is singular and cannot be inverted.");
            }

            return inverse;
        }

        /// <summary>Returns this(after(x)), i.e. this * after.</summary>
        public AffineMatrix2D Compose(AffineMatrix2D after)
        {
            return new AffineMatrix2D(
                (M11 * after.M11) + (M12 * after.M21),
                (M11 * after.M12) + (M12 * after.M22),
                (M11 * after.OffsetX) + (M12 * after.OffsetY) + OffsetX,
                (M21 * after.M11) + (M22 * after.M21),
                (M21 * after.M12) + (M22 * after.M22),
                (M21 * after.OffsetX) + (M22 * after.OffsetY) + OffsetY);
        }

        public double ConditionNumber
        {
            get
            {
                double a = (M11 * M11) + (M21 * M21);
                double b = (M11 * M12) + (M21 * M22);
                double d = (M12 * M12) + (M22 * M22);
                double trace = a + d;
                double discriminant = Math.Sqrt(Math.Max(0.0, ((a - d) * (a - d)) + (4.0 * b * b)));
                double lambdaMax = Math.Max(0.0, (trace + discriminant) * 0.5);
                double lambdaMin = Math.Max(0.0, (trace - discriminant) * 0.5);
                if (lambdaMin <= 1e-30)
                {
                    return double.PositiveInfinity;
                }

                return Math.Sqrt(lambdaMax / lambdaMin);
            }
        }

        public bool Equals(AffineMatrix2D other)
        {
            return M11.Equals(other.M11) && M12.Equals(other.M12) && OffsetX.Equals(other.OffsetX)
                && M21.Equals(other.M21) && M22.Equals(other.M22) && OffsetY.Equals(other.OffsetY);
        }

        public override bool Equals(object obj) { return obj is AffineMatrix2D && Equals((AffineMatrix2D)obj); }
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = M11.GetHashCode();
                hash = (hash * 397) ^ M12.GetHashCode();
                hash = (hash * 397) ^ OffsetX.GetHashCode();
                hash = (hash * 397) ^ M21.GetHashCode();
                hash = (hash * 397) ^ M22.GetHashCode();
                return (hash * 397) ^ OffsetY.GetHashCode();
            }
        }

        public override string ToString()
        {
            return string.Format("[[{0:G6}, {1:G6}, {2:G6}], [{3:G6}, {4:G6}, {5:G6}]]",
                M11, M12, OffsetX, M21, M22, OffsetY);
        }
    }
}
