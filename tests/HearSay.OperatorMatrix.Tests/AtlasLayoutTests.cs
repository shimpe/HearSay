using HearSay.OperatorMatrix.Priors;

namespace HearSay.OperatorMatrix.Tests;

public sealed class AtlasLayoutTests
{
    private static void AssertTidy(AtlasLayout layout)
    {
        int n = layout.Positions.Count;
        for (int i = 0; i < n; i++)
        {
            (double x, double y) = layout.Positions[i];
            double r = layout.Radii[i];
            Assert.True(double.IsFinite(x) && double.IsFinite(y) && r > 0);
            Assert.InRange(x, r - 1e-9, 1 - r + 1e-9);
            Assert.InRange(y, r - 1e-9, 1 - r + 1e-9);
            for (int j = i + 1; j < n; j++)
            {
                double d = Math.Sqrt(Math.Pow(x - layout.Positions[j].X, 2) + Math.Pow(y - layout.Positions[j].Y, 2));
                Assert.True(d >= r + layout.Radii[j] - 1e-6, $"nodes {i} and {j} overlap");
            }
        }

        Assert.All(layout.Edges, e => Assert.True(e.From < e.To && e.To < n));
        Assert.Equal(layout.Edges.Count, layout.Edges.Distinct().Count());
        if (n > 1)
        {
            Assert.All(Enumerable.Range(0, n), i => Assert.Contains(layout.Edges, e => e.From == i || e.To == i));
        }
    }

    private static (List<byte[]> Prints, List<int> Sizes) TopLevel()
    {
        LibraryAtlas atlas = LibraryAtlasTests.Atlas.Value;
        IReadOnlyList<LibraryFamily> children = atlas.Children(atlas.Root);
        return ([.. children.Select(c => atlas.Library.Fingerprint(c.Medoid).ToArray())], [.. children.Select(static c => c.Size)]);
    }

    [Fact]
    public void Layout_IsTidyAndDeterministic()
    {
        (List<byte[]> prints, List<int> sizes) = TopLevel();
        AtlasLayout a = AtlasLayout.Compute(prints, sizes), b = AtlasLayout.Compute(prints, sizes);
        AssertTidy(a);
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Edges, b.Edges);
        Assert.True(a.Radii[0] > a.Radii[^1], "the largest family should be drawn larger than the smallest");
    }

    [Fact]
    public void SimilarSounds_LieCloser()
    {
        (List<byte[]> prints, _) = TopLevel();
        byte[] near = (byte[])prints[0].Clone();
        int loud = Array.FindIndex(near, static b => b < 200);
        near[loud] += 8;

        // A slightly different sound, and the very same sound, both lie next to it.
        foreach (byte[] twin in new[] { near, prints[0] })
        {
            AtlasLayout layout = AtlasLayout.Compute([prints[0], twin, prints[1], prints[2]], [1, 1, 1, 1]);
            double Gap(int i, int j) => Math.Sqrt(Math.Pow(layout.Positions[i].X - layout.Positions[j].X, 2) + Math.Pow(layout.Positions[i].Y - layout.Positions[j].Y, 2));
            Assert.True(Gap(0, 1) < Gap(0, 2) && Gap(0, 1) < Gap(0, 3), $"gap {Gap(0, 1):F3} against {Gap(0, 2):F3} and {Gap(0, 3):F3}");
            Assert.True(Gap(0, 1) < layout.Radii[0] + layout.Radii[1] + 0.05, "neighbours should touch, not merely be nearer");
            Assert.Contains((0, 1), layout.Edges);
        }
    }

    [Fact]
    public void IdenticalPrints_AreSpreadWithoutOverlap()
    {
        (List<byte[]> prints, _) = TopLevel();
        AssertTidy(AtlasLayout.Compute([.. Enumerable.Repeat(prints[0], 41)], [.. Enumerable.Repeat(1, 41)]));
    }

    [Fact]
    public void OneAndNone()
    {
        (List<byte[]> prints, _) = TopLevel();
        Assert.Equal(new[] { (0.5, 0.5) }, AtlasLayout.Compute([prints[0]], [10]).Positions);
        Assert.Empty(AtlasLayout.Compute([], []).Positions);
    }

    [Fact]
    public void CenterOn_PutsTheNodeInTheMiddle_AndKeepsEverythingInside()
    {
        (List<byte[]> prints, List<int> sizes) = TopLevel();
        AtlasLayout layout = AtlasLayout.Compute(prints, sizes);
        AtlasLayout centred = layout.CenterOn(5);
        Assert.Equal(0.5, centred.Positions[5].X, 12);
        Assert.Equal(0.5, centred.Positions[5].Y, 12);
        AssertTidy(centred);
        Assert.Equal(layout.Edges, centred.Edges);
    }
}
