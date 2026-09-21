using HearSay.SuperCollider.Validation;

namespace HearSay.SuperCollider.Tests;

/// <summary>A test that needs a real SuperCollider with sc3-plugins; skipped on machines without one.</summary>
public sealed class SuperColliderFactAttribute : FactAttribute
{
    public SuperColliderFactAttribute()
    {
        if (Installation.Value is null)
        {
            Skip = "SuperCollider (sclang) was not found. Install SuperCollider and sc3-plugins or set HEARSAY_SCLANG.";
        }
    }

    internal static Lazy<SuperColliderInstallation?> Installation { get; } = new(SuperColliderInstallation.Find);
}
