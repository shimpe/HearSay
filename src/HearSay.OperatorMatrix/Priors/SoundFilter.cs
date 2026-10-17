using System.Collections.Immutable;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// The chips chosen in the library filter. Values of one facet are alternatives, facets are combined; a facet without
/// a chosen value does not filter. Immutable.
/// </summary>
public sealed class SoundFilter
{
    private static readonly int FacetCount = Enum.GetValues<TraitFacet>().Length;
    private readonly ImmutableHashSet<TraitValue> _chosen;
    private readonly TraitValue[][] _facets;

    private SoundFilter(ImmutableHashSet<TraitValue> chosen)
    {
        _chosen = chosen;
        _facets = [.. chosen.GroupBy(static v => v.Facet).OrderBy(static g => g.Key).Select(static g => g.OrderBy(static v => v.Value).ToArray())];
    }

    /// <summary>Nothing chosen: every sound matches.</summary>
    public static SoundFilter None { get; } = new(ImmutableHashSet<TraitValue>.Empty);

    /// <summary>The chosen values.</summary>
    public IReadOnlyCollection<TraitValue> Chosen => _chosen;

    /// <summary>True when nothing is chosen.</summary>
    public bool IsEmpty => _chosen.IsEmpty;

    /// <summary>True when the value is chosen.</summary>
    /// <param name="value">Value.</param>
    public bool Contains(TraitValue value) => _chosen.Contains(value);

    /// <summary>A filter with the value chosen as well.</summary>
    /// <param name="value">Value.</param>
    public SoundFilter With(TraitValue value) => new(_chosen.Add(value));

    /// <summary>A filter without the value.</summary>
    /// <param name="value">Value.</param>
    public SoundFilter Without(TraitValue value) => new(_chosen.Remove(value));

    /// <summary>True when the sound has a chosen value of every facet with one.</summary>
    /// <param name="traits">The sound's traits.</param>
    public bool Matches(SoundTraits traits)
    {
        foreach (TraitValue[] facet in _facets)
        {
            if (!HasAny(traits, facet))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Indices of the matching sounds, ascending.</summary>
    /// <param name="traits">Traits of all sounds, by index.</param>
    public int[] Members(IReadOnlyList<SoundTraits> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        var members = new List<int>();
        for (int i = 0; i < traits.Count; i++)
        {
            if (Matches(traits[i]))
            {
                members.Add(i);
            }
        }

        return [.. members];
    }

    /// <summary>Per value, the number of sounds that match every other facet and have the value; one pass.</summary>
    /// <param name="traits">Traits of all sounds.</param>
    /// <param name="values">The chip values.</param>
    public int[] Counts(IReadOnlyList<SoundTraits> traits, IReadOnlyList<TraitValue> values)
    {
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(values);
        var counts = new int[values.Count];
        var missed = new bool[FacetCount];
        for (int i = 0; i < traits.Count; i++)
        {
            SoundTraits t = traits[i];
            Array.Clear(missed);
            int misses = 0;
            foreach (TraitValue[] facet in _facets)
            {
                if (!HasAny(t, facet))
                {
                    missed[(int)facet[0].Facet] = true;
                    misses++;
                }
            }

            if (misses > 1)
            {
                continue;
            }

            for (int k = 0; k < values.Count; k++)
            {
                TraitValue v = values[k];
                if ((misses == 0 || missed[(int)v.Facet]) && t.Has(v))
                {
                    counts[k]++;
                }
            }
        }

        return counts;
    }

    /// <summary>The chosen values for the summary line: facets in panel order, alternatives joined with "or".</summary>
    public string Describe() => string.Join(" · ", _facets.Select(static f => string.Join(" or ", f.Select(static v => v.Label))));

    private static bool HasAny(SoundTraits traits, TraitValue[] facet)
    {
        foreach (TraitValue v in facet)
        {
            if (traits.Has(v))
            {
                return true;
            }
        }

        return false;
    }
}
