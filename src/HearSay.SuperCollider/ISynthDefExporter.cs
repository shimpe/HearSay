using HearSay.Synthesis.Patches;

namespace HearSay.SuperCollider;

/// <summary>An exporter that can also write a bare <c>SynthDef(...)</c> expression for non-realtime rendering.</summary>
public interface ISynthDefExporter
{
    /// <summary>Returns the <c>SynthDef(...)</c> expression, without <c>.add</c>.</summary>
    /// <param name="patch">Patch to export.</param>
    /// <param name="synthDefName">Name of the SynthDef.</param>
    string ExportSynthDef(OperatorMatrixPatch patch, string synthDefName = "HearSayPatch");

    /// <summary>Control block size the backend runs at.</summary>
    int ControlBlockSize { get; }
}
