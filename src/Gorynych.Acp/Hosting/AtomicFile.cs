namespace Gorynych.Acp.Hosting;

/// <summary>
/// Replaces a small file atomically: writes a temporary file next to it, then moves it over
/// the old one. On Windows the move fails while another process (an antivirus scanner, the
/// search indexer) has the target open without delete sharing, usually for a few
/// milliseconds, so the move is retried briefly before the error is thrown.
/// </summary>
public static class AtomicFile
{
    static readonly int[] RetryDelaysMs = [10, 25, 50, 100, 200];

    public static void WriteAllText(string path, string contents, System.Text.Encoding? encoding = null)
    {
        var tmp = path + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (encoding is null) File.WriteAllText(tmp, contents);
                else File.WriteAllText(tmp, contents, encoding);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }
}
