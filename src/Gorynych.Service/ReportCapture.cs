using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Gorynych.Service;

/// <summary>Single-producer, process-recoverable last-message capture. Not a power-loss guarantee.</summary>
internal sealed class ReportCapture : IDisposable
{
    internal const int DefaultMaxBytes = 8 * 1024 * 1024;
    internal const int OutcomeBytes = 128, ErrorBytes = 512, ReasonBytes = 512;
    const int TerminalReserve = 4096;
    static readonly UTF8Encoding Utf8 = new(false, true);
    readonly string path;
    readonly FileStream lease;
    FileStream file;
    internal string Path => path;
    readonly int maxBytes;
    readonly long maxJournalBytes;
    internal int BodyLimit => maxBytes;
    internal long JournalByteLimit => maxJournalBytes;
    readonly Lock gate = new();
    readonly StringBuilder text = new();
    int utf8Bytes;
    bool pendingSurrogate, hasText, sealedCapture, failedWrite, disposed;
    string? incompleteReason, outcome, error;
    long journalBytes;
    internal int CompactionCount { get; private set; }
    internal Action? BeforeReplace { get; set; }
    internal Action? AfterReplace { get; set; }
    internal Action<byte[]> WriteFrame { get; set; }

    internal static long JournalLimit(int bodyBytes) => checked(2L * bodyBytes + 8192);
    internal ReportCapture(string path, int maxBytes = DefaultMaxBytes, long? maxJournalBytes = null)
        : this(path, maxBytes, maxJournalBytes, null) { }
    // The store acquires the per-capture lease before dropping its metadata lock.
    internal ReportCapture(string path, int maxBytes, FileStream ownedLease)
        : this(path, maxBytes, null, ownedLease) { }
    ReportCapture(string path, int maxBytes, long? journalLimit, FileStream? ownedLease)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.maxBytes = maxBytes;
        maxJournalBytes = journalLimit ?? JournalLimit(maxBytes);
        if (maxJournalBytes < 2L * maxBytes + 8192) throw new ArgumentOutOfRangeException(nameof(journalLimit));
        this.path = System.IO.Path.GetFullPath(path);
        lease = ownedLease ?? new FileStream(this.path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            File.Delete(this.path + ".compact");
            file = OpenData();
            WriteFrame = AppendFrame;
            try { Replay(); }
            catch { file.Dispose(); throw; }
        }
        catch { lease.Dispose(); throw; }
    }
    FileStream OpenData() => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    void Replay()
    {
        if (file.Length > maxJournalBytes) throw new InvalidDataException("Capture journal exceeds its limit");
        var header = new byte[5];
        while (file.Position < file.Length)
        {
            if (file.ReadAtLeast(header, header.Length, false) != header.Length) throw new InvalidDataException("Torn capture frame header");
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
            if (length < 0 || length > 2L * maxBytes + TerminalReserve || file.Length - file.Position < length + 32L)
                throw new InvalidDataException("Torn or oversized capture frame");
            var payload = new byte[length]; file.ReadExactly(payload);
            var hash = new byte[32]; file.ReadExactly(hash);
            var material = new byte[5 + length]; header.CopyTo(material, 0); payload.CopyTo(material, 5);
            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(material))) throw new InvalidDataException("Corrupt capture frame");
            Apply(header[0], payload);
        }
        journalBytes = file.Length;
    }
    internal void Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (gate)
        {
            EnsureOpen();
            // Once a chunk is lost, remain incomplete until an actual tool boundary resets it.
            if (incompleteReason is not null) return;
            try
            {
                if ((long)text.Length + chunk.Length > (long)maxBytes + 1) throw new InvalidOperationException("Capture exceeds its UTF-8 byte limit");
                var (size, pending) = Measure(chunk);
                var payload = Utf16(chunk);
                Persist(1, payload, true);
                try { text.Append(chunk); } catch { failedWrite = true; throw; }
                utf8Bytes = size; pendingSurrogate = pending; hasText = true;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
            {
                MarkIncomplete("Output incomplete: " + e.Message);
                throw;
            }
        }
    }
    internal void ToolCall()
    {
        lock (gate)
        {
            EnsureOpen();
            // Replace the entire spool under the per-capture lease; prior messages no longer consume budget.
            ReplaceWith([]);
            text.Clear(); utf8Bytes = 0; pendingSurrogate = false; hasText = false; incompleteReason = null;
        }
    }
    internal void Seal(string outcome, string? error = null)
    {
        lock (gate)
        {
            EnsureOpen();
            var reason = incompleteReason ?? (pendingSurrogate ? "Capture ends with an unmatched surrogate" : null);
            Persist(3, Terminal(outcome, error, reason), false);
            this.outcome = outcome; this.error = error; incompleteReason = reason; sealedCapture = true;
        }
    }
    internal void SealIncomplete(string reason, string outcome, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            EnsureOpen();
            // Preserve the first persisted loss reason; a later terminal error remains separate.
            _ = CheckedUtf8(reason, ReasonBytes, "incomplete reason");
            reason = incompleteReason ?? reason;
            Persist(3, Terminal(outcome, error, reason), false);
            this.outcome = outcome; this.error = error; incompleteReason = reason; sealedCapture = true;
        }
    }
    internal (string Text, bool HasText, bool Sealed, string? IncompleteReason, string? Outcome, string? Error) Snapshot()
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return (text.ToString(), hasText, sealedCapture, incompleteReason, outcome, error); }
    }
    void MarkIncomplete(string reason)
    {
        var bytes = CheckedUtf8(reason, ReasonBytes, "capture failure reason");
        Persist(4, bytes, true);
        incompleteReason = reason;
    }
    static byte[] CheckedUtf8(string value, int max, string field)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > max) throw new ArgumentException($"{field} exceeds {max} UTF-8 bytes");
        return bytes;
    }
    static byte[] Terminal(string outcome, string? error, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var o = CheckedUtf8(outcome, OutcomeBytes, "outcome");
        var e = error is null ? null : CheckedUtf8(error, ErrorBytes, "error");
        var r = reason is null ? null : CheckedUtf8(reason, ReasonBytes, "incomplete reason");
        var bytes = new byte[12 + o.Length + (e?.Length ?? 0) + (r?.Length ?? 0)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), o.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), e?.Length ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), r?.Length ?? -1);
        o.CopyTo(bytes, 12); e?.CopyTo(bytes, 12 + o.Length); r?.CopyTo(bytes, 12 + o.Length + (e?.Length ?? 0));
        return bytes;
    }
    void ReadTerminal(byte[] payload)
    {
        if (payload.Length < 12) throw new InvalidDataException("Invalid capture terminal");
        int ol = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0));
        int el = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        int rl = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8));
        if (ol is < 1 or > OutcomeBytes || el is < -1 or > ErrorBytes || rl is < -1 or > ReasonBytes ||
            payload.Length != 12L + ol + Math.Max(0, el) + Math.Max(0, rl)) throw new InvalidDataException("Invalid capture terminal lengths");
        try
        {
            outcome = Utf8.GetString(payload, 12, ol);
            error = el < 0 ? null : Utf8.GetString(payload, 12 + ol, el);
            var reason = rl < 0 ? null : Utf8.GetString(payload, 12 + ol + Math.Max(0, el), rl);
            if (string.IsNullOrWhiteSpace(outcome) || (reason is not null && string.IsNullOrWhiteSpace(reason)) ||
                (incompleteReason is not null && reason != incompleteReason) || (pendingSurrogate && reason is null))
                throw new InvalidDataException("Inconsistent capture terminal");
            incompleteReason = reason; sealedCapture = true;
        }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid terminal encoding", e); }
    }
    void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failedWrite) throw new IOException("Capture write outcome is uncertain; reopen and reconcile the journal");
        if (sealedCapture) throw new InvalidOperationException("Capture is sealed");
    }
    (int Bytes, bool Pending) Measure(string chunk)
    {
        long size = utf8Bytes; var pending = pendingSurrogate;
        foreach (var c in chunk)
        {
            if (pending) { if (!char.IsLowSurrogate(c)) throw new InvalidDataException("Capture contains an invalid Unicode scalar"); size += 4; pending = false; }
            else if (char.IsHighSurrogate(c)) pending = true;
            else if (char.IsLowSurrogate(c)) throw new InvalidDataException("Capture contains an invalid Unicode scalar");
            else size += c <= 0x7F ? 1 : c <= 0x7FF ? 2 : 3;
            if (size + (pending ? 4 : 0) > maxBytes) throw new InvalidOperationException("Capture exceeds its UTF-8 byte limit");
        }
        return ((int)size, pending);
    }
    static byte[] Utf16(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), value[i]);
        return bytes;
    }
    static byte[] Frame(byte kind, byte[] payload)
    {
        var frame = new byte[5 + payload.Length + 32]; frame[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length); payload.CopyTo(frame, 5);
        SHA256.HashData(frame.AsSpan(0, 5 + payload.Length)).CopyTo(frame.AsSpan(5 + payload.Length));
        return frame;
    }
    void Persist(byte kind, byte[] payload, bool reserveTerminal)
    {
        var frame = Frame(kind, payload);
        if (journalBytes + frame.Length > maxJournalBytes - (reserveTerminal ? TerminalReserve : 0)) Compact();
        if (journalBytes + frame.Length > maxJournalBytes - (reserveTerminal ? TerminalReserve : 0))
            throw new InvalidOperationException("Capture journal capacity exhausted");
        try { WriteFrame(frame); } catch { failedWrite = true; throw; }
        journalBytes += frame.Length;
    }
    void AppendFrame(byte[] frame) { file.Position = file.Length; file.Write(frame); file.Flush(); }
    void Compact()
    {
        using var memory = new MemoryStream();
        if (hasText)
        {
            var current = text.ToString();
            var prefix = pendingSurrogate ? current[..^1] : current;
            var encoded = Utf8.GetBytes(prefix);
            // A compacted checkpoint is UTF-8 plus an optional pending high surrogate.
            // It is at most B+3 bytes, leaving at least B bytes of journal headroom;
            // normal small-chunk streaming cannot repeatedly rewrite an almost-2B snapshot.
            var payload = new byte[3 + encoded.Length];
            payload[0] = pendingSurrogate ? (byte)1 : (byte)0;
            if (pendingSurrogate) BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), current[^1]);
            encoded.CopyTo(payload, 3);
            memory.Write(Frame(6, payload));
        }
        if (incompleteReason is not null) memory.Write(Frame(4, CheckedUtf8(incompleteReason, ReasonBytes, "capture failure reason")));
        ReplaceWith(memory.ToArray());
        CompactionCount++;
    }
    void ReplaceWith(byte[] bytes)
    {
        var stage = path + ".compact";
        try
        {
            using (var temp = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None)) { temp.Write(bytes); temp.Flush(); }
            BeforeReplace?.Invoke();
            file.Dispose();
            File.Move(stage, path, overwrite: true);
            AfterReplace?.Invoke();
            file = OpenData(); journalBytes = bytes.Length;
        }
        catch { failedWrite = true; throw; }
    }
    void Apply(byte kind, byte[] payload)
    {
        if (sealedCapture) throw new InvalidDataException("Capture has frames after its seal");
        switch (kind)
        {
            case 1:
                if (incompleteReason is not null || payload.Length % 2 != 0) throw new InvalidDataException("Invalid capture text frame");
                var chars = new char[payload.Length / 2];
                for (var i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 2));
                var chunk = new string(chars); var (size, pending) = Measure(chunk);
                text.Append(chunk); utf8Bytes = size; pendingSurrogate = pending; hasText = true; break;
            case 6:
                if (hasText || text.Length != 0 || incompleteReason is not null || payload.Length < 3 || payload[0] > 1)
                    throw new InvalidDataException("Invalid compact capture frame");
                string prefix;
                try { prefix = Utf8.GetString(payload, 3, payload.Length - 3); }
                catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid compact capture encoding", e); }
                var tail = (char)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1));
                if ((payload[0] == 1 && !char.IsHighSurrogate(tail)) || (payload[0] == 0 && tail != 0))
                    throw new InvalidDataException("Invalid compact capture surrogate");
                var compacted = payload[0] == 1 ? prefix + tail : prefix;
                var (compactSize, compactPending) = Measure(compacted);
                text.Append(compacted); utf8Bytes = compactSize; pendingSurrogate = compactPending; hasText = true;
                break;
            case 3: ReadTerminal(payload); break;
            case 4:
                try { incompleteReason = Utf8.GetString(payload); }
                catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid failure reason", e); }
                if (string.IsNullOrWhiteSpace(incompleteReason)) throw new InvalidDataException("Missing failure reason");
                break;
            default: throw new InvalidDataException("Unknown capture frame");
        }
    }
    public void Dispose() { lock (this) lock (gate) { if (disposed) return; disposed = true; file.Dispose(); lease.Dispose(); } }
}
