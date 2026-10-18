using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Playing;

/// <summary>
/// A patch as it is played from the keyboard: the patch, the profile whose renderer plays it, and the note whose
/// loudest sample at full velocity sets its gain, so that every note of the sound is played at the same gain and the
/// differences between keys and velocities survive.
/// </summary>
/// <param name="Patch">Patch.</param>
/// <param name="Profile">Profile whose renderer plays the patch.</param>
/// <param name="GainReference">
/// Request rendered once, at full velocity and at its own sample rate, to find the gain: 0.5 over its peak.
/// </param>
public sealed record Sound(OperatorMatrixPatch Patch, SynthesisProfile Profile, RenderRequest GainReference)
{
    /// <summary>True when the patch reacts to key velocity; otherwise the velocity of a note is applied as volume.</summary>
    public bool RespondsToVelocity => Patch.Operators.Any(static o => o.Enabled && o.VelocitySensitivity > 0);

    /// <summary>The gain of the sound: 0.5 over the peak of <see cref="GainReference"/> at full velocity, 1 when it is silent.</summary>
    /// <remarks>Renders the reference note; seconds for a slow profile, so not on an interface or audio thread.</remarks>
    /// <exception cref="NotSupportedException">No renderer implements the profile.</exception>
    /// <exception cref="PatchNotSupportedException">The profile's renderer cannot play the patch.</exception>
    public float ComputeGain()
    {
        RenderedAudio full = SynthesisBackends.RendererFor(Profile).Render(Patch, GainReference with { Velocity = 1.0 });
        return full.Peak > 0 ? 0.5f / full.Peak : 1f;
    }
}
