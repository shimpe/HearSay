using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Tests;

public sealed class LibraryAtlasTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;
    internal static readonly Lazy<StructureLibrary> Library = new(() => StructureLibrary.Build(Fm7, 3000, new Fm7CompatibleOperatorMatrixRenderer()));
    internal static readonly Lazy<LibraryAtlas> Atlas = new(() => LibraryAtlas.Build(Library.Value));

    private static void AssertPartition(LibraryAtlas atlas, LibraryFamily family)
    {
        IReadOnlyList<LibraryFamily> children = atlas.Children(family);
        Assert.InRange(children.Count, 1, LibraryAtlas.Branching);
        Assert.Equal(family.Members, children.SelectMany(static c => c.Members).Order());
        for (int k = 0; k < children.Count; k++)
        {
            LibraryFamily child = children[k];
            Assert.Contains(child.Medoid, child.Members);
            Assert.Equal(SpectralFingerprint.Length, child.Centroid.Count);
            Assert.Equal(family.Path.Append(k), child.Path);
            Assert.Equal(child.Members.Order(), child.Members);
            if (k > 0)
            {
                Assert.True(children[k - 1].Size > child.Size || (children[k - 1].Size == child.Size && children[k - 1].Medoid < child.Medoid));
            }
        }
    }

    [Fact]
    public void Children_PartitionTheirParent()
    {
        LibraryAtlas atlas = Atlas.Value;
        Assert.Equal(Enumerable.Range(0, 3000), atlas.Root.Members);
        AssertPartition(atlas, atlas.Root);
        Assert.Equal(LibraryAtlas.Branching, atlas.Children(atlas.Root).Count);
        LibraryFamily largest = atlas.Children(atlas.Root)[0];
        Assert.True(largest.Size > LibraryAtlas.Branching, "the 3000 sounds should not split into 40 families of 40 or fewer");
        AssertPartition(atlas, largest);
    }

    [Fact]
    public void SmallFamilies_HaveTheirMembersAsLeaves()
    {
        LibraryAtlas atlas = Atlas.Value;
        LibraryFamily small = atlas.Children(atlas.Root).First(static c => c.Size is > 1 and <= LibraryAtlas.Branching);
        IReadOnlyList<LibraryFamily> leaves = atlas.Children(small);
        Assert.All(leaves, static l => Assert.True(l.IsLeaf));
        Assert.Equal(small.Members, leaves.Select(static l => l.Medoid));
        Assert.Empty(atlas.Children(leaves[0]));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        LibraryAtlas a = Atlas.Value, b = LibraryAtlas.Build(Library.Value);
        IReadOnlyList<LibraryFamily> ca = a.Children(a.Root), cb = b.Children(b.Root);
        Assert.Equal(ca.Select(static c => c.Medoid), cb.Select(static c => c.Medoid));
        Assert.Equal(ca.Select(static c => c.Size), cb.Select(static c => c.Size));
        Assert.Equal(a.Children(ca[0]).Select(static c => c.Medoid), b.Children(cb[0]).Select(static c => c.Medoid));
    }

    [Fact]
    public void Locate_OfAMember_FollowsItsFamilies()
    {
        LibraryAtlas atlas = Atlas.Value;
        for (int i = 0; i < 3000; i += 97)
        {
            IReadOnlyList<LibraryFamily> path = atlas.Locate(atlas.Library.Fingerprint(i));
            Assert.Same(atlas.Root, path[0]);
            for (int k = 0; k < path.Count; k++)
            {
                Assert.Contains(i, path[k].Members);
                if (k > 0)
                {
                    Assert.Contains(path[k], atlas.Children(path[k - 1]));
                }
            }

            // The last family is the one whose children the sound is shown among.
            Assert.Contains(atlas.Children(path[^1]), c => (c.IsLeaf && c.Medoid == i) || (!c.IsLeaf && c.Members.Contains(i)));
        }
    }

    [Fact]
    public void IdenticalFingerprints_BecomeLeaves()
    {
        const int n = 100;
        byte[] one = Library.Value.Fingerprint(0).ToArray();
        var prints = new byte[n * SpectralFingerprint.Length];
        for (int i = 0; i < n; i++)
        {
            one.CopyTo(prints, i * SpectralFingerprint.Length);
        }

        StructureLibrary same = StructureLibrary.FromFingerprints(Fm7, [.. Enumerable.Range(1, n)], prints);
        LibraryAtlas atlas = LibraryAtlas.Build(same);
        IReadOnlyList<LibraryFamily> children = atlas.Children(atlas.Root);
        Assert.Equal(LibraryAtlas.Branching, children.Count);
        Assert.All(children, static c => Assert.True(c.IsLeaf));
        Assert.Equal(Enumerable.Range(0, LibraryAtlas.Branching), children.Select(static c => c.Medoid));
        Assert.Equal(new[] { atlas.Root }, atlas.Locate(one));
    }

    private static void AssertSameTopLevel(LibraryAtlas expected, LibraryAtlas actual)
    {
        IReadOnlyList<LibraryFamily> e = expected.Children(expected.Root), a = actual.Children(actual.Root);
        Assert.Equal(e.Count, a.Count);
        for (int k = 0; k < e.Count; k++)
        {
            Assert.Equal(e[k].Members, a[k].Members);
            Assert.Equal(e[k].Medoid, a[k].Medoid);
            Assert.Equal(e[k].Centroid, a[k].Centroid);
            Assert.Equal(e[k].Path, a[k].Path);
        }
    }

    [Fact]
    public void CacheFile_SitsNextToTheLibrary()
    {
        string library = Path.Combine("x", "supercollider-fmx6-v2-300000.bin");
        Assert.Equal(Path.Combine("x", "supercollider-fmx6-v2-300000-atlas-v1.bin"), LibraryAtlas.CacheFileOf(library));
    }

    [Fact]
    public void Open_KeepsTheTopLevelOnDisk_AndRebuildsABrokenFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-atlas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = LibraryAtlas.CacheFileOf(Path.Combine(directory, "test-v1-3000.bin"));
            LibraryAtlas first = LibraryAtlas.Open(Library.Value, file);
            Assert.True(File.Exists(file));
            AssertSameTopLevel(Atlas.Value, first);

            // Loaded, not rebuilt: the file is left alone.
            DateTime written = File.GetLastWriteTimeUtc(file);
            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
            Assert.Equal(written, File.GetLastWriteTimeUtc(file));

            // Truncated: rebuilt and written again in full.
            long length = new FileInfo(file).Length;
            using (FileStream stream = File.Open(file, FileMode.Open))
            {
                stream.SetLength(length / 2);
            }

            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
            Assert.Equal(length, new FileInfo(file).Length);

            // Written for a library of another size: rebuilt.
            StructureLibrary other = StructureLibrary.Build(Fm7, 200, new Fm7CompatibleOperatorMatrixRenderer());
            LibraryAtlas.Open(other, file);
            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
