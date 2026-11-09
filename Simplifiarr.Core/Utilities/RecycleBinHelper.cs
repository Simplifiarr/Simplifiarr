using System.Runtime.InteropServices;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Sends files to the Windows Recycle Bin instead of permanently deleting
/// them, via the classic SHFileOperation shell API - avoids pulling in
/// Microsoft.VisualBasic.FileIO (which needs a Windows-Desktop target
/// framework) for what's otherwise a single P/Invoke call.
/// </summary>
public static class RecycleBinHelper
{
    private const int FoDelete = 3;
    private const int FofAllowUndo = 0x40;
    private const int FofNoConfirmation = 0x10;
    private const int FofSilent = 0x4;
    private const int FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public int wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(
        ref ShFileOpStruct fileOp);

    /// <summary>
    /// Sends a single file to the Recycle Bin. Returns true on success.
    /// pFrom must be double-null-terminated per the shell API's contract.
    /// </summary>
    public static bool Send(
        string path)
    {
        var fileOp =
            new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = path + '\0' + '\0',
                fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi)
            };

        var result =
            SHFileOperation(ref fileOp);

        return result == 0 && !fileOp.fAnyOperationsAborted;
    }
}
