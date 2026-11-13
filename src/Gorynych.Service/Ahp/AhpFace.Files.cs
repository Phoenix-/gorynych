using System.Text;
using System.Text.Json;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Service.Ahp;

/// <summary>Step 2 of the AHP face (docs/ahp-face.md): folders and files, and the terminal it does not have.</summary>
internal sealed partial class AhpFace
{
    /// <summary>The one line a terminal of this host shows.</summary>
    internal const string NoShell = "This host has no shell: Gorynych runs agents here, not terminals.\r\n";

    /// <summary>How many terminals the face keeps; the oldest goes first.</summary>
    const int TerminalsKept = 32;

    /// <summary>The largest file <c>resourceRead</c> reads.</summary>
    const long ReadLimit = 16 * 1024 * 1024;

    sealed class FaceTerminal(string uri, TerminalState state, long baseSeq)
    {
        public string Uri { get; } = uri;
        public TerminalState State { get; } = state;
        public long BaseSeq { get; } = baseSeq;
    }

    // under gate
    readonly Dictionary<string, FaceTerminal> terminals = new(StringComparer.Ordinal);
    readonly List<string> terminalOrder = [];

    // ---------------------------------------------------------------- the terminal it does not have

    /// <summary>
    /// <c>createTerminal</c>: a terminal that never runs anything. VS Code opens one on the host for every session it shows
    /// and toasts an error when it cannot; this one answers, shows one line saying the host has no shell, and is
    /// <c>exited</c> from the start (no process ever ran, so it reports no exit code). The window keeps it as a tab
    /// with that line: it acts on a <c>terminal/exited</c> action, not on the state it subscribes to, and a later
    /// action would close the tab at once. It stays until <c>disposeTerminal</c>; the face keeps the newest 32.
    /// </summary>
    JsonElement? CreateTerminal(CreateTerminalParams p)
    {
        lock (gate)
        {
            if (Exists(p.Channel) || p.Channel.Length == 0) throw new AhpError(AhpErrorCodes.AlreadyExists, $"Channel {p.Channel} exists already");
            var state = new TerminalState
            {
                Title = p.Name is { Length: > 0 } name ? name : "Gorynych",
                Cwd = p.Cwd, Cols = p.Cols, Rows = p.Rows,
                Content = [new TerminalContentPart(new TerminalUnclassifiedPart { Type = "unclassified", Value = NoShell })],
                Lifecycle = new TerminalLifecycleState(new TerminalExitedLifecycleState { Status = TerminalLifecycleStatus.Exited }),
                Claim = p.Claim, SupportsCommandDetection = false, IsPty = false,
            };
            terminals[p.Channel] = new FaceTerminal(p.Channel, state, serverSeq);
            terminalOrder.Add(p.Channel);
            while (terminalOrder.Count > TerminalsKept)
            {
                terminals.Remove(terminalOrder[0]);
                terminalOrder.RemoveAt(0);
            }
            TerminalsChanged();
            return Null;
        }
    }

    /// <summary><c>disposeTerminal</c>: the terminal goes; there is no process to kill.</summary>
    JsonElement? DisposeTerminal(DisposeTerminalParams p)
    {
        lock (gate)
        {
            if (!terminals.Remove(p.Channel)) throw new AhpError(AhpErrorCodes.NotFound, $"No terminal {p.Channel} on this host");
            terminalOrder.Remove(p.Channel);
            TerminalsChanged();
            return Null;
        }
    }

    /// <summary>Under the gate: the root's catalog of terminals, as AHP has the host keep it.</summary>
    void TerminalsChanged()
    {
        var catalog = terminalOrder.Select(uri =>
        {
            var t = terminals[uri].State;
            return new TerminalInfo { Resource = uri, Title = t.Title, Claim = t.Claim, Lifecycle = t.Lifecycle };
        }).ToList();
        var action = new StateAction(new RootTerminalsChangedAction { Terminals = catalog });
        Broadcast(AhpNames.Root, action);
        Reducers.ApplyToRoot(root, action);
    }

    // ---------------------------------------------------------------- folders and files

    /// <summary>
    /// The local path of a <c>file:</c> URI a client names (<see cref="AhpNames.FilePath"/>). Another scheme names nothing
    /// this host has (<c>NotFound</c>); a <c>file:</c> URI it does not serve, another machine's above all, is refused.
    /// </summary>
    static string LocalPath(string uri) =>
        AhpNames.FilePath(uri) ?? throw (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new AhpError(AhpErrorCodes.PermissionDenied, $"{uri}: this host serves its own files only, by file:///<path> URIs")
            : new AhpError(AhpErrorCodes.NotFound, $"{uri}: this host serves file: URIs only"));

    /// <summary>A file system failure as AHP's error.</summary>
    static AhpError Failure(string uri, Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => new AhpError(AhpErrorCodes.NotFound, $"{uri}: not found"),
        UnauthorizedAccessException => new AhpError(AhpErrorCodes.PermissionDenied, $"{uri}: access denied"),
        _ => new AhpError(JsonRpcErrorCodes.InternalError, $"{uri}: {e.Message}"),
    };

    /// <summary><c>resourceList</c>: a folder's entries, folders first, each by name.</summary>
    static JsonElement? ResourceList(ResourceListParams p)
    {
        var path = LocalPath(p.Uri);
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists) throw new AhpError(AhpErrorCodes.NotFound, $"{p.Uri}: no such folder");
            var entries = folder.EnumerateFileSystemInfos()
                .Select(e => new DirectoryEntry { Name = e.Name, Type = e is DirectoryInfo ? "directory" : "file" })
                .OrderBy(e => e.Type == "directory" ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Json.SerializeToElement(new ResourceListResult { Entries = entries });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw Failure(p.Uri, e); }
    }

    /// <summary><c>resourceResolve</c>: what a URI is (a file, a folder, a link), its size and times; links followed unless asked not to.</summary>
    static JsonElement? ResourceResolve(ResourceResolveParams p)
    {
        var path = LocalPath(p.Uri);
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null) throw new AhpError(AhpErrorCodes.NotFound, $"{p.Uri}: not found");
            var uri = p.Uri;
            ResourceType type;
            if (info.LinkTarget is not null && p.FollowSymlinks == false) type = ResourceType.Symlink;
            else
            {
                if (info.LinkTarget is not null)
                {
                    info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
                    if (!info.Exists) throw new AhpError(AhpErrorCodes.NotFound, $"{p.Uri}: the link goes nowhere");
                    uri = AhpNames.FileUri(info.FullName) ?? uri;
                }
                type = info is DirectoryInfo ? ResourceType.Directory : ResourceType.File;
            }
            return Json.SerializeToElement(new ResourceResolveResult
            {
                Uri = uri, Type = type,
                Size = info is FileInfo file && type == ResourceType.File ? file.Length : null,
                Mtime = AhpNames.Time(info.LastWriteTimeUtc), Ctime = AhpNames.Time(info.CreationTimeUtc),
                ContentType = type == ResourceType.File ? ContentTypeOf(info.Name) : null,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw Failure(p.Uri, e); }
    }

    /// <summary><c>resourceRead</c>: a file's content, as UTF-8 text when it is text (and not asked for base64), else in base64.</summary>
    static JsonElement? ResourceRead(ResourceReadParams p)
    {
        var path = LocalPath(p.Uri);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new AhpError(AhpErrorCodes.NotFound, $"{p.Uri}: no such file");
            if (file.Length > ReadLimit) throw new AhpError(AhpErrorCodes.PermissionDenied, $"{p.Uri}: larger than this host reads ({ReadLimit / 1024 / 1024} MB)");
            var bytes = File.ReadAllBytes(path);
            string? text = null;
            if (p.Encoding != ContentEncoding.Base64)
                try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
                catch (DecoderFallbackException) { }
            return Json.SerializeToElement(new ResourceReadResult
            {
                Data = text ?? Convert.ToBase64String(bytes),
                Encoding = text is null ? ContentEncoding.Base64 : ContentEncoding.Utf8,
                ContentType = ContentTypeOf(file.Name),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw Failure(p.Uri, e); }
    }

    /// <summary><c>resourceMkdir</c>: a folder, with its parents (VS Code's folder picker makes one so).</summary>
    static JsonElement? ResourceMkdir(ResourceMkdirParams p)
    {
        var path = LocalPath(p.Uri);
        try
        {
            if (File.Exists(path)) throw new AhpError(AhpErrorCodes.AlreadyExists, $"{p.Uri}: a file is there");
            Directory.CreateDirectory(path);
            return Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw Failure(p.Uri, e); }
    }

    static string? ContentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".txt" or ".log" or ".cs" or ".ts" or ".js" or ".py" or ".rs" or ".go" or ".java" or ".c" or ".h" or ".cpp" or ".xml"
            or ".yaml" or ".yml" or ".toml" or ".css" or ".sh" or ".ps1" => "text/plain",
        _ => null,
    };
}
