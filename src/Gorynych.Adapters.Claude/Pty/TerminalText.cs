using System.Text;
using System.Text.RegularExpressions;

namespace Gorynych.Adapters.Claude.Pty;

/// <summary>Windows command-line quoting and terminal-output helpers.</summary>
public static partial class TerminalText
{
    /// <summary>Joins arguments into a command line that CommandLineToArgvW splits back unchanged.</summary>
    public static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\r', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Removes escape sequences, leaving roughly what a person would read on screen.</summary>
    public static string Plain(string raw)
    {
        var s = Osc().Replace(raw, "");
        s = Csi().Replace(s, " ");
        s = Other().Replace(s, "");
        return Spaces().Replace(s, " ");
    }

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)")] private static partial Regex Osc();
    [GeneratedRegex(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]")] private static partial Regex Csi();
    [GeneratedRegex(@"\x1b[()][0-9A-Za-z]|\x1b[=>78]")] private static partial Regex Other();
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex Spaces();
}

/// <summary>
/// Keeps the tail of a terminal's output as plain text and the bracketed-paste state,
/// so the adapter can recognise screens that need a keypress.
/// </summary>
public sealed class ScreenTail(int capacity = 32 * 1024)
{
    readonly Lock gate = new();
    readonly StringBuilder plain = new();
    readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    string carry = "";

    /// <summary>Set once the application has turned on bracketed paste (<c>ESC[?2004h</c>).</summary>
    public bool BracketedPaste { get; private set; }

    public event Action? Changed;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[decoder.GetCharCount(bytes, flush: false)];
        decoder.GetChars(bytes, chars, flush: false);
        var text = carry + new string(chars);
        // Keep an unfinished escape sequence for the next chunk.
        var esc = text.LastIndexOf('\x1b');
        if (esc >= 0 && text.Length - esc < 64 && !LooksComplete(text.AsSpan(esc)))
        {
            carry = text[esc..];
            text = text[..esc];
        }
        else carry = "";
        lock (gate)
        {
            if (text.Contains("\x1b[?2004h", StringComparison.Ordinal)) BracketedPaste = true;
            if (text.Contains("\x1b[?2004l", StringComparison.Ordinal)) BracketedPaste = false;
            plain.Append(TerminalText.Plain(text));
            if (plain.Length > capacity) plain.Remove(0, plain.Length - capacity);
        }
        Changed?.Invoke();
    }

    public string Text
    {
        get { lock (gate) return plain.ToString(); }
    }

    /// <summary>Forgets the text so far, so a later check only sees new output.</summary>
    public void Clear()
    {
        lock (gate) plain.Clear();
    }

    static bool LooksComplete(ReadOnlySpan<char> seq)
    {
        if (seq.Length < 2) return false;
        if (seq[1] == '[')
        {
            for (var i = 2; i < seq.Length; i++)
                if (seq[i] >= '@' && seq[i] <= '~') return true;
            return false;
        }
        if (seq[1] == ']') return seq.IndexOf('\x07') > 0 || seq.IndexOf("\x1b\\") > 0;
        return seq.Length >= 2;
    }
}
