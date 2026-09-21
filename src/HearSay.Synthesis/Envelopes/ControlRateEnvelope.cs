using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Envelopes;

/// <summary>
/// Steps an <see cref="OperatorEnvelope"/> once per control block, with the stage logic of the SuperCollider
/// <c>EnvGen.kr</c> UGen: segment lengths are whole numbers of blocks with the rounding remainder carried into the
/// next segment, the value of a block is written before the level advances, and closing the gate jumps to the
/// release section from the level reached so far.
/// </summary>
/// <remarks>Mutable value type; use one instance per envelope and per render.</remarks>
public struct ControlRateEnvelope
{
    private const int NotStarted = 1_000_000_000;

    private readonly double[] _levels;
    private readonly double[] _durations;
    private readonly EnvelopeCurve[] _curves;
    private readonly int _releaseNode;
    private readonly double _controlRate;

    private double _level;
    private double _endLevel;
    private double _grow;
    private double _residual;
    private int _counter;
    private int _stage;
    private Shape _shape;
    private bool _previousGate;
    private bool _released;
    private bool _done;

    private enum Shape
    {
        Step,
        Linear,
        Exponential,
        Sustain,
    }

    /// <summary>Prepares an envelope for stepping.</summary>
    /// <param name="envelope">Envelope to run.</param>
    /// <param name="controlRate">Control blocks per second.</param>
    /// <param name="timeScale">Multiplier of all segment durations.</param>
    public ControlRateEnvelope(OperatorEnvelope envelope, double controlRate, double timeScale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        int count = envelope.Points.Count;
        _levels = new double[count];
        _durations = new double[count];
        _curves = new EnvelopeCurve[count];
        for (int i = 0; i < count; i++)
        {
            // The backend stores envelope data as 32-bit floats.
            _levels[i] = (float)envelope.EffectiveLevel(i);
            _durations[i] = (float)envelope.Points[i].TimeSeconds * (double)(float)timeScale;
            _curves[i] = envelope.Points[i].Curve;
        }

        _releaseNode = envelope.SustainPoint ?? -99;
        _controlRate = controlRate;
        _level = _endLevel = _levels[0];
        _counter = 0;
        _stage = NotStarted;
        _shape = Shape.Step;
        _grow = 0;
        _residual = 0;
        _previousGate = false;
        _released = false;
        _done = false;
    }

    /// <summary>True once the last segment has finished.</summary>
    public readonly bool IsDone => _done;

    /// <summary>Returns the value of the next control block.</summary>
    /// <param name="gate">Whether the gate is open during this block.</param>
    public double Next(bool gate)
    {
        if (!_previousGate && gate)
        {
            _stage = -1;
            _released = false;
            _done = false;
            _counter = 0;
            _residual = 0;
        }
        else if (_previousGate && !gate && _releaseNode >= 0 && !_released)
        {
            _counter = 0;
            _stage = _releaseNode - 1;
            _released = true;
            _residual = 0;
        }

        _previousGate = gate;
        if (_counter <= 0)
        {
            NextSegment();
        }

        double output = _level;
        switch (_shape)
        {
            case Shape.Linear:
                _level += _grow;
                break;
            case Shape.Exponential:
                _level *= _grow;
                break;
        }

        if (_done)
        {
            _level = _endLevel;
        }

        _counter--;
        return output;
    }

    private void NextSegment()
    {
        int stages = _levels.Length - 1;
        if (_stage + 1 >= stages)
        {
            _counter = int.MaxValue;
            _shape = Shape.Step;
            _done = true;
        }
        else if (_stage + 1 == _releaseNode && !_released)
        {
            _counter = int.MaxValue;
            _shape = Shape.Sustain;
            _level = _endLevel;
        }
        else
        {
            _stage++;
            InitSegment();
        }
    }

    private void InitSegment()
    {
        int point = _stage + 1;
        double endLevel = _levels[point];
        _endLevel = endLevel;
        double lengthInBlocks = (_durations[point] * _controlRate) + _residual;
        double fractional = Math.Max(1.0, lengthInBlocks);
        _counter = Math.Max(1, (int)lengthInBlocks);
        _residual = lengthInBlocks - _counter;
        _shape = _curves[point] switch
        {
            EnvelopeCurve.Exponential => Shape.Exponential,
            EnvelopeCurve.Step => Shape.Step,
            _ => Shape.Linear,
        };
        if (_counter == 1)
        {
            _shape = Shape.Linear;
        }

        switch (_shape)
        {
            case Shape.Step:
                _level = endLevel;
                break;
            case Shape.Linear:
                _grow = (endLevel - _level) / fractional;
                break;
            case Shape.Exponential:
                _grow = Math.Pow(endLevel / _level, 1.0 / fractional);
                break;
        }
    }
}
