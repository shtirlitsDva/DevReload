using System;

namespace UiMcp.Core.Geometry;

/// <summary>A 2D point (drawing units or device pixels, per context).</summary>
public readonly record struct Pt2(double X, double Y);

/// <summary>A 3D WCS point. Z is carried but the canvas transform is planar.</summary>
public readonly record struct Pt3(double X, double Y, double Z);

/// <summary>An axis-aligned screen rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// A 2D affine transform (2x2 linear part + translation) used to map a WCS
/// point onto the view's display coordinate system. Identity covers plan /
/// top-down views (plan drawings, Civil 3D profile views); a rotation handles
/// view-twist; the plugin supplies a populated matrix from the live view.
/// </summary>
public readonly record struct Matrix2d(
    double M11, double M12,
    double M21, double M22,
    double Tx, double Ty)
{
    public static Matrix2d Identity => new(1, 0, 0, 1, 0, 0);

    public Pt2 Apply(double x, double y) =>
        new(M11 * x + M12 * y + Tx, M21 * x + M22 * y + Ty);

    /// <summary>Inverse affine. Throws if the linear part is singular.</summary>
    public Matrix2d Invert()
    {
        double det = M11 * M22 - M12 * M21;
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Matrix2d is not invertible.");
        double inv = 1.0 / det;
        double i11 = M22 * inv;
        double i12 = -M12 * inv;
        double i21 = -M21 * inv;
        double i22 = M11 * inv;
        // Inverse translation: -Inv(L) * T
        double itx = -(i11 * Tx + i12 * Ty);
        double ity = -(i21 * Tx + i22 * Ty);
        return new Matrix2d(i11, i12, i21, i22, itx, ity);
    }
}
