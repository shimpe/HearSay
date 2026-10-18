namespace HearSay.Synthesis.Rendering;

/// <summary>
/// Base of a voice whose kernel renders one control block at a time: it starts the control source, decides the gate
/// of every block, advances the source once per block and hands out the samples in whatever lengths the caller asks
/// for. A kernel only implements <see cref="Start"/> (its state at construction) and <see cref="RenderBlock"/>.
/// </summary>
/// <remarks>
/// Mutable; render from one thread at a time. <see cref="Release"/> may be called from another thread; the gate is
/// read once at the start of every block.
/// </remarks>
public abstract class ControlBlockVoice : IOperatorMatrixVoice
{
    private readonly Func<bool, IControlSource> _start;
    private IControlSource? _controls;
    private float[] _block = [];
    private int _blockPosition;
    private volatile bool _released;
    private bool? _firstGateOpen;

    /// <summary>
    /// Creates a voice whose control source is created at the first <see cref="Render"/>, so that a voice released
    /// before it renders anything is constructed with the gate closed, as the offline render of a note of zero length
    /// is.
    /// </summary>
    /// <param name="sampleRate">Sample rate in hertz; that of the source <paramref name="start"/> returns.</param>
    /// <param name="start">Creates the control source; receives whether the gate is open in the first block.</param>
    protected ControlBlockVoice(int sampleRate, Func<bool, IControlSource> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        SampleRate = sampleRate;
        _start = start;
    }

    /// <summary>Creates a voice over a source whose gate is already decided, such as a compiled patch.</summary>
    /// <param name="controls">Control source, not advanced yet; the voice takes it over.</param>
    protected ControlBlockVoice(IControlSource controls)
        : this((controls ?? throw new ArgumentNullException(nameof(controls))).SampleRate, _ => controls)
    {
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public bool IsReleased => _released;

    /// <inheritdoc />
    public long SamplesRendered { get; private set; }

    /// <inheritdoc />
    public void Release() => _released = true;

    /// <inheritdoc />
    public void Render(Span<float> output)
    {
        if (output.IsEmpty)
        {
            return;
        }

        if (_controls is null)
        {
            // The gate is read once per block; block 0 keeps the gate the source was constructed with.
            bool gateOpen = !_released;
            _controls = _start(gateOpen);
            _firstGateOpen = gateOpen;
            _block = new float[_controls.BlockSize];
            _blockPosition = _block.Length;
            Start(_controls);
        }

        int written = 0;
        while (written < output.Length)
        {
            if (_blockPosition == _block.Length)
            {
                _controls.Advance(_firstGateOpen ?? !_released);
                _firstGateOpen = null;
                Span<float> rest = output[written..];
                if (rest.Length >= _block.Length)
                {
                    // A whole block fits: render it straight into the output.
                    RenderBlock(_controls, rest[.._block.Length]);
                    written += _block.Length;
                    continue;
                }

                RenderBlock(_controls, _block);
                _blockPosition = 0;
            }

            int count = Math.Min(_block.Length - _blockPosition, output.Length - written);
            _block.AsSpan(_blockPosition, count).CopyTo(output[written..]);
            _blockPosition += count;
            written += count;
        }

        SamplesRendered += output.Length;
    }

    /// <summary>
    /// Sets up the kernel from the construction values of the source, before any block; this is where a unit that
    /// computes a sample when it is constructed does so.
    /// </summary>
    /// <param name="controls">Control source, not advanced yet.</param>
    protected abstract void Start(IControlSource controls);

    /// <summary>
    /// Renders one whole control block, ramping from the values of the previous block to the current values of the
    /// source, which has just been advanced to this block.
    /// </summary>
    /// <param name="controls">Control source.</param>
    /// <param name="block">Receives exactly <see cref="IControlSource.BlockSize"/> samples.</param>
    protected abstract void RenderBlock(IControlSource controls, Span<float> block);
}
