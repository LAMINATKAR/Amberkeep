using System.Buffers;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Core.Diff;

/// <summary>Streams two files and stops at the first difference.</summary>
internal static class ContentComparer
{
    private const int BufferSize = 1 << 20;

    public static bool AreEqual(IFileSystem fileSystem, string pathA, string pathB, CancellationToken cancellationToken)
    {
        using var streamA = fileSystem.OpenRead(pathA);
        using var streamB = fileSystem.OpenRead(pathB);
        var bufferA = ArrayPool<byte>.Shared.Rent(BufferSize);
        var bufferB = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readA = streamA.ReadAtLeast(bufferA.AsSpan(0, BufferSize), BufferSize, throwOnEndOfStream: false);
                var readB = streamB.ReadAtLeast(bufferB.AsSpan(0, BufferSize), BufferSize, throwOnEndOfStream: false);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }
}
