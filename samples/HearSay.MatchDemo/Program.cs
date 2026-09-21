// Round-trip demonstration: every reference patch is rendered to a target, the patch is hidden, and the matcher has
// to find a patch that sounds like the target. Recovering the original parameters is not the goal; sounding alike is.
//
//   dotnet run -c Release --project samples/HearSay.MatchDemo -- [--budget 20000] [--only bell,bass] [--output demo-results] [--supercollider]
using System.Globalization;
using HearSay;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

int budget = 20000;
string output = "demo-results";
string[]? only = null;
bool superCollider = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--budget":
            budget = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--only":
            only = args[++i].Split(',');
            break;
        case "--output":
            output = args[++i];
            break;
        case "--supercollider":
            superCollider = true;
            break;
    }
}

var renderer = new Fm7CompatibleOperatorMatrixRenderer();
var matcher = new OperatorMatrixPatchMatcher();
SuperColliderInstallation? installation = superCollider ? SuperColliderInstallation.Find() : null;
if (superCollider && installation is null)
{
    Console.WriteLine("SuperCollider was not found; skipping the native-versus-SuperCollider comparison.");
}

BackendEquivalenceValidator? validator = installation is null ? null : new BackendEquivalenceValidator(renderer, new SuperColliderFm7Renderer(installation));
Console.WriteLine($"{"target",-22} {"initial",8} {"final",8} {"gain",6} {"similarity",10} {"renders",8} {"seconds",8}  {"sc distance",11}  found structure");
foreach (ReferencePatch reference in ReferencePatches.All)
{
    if (only is not null && !only.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
    {
        continue;
    }

    string folder = Path.Combine(output, reference.Name);
    Directory.CreateDirectory(folder);
    RenderedAudio targetAudio = renderer.Render(reference.Patch, RenderRequest.Create(reference.FrequencyHz, reference.NoteSeconds, reference.ReleaseSeconds));
    targetAudio.WriteWav(Path.Combine(folder, "target.wav"), normalizePeakTo: 0.7f);
    File.WriteAllText(Path.Combine(folder, "hidden-patch.json"), reference.Patch.ToJson());

    var target = new AudioTarget(targetAudio.Samples, targetAudio.SampleRate, reference.FrequencyHz);
    PatchMatchResult result = matcher.Match(target, new PatchMatchOptions { EvaluationBudget = budget });
    PatchMatchResultWriter.Write(result, folder);

    string sc = "-";
    if (validator is not null)
    {
        BackendExportValidation validation = validator.Validate([(result.BestPatch, result.Request)])[0];
        sc = validation.OverallDistance.ToString("F5", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(folder, "supercollider-validation.json"), HearSayJson.Serialize(validation));
    }

    SearchSummary s = result.SearchSummary;
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{reference.Name,-22} {s.InitialAudioLoss,8:F4} {s.FinalAudioLoss,8:F4} {s.InitialAudioLoss / Math.Max(1e-9, s.FinalAudioLoss),5:F1}x {result.Comparison.Scores.Similarity,10:F3} {s.EvaluationsUsed,8} {s.Elapsed.TotalSeconds,8:F1}  {sc,11}  {result.AlternativePatches[0].Regime}"));
}
