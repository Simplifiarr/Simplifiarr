namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Proves two files are byte-for-byte identical by reading both and
/// comparing every byte - no hashing, no sampling. Stops at the first
/// difference, so non-matching files are cheap and only genuinely identical
/// files are read end to end.
/// </summary>
public static class FileContentComparer
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<bool> AreIdenticalAsync(
        string pathA,
        string pathB,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);

            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
            {
                return false;
            }

            await using var streamA = OpenForCompare(pathA);
            await using var streamB = OpenForCompare(pathB);

            var bufferA = new byte[BufferSize];
            var bufferB = new byte[BufferSize];

            while (true)
            {
                var readA = await ReadFullAsync(streamA, bufferA, cancellationToken);
                var readB = await ReadFullAsync(streamB, bufferB, cancellationToken);

                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that can't be read can't be proven identical.
            return false;
        }
    }

    private static FileStream OpenForCompare(
        string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

    private static async Task<int> ReadFullAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read =
                await stream.ReadAsync(
                    buffer.AsMemory(total),
                    cancellationToken);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
