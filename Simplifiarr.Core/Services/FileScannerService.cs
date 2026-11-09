using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class FileScannerService : IFileScannerService
{
    public Task<List<FileRecord>> ScanAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            return Task.FromResult(new List<FileRecord>());
        }

        var files = Directory
            .EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                var info = new FileInfo(file);

                return new FileRecord
                {
                    FullPath = info.FullName,
                    FileName = info.Name,
                    Extension = info.Extension,
                    FileType = GetFileType(info.Extension),
                    SizeBytes = info.Length,
                    LastModifiedUtc = info.LastWriteTimeUtc
                };
            })
            .ToList();

        return Task.FromResult(files);
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

