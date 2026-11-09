namespace Simplifiarr.Web.Support;

/// <summary>
/// Checkbox-selection bookkeeping shared by every page that lets the user
/// pick a subset of scanned candidates for a batch action (Potential
/// Waste, Junk Cleanup, Hardlinks) - each page previously hand-rolled its
/// own HashSet&lt;string&gt; for this.
/// </summary>
public sealed class CandidateSelection<TCandidate>
{
    private readonly Func<TCandidate, string> _keySelector;

    public CandidateSelection(
        Func<TCandidate, string> keySelector)
    {
        _keySelector = keySelector;
    }

    public HashSet<string> SelectedKeys { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count =>
        SelectedKeys.Count;

    public bool IsSelected(
        TCandidate candidate) =>
        SelectedKeys.Contains(_keySelector(candidate));

    public void SetSelected(
        TCandidate candidate,
        bool selected)
    {
        var key =
            _keySelector(candidate);

        if (selected)
        {
            SelectedKeys.Add(key);
        }
        else
        {
            SelectedKeys.Remove(key);
        }
    }

    public void Remove(
        TCandidate candidate) =>
        SelectedKeys.Remove(_keySelector(candidate));

    public void ToggleSelectAll(
        IReadOnlyCollection<TCandidate> all)
    {
        if (SelectedKeys.Count == all.Count)
        {
            SelectedKeys.Clear();
        }
        else
        {
            foreach (var candidate in all)
            {
                SelectedKeys.Add(_keySelector(candidate));
            }
        }
    }

    public void Clear() =>
        SelectedKeys.Clear();
}
