using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class AssetIdentityService
    : IAssetIdentityService
{
    private readonly IFileScannerService _fileScanner;
    private readonly IReleaseParser _releaseParser;

    public AssetIdentityService(
        IFileScannerService fileScanner,
        IReleaseParser releaseParser)
    {
        _fileScanner = fileScanner;
        _releaseParser = releaseParser;
    }

    public async Task<AssetIdentityResult> AnalyzeAsync(
        ParsedRelease release,
        LibraryTarget target,
        string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return new AssetIdentityResult
            {
                IsRepresented = false,
                Reason = "Source file does not exist."
            };
        }

        if (string.IsNullOrWhiteSpace(target.TargetDirectory))
        {
            return new AssetIdentityResult
            {
                IsRepresented = false,
                Reason = "No target directory was resolved."
            };
        }

        if (!Directory.Exists(target.TargetDirectory))
        {
            return new AssetIdentityResult
            {
                IsRepresented = false,
                Reason = "Target directory does not exist."
            };
        }

        var sourceFile =
            new FileInfo(sourcePath);

        var files =
            await _fileScanner.ScanAsync(
                target.TargetDirectory);

        var videoFiles =
            files
                .Where(file => file.FileType == FileType.Video)
                .ToList();

        var matches =
            release.IsTv
                ? videoFiles
                    .Where(file => IsSameEpisode(file, release))
                    .ToList()
                : videoFiles
                    .Where(file =>
                        file.SizeBytes == sourceFile.Length)
                    .ToList();

        // The resolved target file path is deterministic (same title/year
        // always maps to the same filename), so a file already sitting
        // there means this asset is already represented - regardless of
        // whether the new source happens to be a different size/quality.
        // Without this, a re-download of a different quality would look
        // "unrepresented" by the size check above and get offered as a
        // promotion, only to fail at execution time because the target
        // path is already occupied.
        var targetFileAlreadyExists =
            target.FileExists;

        return new AssetIdentityResult
        {
            IsRepresented = matches.Any() || targetFileAlreadyExists,
            MatchingFileCount = matches.Count,
            MatchingFiles =
                matches
                    .Select(match => match.FullPath)
                    .ToList(),
            Reason =
                matches.Any()
                    ? "Matching video file(s) found."
                    : targetFileAlreadyExists
                        ? "A file already exists at the resolved library target path."
                        : "No matching video files found."
        };
    }

    private bool IsSameEpisode(
        FileRecord file,
        ParsedRelease release)
    {
        var parsed =
            _releaseParser.Parse(
                Path.GetFileNameWithoutExtension(
                    file.FileName));

        return parsed.IsTv &&
               parsed.Season == release.Season &&
               parsed.Episode == release.Episode;
    }
}
