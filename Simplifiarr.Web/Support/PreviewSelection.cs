namespace Simplifiarr.Web.Support;

/// <summary>
/// Tracks which single candidate currently has an open Promotion Preview
/// card, and derives the row/button styling every promotion table shows
/// for it - shared between Hardlinks' Movies/TV track (which reuses the
/// same instance across both, since TV episodes are MoviePromotionCandidate
/// too) and its Books track, previously duplicated per candidate type.
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

    public string RowClass(
        TCandidate candidate) =>
        IsSelected(candidate)
            ? "table-success"
            : string.Empty;

    public string ButtonClass(
        TCandidate candidate) =>
        IsSelected(candidate)
            ? "btn btn-sm btn-outline-success"
            : "btn btn-sm btn-success";

    public string ButtonText(
        TCandidate candidate) =>
        IsSelected(candidate)
            ? "Selected"
            : "Promote";

    public bool IsButtonDisabled(
        TCandidate candidate,
        bool isBusy) =>
        isBusy ||
        (Selected is not null && !IsSelected(candidate));
}
