using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class FileScannerService : IFileScannerService
{
    private const int ProgressInterval = 500;

    // Hidden and system files are included, as they always have been. A
    // folder the service account can't open is skipped instead of failing
    // the whole scan.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None
    };

    public Task<List<FileRecord>> ScanAsync(
        string path,
        CancellationToken cancellationToken = default,
        Action<int>? onProgress = null)
    {
        if (!Directory.Exists(path))
        {
            return Task.FromResult(new List<FileRecord>());
        }

        // Walking a large library takes a while; do it off the caller's
        // thread so the page that asked stays responsive.
        return Task.Run(
            () => Walk(path, cancellationToken, onProgress),
            cancellationToken);
    }

    private static List<FileRecord> Walk(
        string path,
        CancellationToken cancellationToken,
        Action<int>? onProgress)
    {
        var files = new List<FileRecord>();

        // Enumerating through DirectoryInfo hands back each file's size and
        // dates along with its name, so there is no second trip to the disk
        // per file.
        foreach (var info in new DirectoryInfo(path)
                     .EnumerateFiles("*", WalkOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            files.Add(
                new FileRecord
                {
                    FullPath = info.FullName,
                    FileName = info.Name,
                    Extension = info.Extension,
                    FileType = GetFileType(info.Extension),
                    SizeBytes = info.Length,
                    LastModifiedUtc = info.LastWriteTimeUtc
                });

            if (files.Count % ProgressInterval == 0)
            {
                onProgress?.Invoke(files.Count);
            }
        }

        onProgress?.Invoke(files.Count);

        return files;
    }

    private static FileType GetFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mkv" => FileType.Video,
            ".mp4" => FileType.Video,
            ".m4v" => FileType.Video,
            ".avi" => FileType.Video,
            ".mov" => FileType.Video,
            ".wmv" => FileType.Video,
            ".webm" => FileType.Video,
            ".ts" => FileType.Video,
            ".m2ts" => FileType.Video,
            ".mts" => FileType.Video,
            ".mpg" => FileType.Video,
            ".mpeg" => FileType.Video,
            ".m2v" => FileType.Video,
            ".flv" => FileType.Video,
            ".vob" => FileType.Video,
            ".ogv" => FileType.Video,
            ".3gp" => FileType.Video,
            ".divx" => FileType.Video,
            ".rm" => FileType.Video,
            ".rmvb" => FileType.Video,
            ".asf" => FileType.Video,
            ".iso" => FileType.Video,

            ".mp3" => FileType.Audio,
            ".flac" => FileType.Audio,
            ".m4b" => FileType.Audio,
            ".m4a" => FileType.Audio,
            ".aac" => FileType.Audio,
            ".ogg" => FileType.Audio,
            ".oga" => FileType.Audio,
            ".opus" => FileType.Audio,
            ".wav" => FileType.Audio,
            ".wma" => FileType.Audio,
            ".ape" => FileType.Audio,
            ".wv" => FileType.Audio,
            ".aiff" => FileType.Audio,
            ".dsf" => FileType.Audio,
            ".dff" => FileType.Audio,

            ".epub" => FileType.Book,
            ".pdf" => FileType.Book,
            ".mobi" => FileType.Book,
            ".azw" => FileType.Book,
            ".azw3" => FileType.Book,
            ".fb2" => FileType.Book,
            ".lit" => FileType.Book,
            ".djvu" => FileType.Book,

            ".cbz" => FileType.Comic,
            ".cbr" => FileType.Comic,
            ".cb7" => FileType.Comic,
            ".cbt" => FileType.Comic,
            ".cba" => FileType.Comic,

            ".srt" => FileType.Subtitle,
            ".ass" => FileType.Subtitle,
            ".ssa" => FileType.Subtitle,
            ".sub" => FileType.Subtitle,
            ".idx" => FileType.Subtitle,
            ".vtt" => FileType.Subtitle,
            ".smi" => FileType.Subtitle,

            ".jpg" => FileType.Metadata,
            ".jpeg" => FileType.Metadata,
            ".png" => FileType.Metadata,
            ".gif" => FileType.Metadata,
            ".bmp" => FileType.Metadata,
            ".webp" => FileType.Metadata,
            ".ico" => FileType.Metadata,
            ".tbn" => FileType.Metadata,
            ".nfo" => FileType.Metadata,
            ".metathumb" => FileType.Metadata,
            ".txt" => FileType.Metadata,
            ".url" => FileType.Metadata,
            ".torrent" => FileType.Metadata,
            ".sfv" => FileType.Metadata,
            ".md5" => FileType.Metadata,
            ".xml" => FileType.Metadata,
            ".json" => FileType.Metadata,

            _ => FileType.Unknown
        };
    }
}

