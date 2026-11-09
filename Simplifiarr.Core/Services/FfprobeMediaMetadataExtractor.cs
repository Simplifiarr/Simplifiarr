using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class FfprobeMediaMetadataExtractor
    : IMediaMetadataExtractor
{
    public async Task<MediaMetadata?> ExtractAsync(
        string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var process =
                new Process
                {
                    StartInfo =
                        new ProcessStartInfo
                        {
                            FileName = "ffprobe",
                            Arguments =
                                $"-v error " +
                                "-select_streams v:0 " +
                                "-show_entries stream=width,height,codec_name " +
                                "-of json " +
                                $"\"{filePath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                };

            process.Start();

            var output =
                await process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();

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
        catch
        {
            return null;
        }
    }
}