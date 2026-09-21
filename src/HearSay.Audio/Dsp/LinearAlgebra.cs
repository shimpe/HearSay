namespace HearSay.Dsp;

/// <summary>Minimal dense linear algebra for the small systems used by the optimization helpers.</summary>
public static class LinearAlgebra
{
    /// <summary>Inverts a square matrix with Gauss-Jordan elimination and partial pivoting. Returns null when the matrix is singular.</summary>
    /// <param name="matrix">Square matrix; not modified.</param>
    public static double[,]? Invert(double[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        int n = matrix.GetLength(0);
        if (matrix.GetLength(1) != n)
        {
            throw new ArgumentException("Matrix must be square.", nameof(matrix));
        }

        var a = (double[,])matrix.Clone();
        var inverse = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            inverse[i, i] = 1.0;
        }

        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(a[pivot, column]) < 1e-300)
            {
                return null;
            }

            if (pivot != column)
            {
                for (int k = 0; k < n; k++)
                {
                    (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                    (inverse[column, k], inverse[pivot, k]) = (inverse[pivot, k], inverse[column, k]);
                }
            }

            double scale = 1.0 / a[column, column];
            for (int k = 0; k < n; k++)
            {
                a[column, k] *= scale;
                inverse[column, k] *= scale;
            }

            for (int row = 0; row < n; row++)
            {
                if (row == column)
                {
                    continue;
                }

                double factor = a[row, column];
                if (factor == 0)
                {
                    continue;
                }

                for (int k = 0; k < n; k++)
                {
                    a[row, k] -= factor * a[column, k];
                    inverse[row, k] -= factor * inverse[column, k];
                }
            }
        }

        return inverse;
    }
}
