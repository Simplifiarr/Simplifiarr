using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class FfprobeMediaMetadataExtractor
    : IMediaMetadataExtractor
{
    // A healthy probe takes well under a second. A file on a sleeping or
    // failing disk must not be able to hang a whole scan.
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settingsService;

    public FfprobeMediaMetadataExtractor(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<MediaMetadata?> ExtractAsync(
        string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var settings =
                await _settingsService.GetSettingsAsync();

            FfprobeLocator.Configure(settings.FfprobePath);

            var ffprobe = FfprobeLocator.Find();

            if (ffprobe is null)
            {
                return null;
            }

            using var process =
                new Process
                {
                    StartInfo =
                        new ProcessStartInfo
                        {
                            FileName = ffprobe,
                            Arguments =
                                $"-v error " +
                                "-select_streams v:0 " +
                                "-show_entries stream=width,height,codec_name " +
                                "-of json " +
                                $"\"{filePath}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                };

            process.Start();

            using var timeout =
                new CancellationTokenSource(ProbeTimeout);

            string output;

            try
            {
                output =
                    await process.StandardOutput.ReadToEndAsync(timeout.Token);

                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);

                AppLog.Warn($"ffprobe took longer than {ProbeTimeout.TotalSeconds:N0}s and was stopped: '{filePath}'.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var document =
                JsonDocument.Parse(output);

            if (!document.RootElement.TryGetProperty(
                    "streams",
                    out var streams))
            {
                return null;
            }

            if (streams.GetArrayLength() == 0)
            {
                return null;
            }

            var stream = streams[0];

            var width =
                stream.TryGetProperty("width", out var widthProperty)
                    ? widthProperty.GetInt32()
                    : 0;

            var height =
                stream.TryGetProperty("height", out var heightProperty)
                    ? heightProperty.GetInt32()
                    : 0;

            var codec =
                stream.TryGetProperty("codec_name", out var codecProperty)
                    ? codecProperty.GetString() ?? string.Empty
                    : string.Empty;

            return new MediaMetadata
            {
                Width = width,
                Height = height,
                Codec = codec,
                Resolution =
                    height switch
                    {
                        >= 2000 => "2160P",
                        >= 1400 => "1440P",
                        >= 900 => "1080P",
                        >= 500 => "720P",
                        _ => "UNKNOWN"
                    }
            };
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not read video details from '{filePath}'.", exception);

            return null;
        }
    }
}
