namespace Simplifiarr.Core.Models;

public enum HistoryActionType
{
    ConversionSucceeded,
    ConversionFailed,
    QuarantineDeleted,
    BatchCompleted,
    PromotionSucceeded,
    PromotionFailed,
    DownloadCopyRemoved,
    JunkFileQuarantined,
    QuarantineRestored,
    DuplicateQuarantined
}

