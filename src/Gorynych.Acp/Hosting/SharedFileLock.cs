namespace Gorynych.Acp.Hosting;

/// <summary>
/// An exclusive lock across processes (and threads) for a small shared file, held on
/// <c>&lt;path&gt;.lock</c> opened with no sharing: the OS refuses the second opener on Windows and
/// on Unix alike. The lock file is never deleted, since deleting it races with other openers.
/// </summary>
public sealed class SharedFileLock : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    readonly FileStream stream;

    SharedFileLock(FileStream stream) => this.stream = stream;

    /// <summary>Waits up to five seconds for the lock on <paramref name="path"/>, else throws <see cref="IOException"/>.</summary>
    public static SharedFileLock Acquire(string path)
    {
        var lockPath = path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                return new SharedFileLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new IOException($"Could not lock {lockPath} within {Timeout.TotalSeconds:0} seconds; another process holds it: {e.Message}", e);
                Thread.Sleep(5);
            }
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>A file's last write time (UTC) and length: a cheap check that another process changed it.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    /// <summary>The stamp of <paramref name="path"/>, or null when the file does not exist.</summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
