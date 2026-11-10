namespace Simplifiarr.Web.Support;

/// <summary>
/// Tracks which single candidate currently has its preview open, and
/// derives the row and button styling a results table shows for it.
/// Shared by Hardlinks' Movies/TV tabs (one instance for both, since TV
/// episodes are MoviePromotionCandidate too) and the shared media-type
/// section.
/// </summary>
public sealed class PreviewSelection<TCandidate>
{
    public TCandidate? Selected { get; private set; }

    public bool IsSelected(
        TCandidate candidate) =>
        ReferenceEquals(candidate, Selected);

    public void Select(
        TCandidate candidate) =>
        Selected = candidate;

    public void Clear() =>
        Selected = default;

    /// <summary>Highlights the row whose preview is open.</summary>
    public string RowClass(
        TCandidate candidate) =>
        IsSelected(candidate)
            ? "table-active"
            : string.Empty;

    public string ButtonClass(
        TCandidate candidate) =>
        "btn btn-sm btn-secondary";

    public string ButtonText(
        TCandidate candidate) =>
        IsSelected(candidate)
            ? "Previewing"
            : "Preview";

    /// <summary>
    /// Only one preview at a time: while one is open, the others wait.
    /// </summary>
    public bool IsButtonDisabled(
        TCandidate candidate,
        bool isBusy) =>
        isBusy ||
        (Selected is not null && !IsSelected(candidate));
}
