namespace Simplifiarr.Web.Components.Shared;

public class DriveScanCardRow
{
    public string Label { get; set; } =
        string.Empty;

    public int Count { get; set; }

    public string LastScanText { get; set; } =
        string.Empty;

    public bool IsScanning { get; set; }

    public bool IsDisabled { get; set; }

    public Func<Task>? OnScanClick { get; set; }
}
