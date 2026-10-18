using HearSay.Synthesis.Patches;

namespace HearSay.Playing;

/// <summary>
/// How loud HearSay plays a sound, wherever it plays it: one gain per sound that brings its reference note to a peak of
/// 0.5, and the key velocity as volume for a patch that does not react to velocity itself.
/// </summary>
public static class PlaybackLevel
{
    /// <summary>Peak of a sound's reference note, or of any audio played as it is, after the gain.</summary>
    public const float ReferencePeak = 0.5f;

    /// <summary>The gain that brings audio with a peak to <see cref="ReferencePeak"/>; 1 for silence.</summary>
    /// <param name="peak">Largest absolute sample value.</param>
    public static float GainFor(float peak) => peak > 0 ? ReferencePeak / peak : 1f;

    /// <summary>True when the patch reacts to key velocity; otherwise the velocity is applied as volume.</summary>
    /// <param name="patch">Patch.</param>
    public static bool RespondsToVelocity(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch.Operators.Any(static o => o.Enabled && o.VelocitySensitivity > 0);
    }

    /// <summary>The volume a note of a patch gets on top of the gain: 1 when the patch reacts to velocity, the velocity otherwise.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="velocity">Key velocity, 0..1.</param>
    public static float VelocityVolume(OperatorMatrixPatch patch, double velocity) => RespondsToVelocity(patch) ? 1f : (float)velocity;
}
