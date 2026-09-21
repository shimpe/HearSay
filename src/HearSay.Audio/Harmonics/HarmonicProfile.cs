namespace HearSay.Harmonics;

/// <summary>Configuration of the harmonic analysis.</summary>
public sealed record HarmonicOptions
{
    /// <summary>Largest number of harmonics that is measured.</summary>
    public int MaxHarmonics { get; init; } = 32;

    /// <summary>Highest harmonic frequency that is measured, in Hz; null means up to 95% of the Nyquist frequency.</summary>
    public double? MaxFrequencyHz { get; init; }

    /// <summary>Half-width, in cents, of the window around each nominal harmonic frequency in which its peak is searched.</summary>
    public double SearchCents { get; init; } = 50.0;

    /// <summary>
    /// Floor, in dB relative to the total harmonic power, below which harmonic levels are clamped. Two harmonics that
    /// are both below the floor are reported as equal.
    /// </summary>
    public double LevelFloorDb { get; init; } = -80.0;

    /// <summary>Smallest FFT size used for harmonic analysis.</summary>
    public int MinimumFftSize { get; init; } = 4096;

    /// <summary>Largest FFT size used for harmonic analysis (needed for very low notes).</summary>
    public int MaximumFftSize { get; init; } = 32768;

    /// <summary>Lowest fundamental that F0 estimation considers, in Hz.</summary>
    public double MinimumFundamentalHz { get; init; } = 30.0;

    /// <summary>Highest fundamental that F0 estimation considers, in Hz.</summary>
    public double MaximumFundamentalHz { get; init; } = 5000.0;
}

/// <summary>One measured harmonic.</summary>
/// <param name="Number">Harmonic number; 1 is the fundamental.</param>
/// <param name="NominalHz">Ideal frequency: harmonic number times the fundamental.</param>
/// <param name="MeasuredHz">Interpolated frequency of the spectral peak that was found.</param>
/// <param name="Power">Mean-square power of the harmonic.</param>
/// <param name="AbsoluteDb">Level of the harmonic in dB (RMS, in the analysed signal).</param>
/// <param name="RelativeDb">Level in dB relative to the total harmonic power, clamped at the configured floor.</param>
public sealed record HarmonicPeak(
    int Number,
    double NominalHz,
    double MeasuredHz,
    double Power,
    double AbsoluteDb,
    double RelativeDb);

/// <summary>Harmonic structure of one note.</summary>
public sealed record HarmonicProfile
{
    /// <summary>Fundamental the analysis was asked to use, in Hz.</summary>
    public double NominalFundamentalHz { get; init; }

    /// <summary>Fundamental refined from the measured partials, in Hz.</summary>
    public double FundamentalHz { get; init; }

    /// <summary>Measured harmonics in ascending order.</summary>
    public IReadOnlyList<HarmonicPeak> Harmonics { get; init; } = [];

    /// <summary>Sum of the power of all measured harmonics.</summary>
    public double TotalHarmonicPower { get; init; }

    /// <summary>Harmonic-to-noise ratio in dB, clamped to -20..+50. Everything that is not a harmonic counts as noise.</summary>
    public double HnrDb { get; init; }

    /// <summary>Amplitude-weighted mean deviation of the partials from exact harmonic positions, in cents.</summary>
    public double InharmonicityCents { get; init; }

    /// <summary>Slope of harmonic level versus harmonic number, in dB per octave (negative: levels fall with frequency).</summary>
    public double SlopeDbPerOctave { get; init; }

    /// <summary>Power of the even harmonics relative to the odd harmonics (fundamental included), in dB, clamped to -40..+40.</summary>
    public double EvenOddRatioDb { get; init; }

    /// <summary>Power of harmonics 2..5 relative to the total harmonic power, in dB.</summary>
    public double LowHarmonicsDb { get; init; }

    /// <summary>Power of harmonics 6 and up relative to the total harmonic power, in dB.</summary>
    public double HighHarmonicsDb { get; init; }
}
