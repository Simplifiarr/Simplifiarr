using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Direct Windows calls for hardlink work. These replace launching
/// fsutil.exe / cmd.exe once per file, which was by far the slowest part
/// of a scan. No third-party code: just kernel32.
/// </summary>
public static class NativeHardlinks
{
    /// <summary>
    /// What Windows knows about a file's identity: how many names point at
    /// it, and an id that is the same for every one of those names.
    /// </summary>
    public readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        int LinkCount);

    public static bool IsSupported =>
        OperatingSystem.IsWindows();

    /// <summary>
    /// Returns null when the file can't be opened (missing, no access).
    /// </summary>
    public static FileIdentity? GetIdentity(string path)
    {
        if (!IsSupported)
        {
            return null;
        }

        // Asking for no access rights (0) reads metadata only, so this
        // works even while a torrent client has the file open.
        using var handle =
            CreateFileW(
                ToExtendedPath(path),
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagBackupSemantics,
                IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return new FileIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            (int)info.NumberOfLinks);
    }

    /// <summary>
    /// Creates <paramref name="newLinkPath"/> as another name for
    /// <paramref name="existingFilePath"/>. Returns null on success, or
    /// Windows' own explanation of what went wrong.
    /// </summary>
    public static string? CreateHardlink(
        string existingFilePath,
        string newLinkPath)
    {
        if (!IsSupported)
        {
            return "Hardlinks are only supported on Windows.";
        }

        if (CreateHardLinkW(
                ToExtendedPath(newLinkPath),
                ToExtendedPath(existingFilePath),
                IntPtr.Zero))
        {
            return null;
        }

        var error = Marshal.GetLastWin32Error();

        return $"{new Win32Exception(error).Message} (Windows error {error})";
    }

    /// <summary>
    /// Windows' classic path limit is 260 characters; the \\?\ prefix lifts
    /// it. .NET's own file APIs add this automatically, raw kernel32 calls
    /// don't.
    /// </summary>
    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (fullPath.Length < 248 ||
            fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
