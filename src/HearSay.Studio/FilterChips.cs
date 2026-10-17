using System.ComponentModel;
using System.Globalization;
using HearSay.OperatorMatrix.Priors;

namespace HearSay.Studio;

/// <summary>One chip of the library filter: a value, how many sounds it would show, and whether it is chosen.</summary>
public sealed class TraitChip : INotifyPropertyChanged
{
    private readonly Action<TraitChip> _toggled;
    private bool _isChosen;
    private int _count;

    /// <summary>Creates a chip.</summary>
    /// <param name="value">Its value.</param>
    /// <param name="count">Sounds that match the rest of the filter and have the value.</param>
    /// <param name="toggled">Called when the chip is chosen or released by the user.</param>
    public TraitChip(TraitValue value, int count, Action<TraitChip> toggled)
    {
        Value = value;
        _count = count;
        _toggled = toggled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The value.</summary>
    public TraitValue Value { get; }

    /// <summary>Chosen; set by the toggle button.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (_isChosen == value)
            {
                return;
            }

            _isChosen = value;
            Changed(nameof(IsChosen));
            _toggled(this);
        }
    }

    /// <summary>Sounds that match the rest of the filter and have the value.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            Changed(nameof(Count));
            Changed(nameof(Text));
            Changed(nameof(IsEmpty));
        }
    }

    /// <summary>Label and count, for example <c>saw 12,408</c>.</summary>
    public string Text => Value.Label + " " + Count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>True when no sound would be shown.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Releases the chip without telling the owner.</summary>
    internal void Release()
    {
        if (_isChosen)
        {
            _isChosen = false;
            Changed(nameof(IsChosen));
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A row of the filter panel: one facet and its chips.</summary>
/// <param name="Title">Row title.</param>
/// <param name="Chips">Chips, in panel order.</param>
public sealed record TraitRow(string Title, IReadOnlyList<TraitChip> Chips);
