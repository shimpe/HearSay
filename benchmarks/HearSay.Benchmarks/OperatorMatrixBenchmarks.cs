using BenchmarkDotNet.Attributes;
using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Objective;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Benchmarks;

/// <summary>Offline FM7-compatible rendering: note length, operator count and routing density.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RenderBenchmarks
{
    private readonly Fm7CompatibleOperatorMatrixRenderer _renderer = new();
    private OperatorMatrixPatch _one = null!;
    private OperatorMatrixPatch _sparse = null!;
    private OperatorMatrixPatch _dense = null!;
    private OperatorMatrixPatch _feedback = null!;
    private RenderRequest _oneSecond = null!;
    private RenderRequest _threeSeconds = null!;

    [GlobalSetup]
    public void Setup()
    {
        OperatorEnvelope envelope = OperatorEnvelope.TwoStageDecay(0.003, 0.3, 0.4, 1.2, 0.1, 0.2);
        _one = ReferencePatches.Find("pure-sine")!.Patch;

        // Six operators, three stacks: 3 edges.
        _sparse = OperatorMatrixPatch.Silent(6);
        for (int i = 0; i < 6; i++)
        {
            _sparse = _sparse.WithOperator(i, OperatorPatch.Sine(OperatorFrequency.AtRatio(1 + i), envelope)).WithOutputLevel(i, i % 2 == 0 ? 0.3 : 0.0);
            if (i % 2 == 1)
            {
                _sparse = _sparse.WithEdge(new ModulationEdge(i, i - 1, true, 1.5));
            }
        }

        // Six operators, all 36 cells in use.
        _dense = _sparse;
        _feedback = _sparse;
        for (int d = 0; d < 6; d++)
        {
            for (int s = 0; s < 6; s++)
            {
                _dense = _dense.WithEdge(new ModulationEdge(s, d, true, 0.25));
            }

            _feedback = _feedback.WithEdge(new ModulationEdge(d, d, true, 1.2)).WithEdge(new ModulationEdge(d, (d + 1) % 6, true, 0.6));
        }

        _oneSecond = RenderRequest.Create(220, 0.8, 0.2);
        _threeSeconds = RenderRequest.Create(220, 2.5, 0.5);
    }

    [Benchmark(Description = "1 s, 1 operator")]
    public RenderedAudio OneOperator() => _renderer.Render(_one, _oneSecond);

    [Benchmark(Description = "1 s, 6 operators, sparse (3 edges)")]
    public RenderedAudio SixSparse() => _renderer.Render(_sparse, _oneSecond);

    [Benchmark(Description = "3 s, 6 operators, sparse (3 edges)")]
    public RenderedAudio SixSparseLong() => _renderer.Render(_sparse, _threeSeconds);

    [Benchmark(Description = "1 s, 6 operators, dense (36 edges)")]
    public RenderedAudio SixDense() => _renderer.Render(_dense, _oneSecond);

    [Benchmark(Description = "1 s, 6 operators, feedback-heavy (15 edges)")]
    public RenderedAudio SixFeedback() => _renderer.Render(_feedback, _oneSecond);
}

/// <summary>Cost of one candidate evaluation at the three levels, and of a parallel population.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EvaluationBenchmarks
{
    private readonly AudioObjective _objective = new();
    private PreparedObjectiveTarget _target = null!;
    private AudioNote _candidate = null!;
    private PatchEvaluator _serial = null!;
    private PatchEvaluator _parallel = null!;
    private OperatorMatrixPatch[] _population = null!;
    private int _shift;

    [GlobalSetup]
    public void Setup()
    {
        var renderer = new Fm7CompatibleOperatorMatrixRenderer();
        ReferencePatch reference = ReferencePatches.Find("electric-piano")!;
        RenderRequest request = RenderRequest.Create(reference.FrequencyHz, 0.8, 0.2);
        RenderedAudio target = renderer.Render(reference.Patch, request);
        _target = _objective.Prepare(new AudioTarget(target.Samples, target.SampleRate, reference.FrequencyHz));
        _target.Analysis.Partials.Count.ToString();
        RenderedAudio candidate = renderer.Render(ReferencePatches.Find("simple-2op")!.Patch, request);
        _candidate = candidate.ToAudioNote(reference.FrequencyHz);
        foreach (EvaluationLevel level in Enum.GetValues<EvaluationLevel>())
        {
            _objective.Evaluate(_target, _candidate, level);
        }

        _serial = new PatchEvaluator(renderer, _objective, _target, request, int.MaxValue, 0.02, null, 1);
        _parallel = new PatchEvaluator(renderer, _objective, _target, request, int.MaxValue, 0.02, null, Environment.ProcessorCount);
        _population = new OperatorMatrixPatch[16];
    }

    [Benchmark(Description = "comparison, coarse (1 s)")]
    public ObjectiveResult Coarse() => _objective.Evaluate(_target, _candidate, EvaluationLevel.Coarse);

    [Benchmark(Description = "comparison, normal (1 s)")]
    public ObjectiveResult Normal() => _objective.Evaluate(_target, _candidate, EvaluationLevel.Normal);

    [Benchmark(Description = "comparison, detailed (1 s)")]
    public ObjectiveResult Detailed() => _objective.Evaluate(_target, _candidate, EvaluationLevel.Detailed);

    [Benchmark(Description = "population of 16: render + normal, 1 thread")]
    public int PopulationSerial() => _serial.Evaluate(NextPopulation(), EvaluationLevel.Normal).Count;

    [Benchmark(Description = "population of 16: render + normal, all cores")]
    public int PopulationParallel() => _parallel.Evaluate(NextPopulation(), EvaluationLevel.Normal).Count;

    /// <summary>A fresh population every time, so that the evaluation cache never answers.</summary>
    private OperatorMatrixPatch[] NextPopulation()
    {
        OperatorMatrixPatch basePatch = ReferencePatches.Find("electric-piano")!.Patch;
        for (int i = 0; i < _population.Length; i++)
        {
            _shift++;
            _population[i] = basePatch.WithOutputLevel(0, 0.2 + (_shift * 1e-7));
        }

        return _population;
    }
}
