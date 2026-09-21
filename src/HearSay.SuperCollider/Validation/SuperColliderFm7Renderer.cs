using System.Diagnostics;
using System.Globalization;
using System.Text;
using HearSay.Audio;
using HearSay.SuperCollider.FM7;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.SuperCollider.Validation;

/// <summary>
/// Renders patches with a real SuperCollider: the exported SynthDef is played by scsynth in non-realtime mode and the
/// resulting file is read back. Slow (a second or two per batch) and meant for validating the export mapping, not for
/// searching. Needs SuperCollider with sc3-plugins.
/// </summary>
public sealed class SuperColliderFm7Renderer : IOperatorMatrixRenderer
{
    private readonly SuperColliderInstallation _installation;
    private readonly SuperColliderFm7Exporter _exporter = new();

    /// <summary>Creates a renderer that uses the given installation.</summary>
    /// <param name="installation">SuperCollider installation.</param>
    public SuperColliderFm7Renderer(SuperColliderInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        _installation = installation;
    }

    /// <summary>Longest time one sclang run may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public SynthesisCapabilities Capabilities => SynthesisProfiles.SuperColliderFm7.Capabilities;

    /// <inheritdoc />
    public RenderProfile RenderProfile => RenderProfile.BackendExact;

    /// <inheritdoc />
    public RenderedAudio Render(OperatorMatrixPatch patch, RenderRequest request) => RenderMany([(patch, request)])[0];

    /// <summary>Renders several notes in one sclang session, which avoids paying the interpreter start-up per note.</summary>
    /// <param name="jobs">Patches and the notes to play with them.</param>
    /// <exception cref="InvalidOperationException">SuperCollider failed or did not produce a file.</exception>
    public IReadOnlyList<RenderedAudio> RenderMany(IReadOnlyList<(OperatorMatrixPatch Patch, RenderRequest Request)> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return [];
        }

        string directory = Path.Combine(Path.GetTempPath(), "hearsay-sc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = new StringBuilder();
            script.Append("(\nvar jobs, run;\njobs = [\n");
            for (int i = 0; i < jobs.Count; i++)
            {
                (OperatorMatrixPatch patch, RenderRequest request) = jobs[i];
                string def = _exporter.ExportSynthDef(patch, "HearSayJob" + i.ToString(CultureInfo.InvariantCulture));
                script.Append("[\n").Append(def).Append(",\n");
                script.Append(ScString(Path.Combine(directory, $"job{i}.osc"))).Append(", ");
                script.Append(ScString(Path.Combine(directory, $"job{i}.wav"))).Append(", ");
                script.Append(Invariant(request.FrequencyHz)).Append(", ");
                script.Append(Invariant(request.Velocity)).Append(", ");
                script.Append(Invariant((double)request.NoteSamples / request.SampleRate)).Append(", ");
                script.Append(Invariant((double)request.TotalSamples / request.SampleRate)).Append(", ");
                script.Append(request.SampleRate.ToString(CultureInfo.InvariantCulture));
                script.Append("\n]").Append(i < jobs.Count - 1 ? "," : string.Empty).Append('\n');
            }

            script.Append("];\n");
            script.Append("run = { |i|\n");
            script.Append("\tif (i >= jobs.size) { \"HEARSAY_DONE\".postln; 0.exit } {\n");
            script.Append("\t\tvar job = jobs[i];\n");
            script.Append("\t\tvar def = job[0];\n");
            script.Append("\t\tvar score = Score([\n");
            script.Append("\t\t\t[0.0, [\\d_recv, def.asBytes]],\n");
            script.Append("\t\t\t[0.0, [\\s_new, def.name, 1000, 0, 0, \\freq, job[3], \\velocity, job[4], \\amp, 1.0]],\n");
            script.Append("\t\t\t[job[5], [\\n_set, 1000, \\gate, 0]],\n");
            script.Append("\t\t\t[job[6], [\\c_set, 0, 0]]\n");
            script.Append("\t\t]);\n");
            script.Append("\t\tscore.recordNRT(job[1], job[2], sampleRate: job[7], headerFormat: \"WAV\", sampleFormat: \"float\",\n");
            script.Append("\t\t\toptions: ServerOptions.new.numOutputBusChannels_(2).numInputBusChannels_(0).blockSize_(");
            script.Append(SynthesisProfiles.SuperColliderFm7.ControlBlockSize.ToString(CultureInfo.InvariantCulture));
            script.Append("),\n\t\t\taction: { run.(i + 1) });\n");
            script.Append("\t}\n};\nrun.(0);\n)\n");

            string scriptPath = Path.Combine(directory, "render.scd");
            File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(false));
            string log = RunSclang(scriptPath, directory);

            var results = new RenderedAudio[jobs.Count];
            for (int i = 0; i < jobs.Count; i++)
            {
                string wav = Path.Combine(directory, $"job{i}.wav");
                if (!File.Exists(wav))
                {
                    throw new InvalidOperationException($"SuperCollider did not render job {i}. Is sc3-plugins (FM7) installed? Output:{Environment.NewLine}{log}");
                }

                AudioNote note = WavFile.Read(wav, WavChannelMode.SingleChannel, channelIndex: 0);
                float[] samples = note.Samples.ToArray();
                int expected = jobs[i].Request.TotalSamples;
                if (samples.Length != expected)
                {
                    Array.Resize(ref samples, expected);
                }

                results[i] = new RenderedAudio(samples, note.SampleRate);
            }

            return results;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A scanner may still hold a file; the folder is in the temp directory anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string RunSclang(string scriptPath, string workingDirectory)
    {
        var start = new ProcessStartInfo(_installation.SclangPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(scriptPath);
        using var process = new Process { StartInfo = start };
        var log = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"sclang did not finish within {Timeout}. Output:{Environment.NewLine}{log}");
        }

        process.WaitForExit();
        string text;
        lock (log)
        {
            text = log.ToString();
        }

        if (!text.Contains("HEARSAY_DONE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"sclang did not complete the render script. Output:{Environment.NewLine}{text}");
        }

        return text;
    }

    private static void Append(StringBuilder log, string? line)
    {
        if (line is not null)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }
    }

    private static string ScString(string path) => "\"" + path.Replace('\\', '/') + "\"";

    private static string Invariant(double value) => value.ToString("0.0###############", CultureInfo.InvariantCulture);
}
