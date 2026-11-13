using Amberkeep.Core.FileSystem;

namespace Amberkeep.Tests.Infrastructure;

/// <summary>Real file system with hooks for simulating a full disk and observing writes.</summary>
internal sealed class TestFileSystem : IWritableFileSystem
{
    private readonly PhysicalFileSystem _inner = PhysicalFileSystem.Instance;

    /// <summary>Overrides the reported free space.</summary>
    public long? FreeSpace { get; set; }

    /// <summary>Called before every write to a newly created file.</summary>
    public Action? OnWrite { get; set; }

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public bool FileExists(string path) => _inner.FileExists(path);

    public string? GetVolumeFormat(string path) => _inner.GetVolumeFormat(path);

    public IReadOnlyList<FileSystemEntry> ListDirectory(string path) => _inner.ListDirectory(path);

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);

    public bool IsDirectoryEmpty(string path) => _inner.IsDirectoryEmpty(path);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public Stream CreateFile(string path)
    {
        var stream = _inner.CreateFile(path);
        return OnWrite is null ? stream : new CallbackStream(stream, OnWrite);
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        _inner.MoveFile(sourcePath, destinationPath, overwrite);

    public void MoveDirectory(string sourcePath, string destinationPath) => _inner.MoveDirectory(sourcePath, destinationPath);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void DeleteEmptyDirectory(string path) => _inner.DeleteEmptyDirectory(path);

    public void DeleteDirectoryTree(string path) => _inner.DeleteDirectoryTree(path);

    public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);

    public void SetAttributes(string path, FileAttributes attributes) => _inner.SetAttributes(path, attributes);

    public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) => _inner.SetLastWriteTimeUtc(path, lastWriteTimeUtc);

    public void CopyMetadata(string sourcePath, string destinationPath) => _inner.CopyMetadata(sourcePath, destinationPath);

    /// <summary>Report free space as unknown (e.g. a network share).</summary>
    public bool FreeSpaceUnknown { get; set; }

    public long? GetAvailableFreeSpace(string existingPath) =>
        FreeSpaceUnknown ? null : FreeSpace ?? _inner.GetAvailableFreeSpace(existingPath);

    private sealed class CallbackStream(Stream inner, Action onWrite) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            onWrite();
            inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal static class TreeSnapshot
{
    /// <summary>
    /// Every entry under <paramref name="root"/> with attributes, size and times.
    /// Refresh() reads metadata from the entry itself; values from a directory enumeration can be stale on NTFS.
    /// </summary>
    public static List<string> Take(string root) => new DirectoryInfo(root)
        .EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
        .Select(i =>
        {
            i.Refresh();
            return string.Join(
                '|',
                Path.GetRelativePath(root, i.FullName),
                i.Attributes,
                i is FileInfo f ? f.Length : -1,
                i.LastWriteTimeUtc.Ticks,
                i.CreationTimeUtc.Ticks);
        })
        .Order(StringComparer.Ordinal)
        .ToList();
}
