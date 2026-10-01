namespace HearSay.Synthesis.Oscillators;

/// <summary>
/// The Jacobi nome q of an elliptic parameter m and back. The odd harmonics of the elliptic waveforms sn and sd fall
/// off as q per step (harmonic 2n + 1 has a level close to q^n times the fundamental), so the nome is the natural axis
/// of their brightness: 0 is a sine, about 0.04 at m = 0.5, 0.26 at m = 0.99 and 0.55 at m = 0.999999.
/// </summary>
public static class JacobiNome
{
    /// <summary>The nome q = exp(-π K'(m) / K(m)) of an elliptic parameter.</summary>
    /// <param name="m">Elliptic parameter, 0 ≤ m &lt; 1.</param>
    public static double FromParameter(double m)
    {
        m = Math.Clamp(m, 0.0, 1.0 - 1e-15);
        if (m <= 1e-10)
        {
            return 0.0;
        }

        // K = π / (2 AGM(1, k')), K' = π / (2 AGM(1, k)).
        double a = Agm(1.0, Math.Sqrt(1.0 - m));
        double b = Agm(1.0, Math.Sqrt(m));
        return Math.Exp(-Math.PI * a / b);
    }

    /// <summary>The elliptic parameter m = (θ2(q) / θ3(q))^4 of a nome.</summary>
    /// <param name="q">Nome, 0 ≤ q &lt; 1.</param>
    public static double ToParameter(double q)
    {
        if (q <= 0.0)
        {
            return 0.0;
        }

        q = Math.Min(q, 0.999);
        double theta2 = 0.0, theta3 = 1.0;
        for (int n = 0; n < 200; n++)
        {
            double t2 = Math.Pow(q, n * (n + 1.0));
            double t3 = Math.Pow(q, (n + 1.0) * (n + 1.0));
            theta2 += t2;
            theta3 += 2.0 * t3;
            if (t2 < 1e-17)
            {
                break;
            }
        }

        theta2 *= 2.0 * Math.Pow(q, 0.25);
        double ratio = theta2 / theta3;
        return Math.Min(1.0, ratio * ratio * ratio * ratio);
    }

    private static double Agm(double a, double b)
    {
        for (int i = 0; i < 40 && Math.Abs(a - b) > 1e-15 * a; i++)
        {
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        }

        return a;
    }
}
