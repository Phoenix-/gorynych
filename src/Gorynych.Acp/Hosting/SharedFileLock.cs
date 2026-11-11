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

/// <summary>
/// A small shared file read whole, so that a store can tell whether another process changed it since
/// it last read it by what the file holds. Its write time and length cannot tell: two writes of the
/// same length within one tick of the clock the file system stamps them with (about 15 ms on Windows,
/// coarser on some file systems) leave both alike.
/// </summary>
public static class SharedFile
{
    /// <summary>The file's text, or null when it does not exist; other errors are thrown.</summary>
    public static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}
