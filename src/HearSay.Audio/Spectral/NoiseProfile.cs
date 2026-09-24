namespace HearSay.Spectral;

/// <summary>The noise of a note: what the spectrum holds between its partials.</summary>
/// <param name="Fraction">Share of the energy of the note that is noise, from 0 (only partials) to 1 (only noise).</param>
/// <param name="CentroidHz">Spectral centroid of the noise alone, its colour; 0 when there is no noise.</param>
public sealed record NoiseProfile(double Fraction, double CentroidHz)
{
    /// <summary>No noise at all.</summary>
    public static NoiseProfile None { get; } = new(0.0, 0.0);
}

/// <summary>
/// Separates noise from partials. In every frame the noise floor is the running median of the power spectrum across
/// frequency: partials are narrow peaks that a median ignores, noise fills every bin. The median of noise power is
/// ln 2 times its mean, so the floor is scaled back to a mean before it is summed. Stateless.
/// </summary>
public static class NoiseAnalyzer
{
    /// <summary>Half the width of the median window. Wide enough to span the gaps between partials, narrow enough to follow the colour of the noise.</summary>
    private const double HalfWindowHz = 400.0;

    private const double LowestHz = 20.0;

    /// <summary>Measures the noise of a note.</summary>
    /// <param name="spectrogram">
    /// Spectrogram with enough frequency resolution to resolve the partials; a main lobe must be much narrower than
    /// the spacing of the partials.
    /// </param>
    public static NoiseProfile Analyze(Spectrogram spectrogram)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        int bins = spectrogram.BinCount;
        double binHz = spectrogram.BinHz;
        int half = Math.Max(4, (int)Math.Round(HalfWindowHz / binHz));
        int first = Math.Max(1, (int)Math.Ceiling(LowestHz / binHz));

        // The median changes slowly across frequency: computing it at every few bins and holding it is enough.
        int stride = Math.Max(1, half / 8);
        var power = new double[bins];
        var window = new double[(2 * half) + 1];
        double total = 0, noise = 0, weightedHz = 0;
        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            ReadOnlySpan<float> frame = spectrogram.Frame(f);
            double frameTotal = 0;
            for (int k = first; k < bins; k++)
            {
                power[k] = (double)frame[k] * frame[k];
                frameTotal += power[k];
            }

            if (frameTotal <= 0)
            {
                continue;
            }

            double frameNoise = 0, frameWeightedHz = 0;
            double floor = 0;
            for (int k = first; k < bins; k++)
            {
                if ((k - first) % stride == 0)
                {
                    int from = Math.Max(first, k - half), to = Math.Min(bins - 1, k + half);
                    int count = to - from + 1;
                    Array.Copy(power, from, window, 0, count);
                    Array.Sort(window, 0, count);
                    floor = window[count / 2] / Math.Log(2.0);
                }

                frameNoise += floor;
                frameWeightedHz += floor * k * binHz;
            }

            // The floor of a frame that is all noise can come out a little above its power; it is never more.
            double scale = frameNoise > frameTotal ? frameTotal / frameNoise : 1.0;
            total += frameTotal;
            noise += frameNoise * scale;
            weightedHz += frameWeightedHz * scale;
        }

        return total > 0 && noise > 0 ? new NoiseProfile(noise / total, weightedHz / noise) : NoiseProfile.None;
    }
}
