namespace Simplifiarr.Core.Models;

public class CollectionBuildResult
{
    public int Requested { get; set; }

    public int Synced { get; set; }

    public int Failed { get; set; }

    public List<string> FailureMessages { get; set; } =
        [];
}
