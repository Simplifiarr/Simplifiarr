namespace Simplifiarr.Core.Services;

/// <summary>
/// Deliberately minimal right now - only connection testing. The
/// collection-management methods this used to have (create/find/add-to/
/// remove-from/delete a Jellyfin collection) were removed after both ways
/// of creating a native Jellyfin collection were confirmed broken against
/// a real Jellyfin 12.0.0 install, including a real incident where the
/// REST API repeatedly spawned duplicate internal library folders. See
/// CollectionBuilderService for the current (disabled) state of Jellyfin
/// sync.
/// </summary>
public interface IJellyfinClient
{
    /// <summary>
    /// Tests against the currently-saved Jellyfin URL/API key.
    /// </summary>
    Task<bool> TestConnectionAsync();

    /// <summary>
    /// Tests against explicit, possibly-unsaved values - lets the Settings
    /// page verify what's in the form before the user clicks Save.
    /// </summary>
    Task<bool> TestConnectionAsync(
        string url,
        string apiKey);
}
