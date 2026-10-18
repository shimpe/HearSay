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
/// Request rendered once, at full velocity and at its own sample rate, to find the gain (<see cref="PlaybackLevel.GainFor"/>).
/// </param>
public sealed record Sound(OperatorMatrixPatch Patch, SynthesisProfile Profile, RenderRequest GainReference)
{
    /// <summary>True when the patch reacts to key velocity; otherwise the velocity of a note is applied as volume.</summary>
    public bool RespondsToVelocity => PlaybackLevel.RespondsToVelocity(Patch);

    /// <summary>
    /// The gain to play at when it is already known, for example the gain of the original of an edited patch, which
    /// must not be normalised again, or of a favourite that kept it; null to compute it from <see cref="GainReference"/>.
    /// It is part of the sound's identity: a sound with a fixed gain is another sound than the same patch without one.
    /// </summary>
    public float? FixedGain { get; init; }

    /// <summary>The gain of the sound: <see cref="FixedGain"/> when set, else <see cref="PlaybackLevel.GainFor"/> the peak of <see cref="GainReference"/> at full velocity.</summary>
    /// <remarks>Renders the reference note unless the gain is fixed; seconds for a slow profile, so not on an interface or audio thread.</remarks>
    /// <exception cref="NotSupportedException">No renderer implements the profile.</exception>
    /// <exception cref="PatchNotSupportedException">The profile's renderer cannot play the patch.</exception>
    public float ComputeGain() =>
        FixedGain ?? PlaybackLevel.GainFor(SynthesisBackends.RendererFor(Profile).Render(Patch, GainReference with { Velocity = 1.0 }).Peak);
}
