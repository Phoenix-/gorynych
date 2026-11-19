using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service.Hosts;

// One durable stream per service data directory. A compacted or never-seen number at
// or below the watermark is stale/unknown, never evidence that native input was absent.
// The shared lock covers publication and compaction, never network I/O.
internal sealed class CompletionHostLedger(string path, int capacity = 64)
{
    internal enum Admission { Added, Retained, RetainedAborted, RetainedReleased, Stale, Full }
    const int MaxBytes = 2 * 1024 * 1024;
    internal Action<string, string> WriteFile { get; set; } = static (target, value) => AtomicFile.WriteAllText(target, value);

    JsonObject Read()
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Completion ledger exceeds its bound");
        var anchor = SharedFile.ReadText(path + ".anchor");
        var text = SharedFile.ReadText(path);
        if (!ValidStream(anchor) || text is null) throw new InvalidDataException("Completion ledger or its incarnation is unavailable");
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Invalid completion ledger");
        if (root["rows"] is not JsonObject rows || (long?)root["highWater"] is not { } high || high < 0 || rows.Count > capacity)
            throw new InvalidDataException("Invalid completion ledger header");
        var stream = (string?)root["stream"];
        if (!ValidStream(stream) || stream != anchor) throw new InvalidDataException("Invalid completion stream");
        var numbers = new HashSet<long>();
        foreach (var (attempt, value) in rows)
            if (!ValidStream(attempt) || value is not JsonObject row || (long?)row["number"] is not { } number ||
                number < 1 || number > high || !numbers.Add(number) || row["identity"] is not JsonObject ||
                (string?)row["state"] is not ("admitted" or "precommit_aborted" or "released") ||
                (bool?)row["forwardStarted"] is null ||
                (string?)row["state"] == "released" && ((long?)row["ackRevision"] is not { } ack || ack < 1 ||
                    (string?)row["outcome"] is not ("native_ended" or "native_failed" or "native_cancelled" or "not_sent")))
                throw new InvalidDataException("Invalid completion ledger row");
        return root;
    }

    static bool ValidStream(string? value) => value is not null && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();
    void Save(JsonObject root)
    {
        var text = root.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new IOException("Completion ledger byte limit reached");
        WriteFile(path, text);
    }

    // Called only for a freshly allocated mailbox stream, before any handoff is
    // possible. An anchor without its ledger is never silently recreated.
    internal void InitializeNewStream(string stream, Func<bool> mailboxStreamIsPristine)
    {
        if (!ValidStream(stream)) throw new ArgumentException("Invalid completion stream");
        using var held = SharedFileLock.Acquire(path);
        InitializeLocked(stream, mailboxStreamIsPristine);
    }

    void InitializeLocked(string stream, Func<bool> mailboxStreamIsPristine)
    {
        var anchor = SharedFile.ReadText(path + ".anchor"); var ledgerExists = File.Exists(path);
        if (anchor is not null || ledgerExists)
        {
            if (anchor == stream && !ledgerExists && mailboxStreamIsPristine())
            {
                Save(new JsonObject { ["stream"] = stream, ["highWater"] = 0L, ["rows"] = new JsonObject() });
                return;
            }
            var existing = Read();
            if ((string?)existing["stream"] != stream) throw new InvalidOperationException("Completion stream mismatch");
            return;
        }
        if (!mailboxStreamIsPristine()) throw new InvalidOperationException("Completion stream is not pristine");
        WriteFile(path + ".anchor", stream);
        Save(new JsonObject { ["stream"] = stream, ["highWater"] = 0L, ["rows"] = new JsonObject() });
    }

    // Lock order is shared ledger then mailbox. This publishes a claim only after
    // the exact stream's ledger exists; no network operation is held under either lock.
    internal CompletionReminderAdmission ClaimFromMailbox(MailboxStore mailbox, string source, string id, string owner,
        string assignee, string generation, long revision, string attempt, CompletionReminderBinding binding)
    {
        using var held = SharedFileLock.Acquire(path);
        var stream = mailbox.PrepareCompletionStream();
        InitializeLocked(stream, () => mailbox.CompletionStreamIsPristine(stream));
        var root = Read();
        if (root["rows"]!.AsObject().Count >= capacity)
        {
            var prior = mailbox.ReadCompletionRecovery(id, owner, assignee, generation, attempt);
            if (prior is not null && prior.Source == source && prior.OriginalRevision == revision && prior.Binding == binding)
                return new(false, new CompletionReminderClaim(attempt, revision, prior.Disposition, prior.Stream, prior.Number));
            return new(false, null);
        }
        var claim = mailbox.ClaimCompletionReminder(source, id, owner, assignee, generation, revision, attempt, binding);
        if (!claim.Acquired || claim.Claim is not { } created) return claim;
        var identity = Identity(source, id, owner, assignee, generation, revision, attempt, binding, created.Stream!, created.Number);
        if (AdmitLocked(root, created.Stream!, created.Number, attempt, identity) != Admission.Added)
            throw new InvalidOperationException("Durable completion claim could not be admitted");
        return claim;
    }

    // Read-only: an RPC cannot create an admitted row. Absence is always stale/unknown.
    internal Admission ReadAdmission(string stream, long number, string attempt, JsonObject identity)
    {
        if (!ValidStream(stream) || !ValidStream(attempt) || number < 1 || identity.Count == 0) return Admission.Stale;
        using var held = SharedFileLock.Acquire(path);
        var root = Read();
        if ((string?)root["stream"] != stream || root["rows"]?[attempt] is not JsonObject prior ||
            (long?)prior["number"] != number || !JsonNode.DeepEquals(prior["identity"], identity)) return Admission.Stale;
        return (string?)prior["state"] switch { "precommit_aborted" => Admission.RetainedAborted,
            "released" => Admission.RetainedReleased, _ => Admission.Retained };
    }

    internal static JsonObject Identity(string source, string id, string owner, string assignee, string generation,
        long revision, string attempt, CompletionReminderBinding binding, string stream, long number) => new()
    {
        ["source"] = source, ["assignmentId"] = id, ["ownerId"] = owner, ["sessionId"] = assignee,
        ["assignmentGeneration"] = generation, ["assignmentRevision"] = revision, ["attemptId"] = attempt,
        ["projectKey"] = binding.ProjectKey, ["groupId"] = binding.GroupId, ["groupInstance"] = binding.GroupInstance,
        ["promptId"] = binding.PromptId, ["nativeUuid"] = binding.NativeUuid, ["stream"] = binding.Stream,
        ["observationRevision"] = binding.ObservationRevision, ["digest"] = binding.Digest,
        ["hostId"] = binding.HostId, ["hostGeneration"] = binding.HostGeneration,
        ["producerPid"] = binding.ProducerPid, ["producerStart"] = binding.ProducerStart,
        ["ledgerStream"] = stream, ["ledgerNumber"] = number,
    };

    Admission AdmitLocked(JsonObject root, string stream, long number, string attempt, JsonObject identity)
    {
        var rows = root["rows"]!.AsObject();
        if ((string?)root["stream"] is { } existingStream && existingStream != stream) return Admission.Stale;
        if (rows[attempt] is JsonObject prior)
        {
            if ((long?)prior["number"] != number || !JsonNode.DeepEquals(prior["identity"], identity)) return Admission.Stale;
            return (string?)prior["state"] switch { "precommit_aborted" => Admission.RetainedAborted,
                "released" => Admission.RetainedReleased, _ => Admission.Retained };
        }
        if (number <= (long)root["highWater"]!) return Admission.Stale;
        if (rows.Count >= capacity) return Admission.Full;
        root["stream"] = stream; root["highWater"] = number;
        rows[attempt] = new JsonObject { ["number"] = number, ["identity"] = identity.DeepClone(),
            ["state"] = "admitted", ["forwardStarted"] = false };
        Save(root); return Admission.Added;
    }

    internal string RecordAbort(string attempt, JsonObject identity, JsonObject verdict)
    {
        using var held = SharedFileLock.Acquire(path); var root = Read();
        if (root["rows"]?[attempt] is not JsonObject row || !JsonNode.DeepEquals(row["identity"], identity) ||
            (bool?)row["forwardStarted"] != true || (string?)verdict["attemptId"] != attempt ||
            (string?)verdict["state"] is not ("confirmed_precommit" or "already_committed"))
            throw new InvalidOperationException("Abort identity mismatch");
        foreach (var (fact, expected) in new[] { ("sessionId", "sessionId"), ("promptId", "promptId"), ("witnessUuid", "nativeUuid"),
            ("stream", "stream"), ("observationRevision", "observationRevision"), ("digest", "digest") })
            if (verdict[fact] is null || !JsonNode.DeepEquals(verdict[fact], identity[expected])) throw new InvalidOperationException("Abort witness mismatch");
        var state = (string)verdict["state"]!;
        var revision = (long?)verdict["revision"];
        if (state == "confirmed_precommit" && revision is null or < 0) throw new InvalidOperationException("Missing abort revision");
        if ((string?)row["abortVerdict"] is { } prior)
        {
            if (prior != state || state == "confirmed_precommit" && (long?)row["abortRevision"] != revision)
                throw new InvalidOperationException("Conflicting abort verdict");
            return prior;
        }
        if (state == "confirmed_precommit" && row["receipt"] is JsonObject receipt &&
            ((bool?)receipt["committed"] == true || (bool?)receipt["taken"] == true || (bool?)receipt["writeCompleted"] == true || receipt["cycleResult"] is not null))
            throw new InvalidOperationException("Abort contradicts receipt");
        row["abortVerdict"] = state; row["abortOrigin"] = "adapter";
        if (state == "confirmed_precommit") row["abortRevision"] = revision;
        if (state == "confirmed_precommit") row["state"] = "precommit_aborted";
        Save(root); return state;
    }

    internal JsonObject? ReadAttempt(string attempt)
    {
        using var held = SharedFileLock.Acquire(path);
        return Read()["rows"]?[attempt]?.DeepClone().AsObject();
    }
    internal bool BeginForward(string attempt, JsonObject identity) =>
        BeginForward((string?)identity["ledgerStream"] ?? "", (long?)identity["ledgerNumber"] ?? -1, attempt, identity);
    internal string AbortBeforeForward(string attempt, JsonObject identity) =>
        AbortBeforeForward((string?)identity["ledgerStream"] ?? "", (long?)identity["ledgerNumber"] ?? -1, attempt, identity)
            ? "confirmed_precommit" : "needs_adapter";

    internal bool BeginForward(string stream, long number, string attempt, JsonObject identity)
    {
        using var held = SharedFileLock.Acquire(path); var root = Read();
        if ((string?)root["stream"] != stream || root["rows"]?[attempt] is not JsonObject row ||
            (long?)row["number"] != number || !JsonNode.DeepEquals(row["identity"], identity) ||
            (string?)row["state"] != "admitted" || (bool?)row["forwardStarted"] != false) return false;
        row["forwardStarted"] = true; Save(root); return true;
    }

    internal bool AbortBeforeForward(string stream, long number, string attempt, JsonObject identity)
    {
        using var held = SharedFileLock.Acquire(path); var root = Read();
        if ((string?)root["stream"] != stream || root["rows"]?[attempt] is not JsonObject row ||
            (long?)row["number"] != number || !JsonNode.DeepEquals(row["identity"], identity)) return false;
        if ((string?)row["state"] == "precommit_aborted" && (bool?)row["forwardStarted"] == false) return true;
        if ((string?)row["state"] != "admitted" || (bool?)row["forwardStarted"] != false) return false;
        // Persist the source of the ordering proof with the row. Consumers must
        // not infer a verdict merely from a missing row or a transport failure.
        row["state"] = "precommit_aborted";
        row["abortVerdict"] = "confirmed_precommit";
        row["abortOrigin"] = "host_before_forward";
        Save(root); return true;
    }

    internal JsonObject Record(string attempt, JsonObject identity, JsonObject receipt)
    {
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var all = root["rows"]!.AsObject();
        var row = all[attempt] as JsonObject ?? throw new InvalidOperationException("No admitted attempt");
        if (!JsonNode.DeepEquals(row["identity"], identity)) throw new InvalidOperationException("Attempt identity conflict");
        var revision = ValidateReceipt(attempt, identity, receipt);
        if (row["receipt"] is JsonObject old && old["revision"] is JsonValue oldValue && oldValue.TryGetValue<long>(out var oldRevision))
        {
            if (revision < oldRevision) return (JsonObject)row.DeepClone();
            if (revision == oldRevision)
            {
                if (!JsonNode.DeepEquals(old, receipt)) throw new InvalidDataException("Conflicting receipt revision");
                return (JsonObject)row.DeepClone();
            }
            foreach (var field in new[] { "committed", "noInput", "writeCompleted", "taken", "nativeCancelled", "cancelRequested", "uncertain", "contradiction", "cancelUnresolved" })
                if ((bool?)old[field] == true && (bool?)receipt[field] != true) throw new InvalidDataException("Receipt fact regressed");
            foreach (var field in new[] { "cycleResult", "nativeStop", "nativeSubtype", "nativeTerminal" })
                if (old[field] is not null && !JsonNode.DeepEquals(old[field], receipt[field])) throw new InvalidDataException("Receipt result changed");
        }
        row["receipt"] = receipt.DeepClone();
        // State is evidence only. No transport response grants permission to resend.
        if ((string?)row["state"] != "admitted" || (bool?)row["forwardStarted"] != true)
            throw new InvalidOperationException("Receipt does not follow a forwarded attempt");
        Save(root);
        return (JsonObject)row.DeepClone();
    }
    static long ValidateReceipt(string attempt, JsonObject identity, JsonObject receipt)
    {
        string[] fields = ["state", "attemptId", "nativeUuid", "sessionId", "promptId", "witnessUuid", "stream", "observationRevision", "digest",
            "revision", "committed", "noInput", "writeCompleted", "taken", "cycleResult", "nativeCancelled", "cancelRequested", "uncertain",
            "contradiction", "nativeStop", "nativeSubtype", "nativeTerminal", "cancelObservers", "cancelUnresolved"];
        if (receipt.Count != fields.Length || fields.Any(field => !receipt.ContainsKey(field))) throw new InvalidDataException("Invalid receipt shape");
        if (receipt.Count != 24 || (string?)receipt["state"] != "receipt" || (string?)receipt["attemptId"] != attempt ||
            (string?)receipt["nativeUuid"] != Guid.ParseExact(attempt, "N").ToString("D"))
            throw new InvalidDataException("Receipt identity or shape mismatch");
        foreach (var (fact, expected) in new[] { ("sessionId", "sessionId"), ("promptId", "promptId"), ("witnessUuid", "nativeUuid"),
                     ("stream", "stream"), ("observationRevision", "observationRevision"), ("digest", "digest") })
            if (!JsonNode.DeepEquals(receipt[fact], identity[expected]) || receipt[fact] is null)
                throw new InvalidDataException("Receipt witness mismatch");
        foreach (var field in new[] { "committed", "noInput", "writeCompleted", "taken", "nativeCancelled", "cancelRequested", "uncertain", "contradiction", "cancelUnresolved" })
            if (receipt[field] is not JsonValue value || !value.TryGetValue<bool>(out _)) throw new InvalidDataException("Invalid receipt fact");
        if (receipt["cancelObservers"] is not JsonValue observers || !observers.TryGetValue<int>(out var count) || count < 0 || count > 1024)
            throw new InvalidDataException("Invalid observer count");
        foreach (var field in new[] { "cycleResult", "nativeStop", "nativeSubtype", "nativeTerminal" })
            if (receipt[field] is not null && (receipt[field] is not JsonValue text || !text.TryGetValue<string>(out var value) || value.Length > 160))
                throw new InvalidDataException("Invalid native fact");
        if (receipt["revision"] is not JsonValue revisionValue || !revisionValue.TryGetValue<long>(out var revision) || revision < 0)
            throw new InvalidDataException("Invalid receipt revision");
        return revision;
    }

    static string? EligibleOutcome(JsonObject? receipt)
    {
        if (receipt is null || (bool?)receipt["uncertain"] != false || (bool?)receipt["contradiction"] != false ||
            (bool?)receipt["cancelUnresolved"] != false || (int?)receipt["cancelObservers"] != 0 || (bool?)receipt["nativeCancelled"] != false) return null;
        if ((bool?)receipt["noInput"] == true && (bool?)receipt["committed"] == false && (bool?)receipt["writeCompleted"] == false &&
            (bool?)receipt["taken"] == false && (bool?)receipt["cancelRequested"] == false && receipt["cycleResult"] is null &&
            receipt["nativeStop"] is null && receipt["nativeSubtype"] is null && receipt["nativeTerminal"] is null) return "not_sent";
        if ((bool?)receipt["noInput"] != false || (bool?)receipt["committed"] != true || (bool?)receipt["writeCompleted"] != true || (bool?)receipt["taken"] != true) return null;
        return (string?)receipt["cycleResult"] switch { "ended" => "native_ended", "failed" when (bool?)receipt["cancelRequested"] == false => "native_failed", _ => null };
    }

    static bool AckCompatible(JsonObject row, string outcome) =>
        !((string?)row["abortVerdict"] == "confirmed_precommit" && outcome != "not_sent") &&
        !((string?)row["abortVerdict"] == "already_committed" && outcome == "not_sent");

    internal JsonObject PrepareAck(string attempt, JsonObject identity, long revision)
    {
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var all = root["rows"]!.AsObject(); var row = all[attempt] as JsonObject ?? throw new InvalidOperationException("Unknown attempt");
        if (!JsonNode.DeepEquals(row["identity"], identity) || row["receipt"] is not JsonObject receipt ||
            (long?)receipt["revision"] != revision || EligibleOutcome(receipt) is not { } outcome || !AckCompatible(row, outcome))
            throw new InvalidOperationException("Receipt is not stable at this revision");
        if ((long?)row["ackRevision"] == revision) return (JsonObject)row.DeepClone();
        if ((long?)row["preparedRevision"] == revision && (string?)row["preparedOutcome"] == outcome) return (JsonObject)row.DeepClone();
        row["preparedRevision"] = revision; row["preparedOutcome"] = outcome;
        Save(root);
        return (JsonObject)row.DeepClone();
    }

    internal JsonObject RecordAck(string attempt, JsonObject identity, long revision, JsonObject acknowledgement)
    {
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var all = root["rows"]!.AsObject(); var row = all[attempt] as JsonObject ?? throw new InvalidOperationException("Unknown attempt");
        if (!JsonNode.DeepEquals(row["identity"], identity) || row["receipt"] is not JsonObject receipt ||
            (long?)receipt["revision"] != revision || (long?)row["preparedRevision"] != revision ||
            EligibleOutcome(receipt) != (string?)row["preparedOutcome"] || EligibleOutcome(receipt) is not { } outcome || !AckCompatible(row, outcome) ||
            (string?)acknowledgement["state"] != "acknowledged" || (string?)acknowledgement["attemptId"] != attempt ||
            (long?)acknowledgement["revision"] != revision || (string?)acknowledgement["nativeUuid"] != Guid.ParseExact(attempt, "N").ToString("D"))
            throw new InvalidOperationException("ACK does not match the prepared receipt");
        foreach (var (fact, expected) in new[] { ("sessionId", "sessionId"), ("promptId", "promptId"), ("witnessUuid", "nativeUuid"),
                     ("stream", "stream"), ("observationRevision", "observationRevision"), ("digest", "digest") })
            if (acknowledgement[fact] is null || !JsonNode.DeepEquals(acknowledgement[fact], identity[expected]))
                throw new InvalidOperationException("ACK witness mismatch");
        if ((long?)row["ackRevision"] == revision) return (JsonObject)row.DeepClone();
        row["ackRevision"] = revision; row["state"] = "released"; row["outcome"] = (string?)row["preparedOutcome"];
        Save(root);
        return (JsonObject)row.DeepClone();
    }


    internal JsonObject PrepareRetire(string attempt, JsonObject identity, long revision)
    {
        using var held = SharedFileLock.Acquire(path);
        var row = Read()["rows"]?[attempt] as JsonObject ?? throw new InvalidOperationException("Unknown attempt");
        var eligible = (string?)row["state"] == "released" && (long?)row["ackRevision"] == revision ||
            (string?)row["state"] == "precommit_aborted" && (string?)row["abortOrigin"] == "adapter" &&
            (string?)row["abortVerdict"] == "confirmed_precommit" && (long?)row["abortRevision"] == revision;
        if (!JsonNode.DeepEquals(row["identity"], identity) || !eligible)
            throw new InvalidOperationException("Retire requires a durable exact ACK or adapter abort");
        return (JsonObject)row.DeepClone();
    }

    internal JsonObject RecordRetire(string attempt, JsonObject identity, long revision, JsonObject result)
    {
        using var held = SharedFileLock.Acquire(path); var root = Read();
        var row = root["rows"]?[attempt] as JsonObject ?? throw new InvalidOperationException("Unknown attempt");
        var eligible = (string?)row["state"] == "released" && (long?)row["ackRevision"] == revision ||
            (string?)row["state"] == "precommit_aborted" && (string?)row["abortOrigin"] == "adapter" &&
            (string?)row["abortVerdict"] == "confirmed_precommit" && (long?)row["abortRevision"] == revision;
        if (!JsonNode.DeepEquals(row["identity"], identity) || !eligible ||
            (string?)result["state"] != "retired" || (string?)result["attemptId"] != attempt || (long?)result["revision"] != revision)
            throw new InvalidOperationException("Retire does not match durable ACK");
        foreach (var (fact, expected) in new[] { ("sessionId", "sessionId"), ("promptId", "promptId"), ("witnessUuid", "nativeUuid"),
                     ("stream", "stream"), ("observationRevision", "observationRevision"), ("digest", "digest") })
            if (result[fact] is null || !JsonNode.DeepEquals(result[fact], identity[expected])) throw new InvalidOperationException("Retire witness mismatch");
        if ((long?)row["retiredRevision"] != revision) { row["retiredRevision"] = revision; Save(root); }
        return (JsonObject)row.DeepClone();
    }

    // Caller has already established the exact producer exit. The ledger is reread
    // under the cross-process lock before the mailbox COW; no network is held here.
    internal string RecoverAfterProducerExit(CompletionReminderRecovery saved, MailboxStore mailbox)
    {
        var identity = Identity(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation,
            saved.OriginalRevision, saved.AttemptId, saved.Binding, saved.Stream, saved.Number);
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var rows = root["rows"]!.AsObject();
        if ((string?)root["stream"] != saved.Stream || saved.Number < 1 ||
            mailbox.ReadCompletionRecovery(saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation, saved.AttemptId) != saved)
            return "unresolved";
        var row = rows[saved.AttemptId] as JsonObject;
        if (row is not null && ((long?)row["number"] != saved.Number || !JsonNode.DeepEquals(row["identity"], identity))) return "unresolved";
        if (row is null && rows.Any(pair => (long?)pair.Value?["number"] == saved.Number)) return "unresolved";
        if (row is null && saved.Disposition is "not_sent" or "native_ended" or "native_failed" or "native_cancelled" or "unknown_closed")
            return (long)root["highWater"]! >= saved.Number ? saved.Disposition : "unresolved";
        string? outcome = null;
        if (row is not null)
        {
            if ((string?)row["state"] == "released" && row["receipt"] is JsonObject receipt &&
                (long?)row["ackRevision"] is { } ack && (long?)row["preparedRevision"] == ack &&
                ValidateReceipt(saved.AttemptId, identity, receipt) == ack && EligibleOutcome(receipt) is { } eligible &&
                eligible == (string?)row["outcome"] && eligible == (string?)row["preparedOutcome"] && AckCompatible(row, eligible)) outcome = eligible;
            else if ((string?)row["state"] == "precommit_aborted" && (string?)row["abortVerdict"] == "confirmed_precommit" &&
                ((string?)row["abortOrigin"] == "host_before_forward" && (bool?)row["forwardStarted"] == false && row["receipt"] is null ||
                 (string?)row["abortOrigin"] == "adapter" && (bool?)row["forwardStarted"] == true && (long?)row["abortRevision"] is >= 0 &&
                 (row["receipt"] is not JsonObject facts || (bool?)facts["committed"] != true && (bool?)facts["taken"] != true &&
                    (bool?)facts["writeCompleted"] != true && facts["cycleResult"] is null))) outcome = "not_sent";
        }
        bool committed;
        if (outcome is not null)
        {
            CompletionReminderSettlement? settlement = outcome switch { "native_ended" => CompletionReminderSettlement.NativeEnded,
                "native_failed" => CompletionReminderSettlement.NativeFailed, "native_cancelled" => CompletionReminderSettlement.NativeCancelled,
                "not_sent" => CompletionReminderSettlement.ProvenNotSent, _ => null };
            committed = settlement is { } known && mailbox.SettleCompletionReminder(saved.Source, saved.AssignmentId, saved.Owner,
                saved.Assignee, saved.Generation, saved.OriginalRevision, saved.AttemptId, known);
        }
        else committed = mailbox.CloseCompletionReminderUnknown(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee,
            saved.Generation, saved.OriginalRevision, saved.AttemptId);
        if (!committed) return "unresolved";
        rows.Remove(saved.AttemptId);
        if ((long)root["highWater"]! < saved.Number) root["highWater"] = saved.Number;
        Save(root);
        return outcome ?? "unknown_closed";
    }

    // Called after exact original producer exit has been independently established.
    // The callback rechecks the exact binding and persists unknown_closed plus the
    // owner receipt. A released ACK or a precommit proof must be reconciled instead.
    internal bool CompactUnknownAfterProducerExit(string stream, long number, string attempt, JsonObject identity, Func<bool> closeUnknown)
    {
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var rows = root["rows"]!.AsObject();
        if ((string?)root["stream"] != stream || rows[attempt] is not JsonObject row ||
            (long?)row["number"] != number || !JsonNode.DeepEquals(row["identity"], identity) ||
            (string?)row["state"] != "admitted") return false;
        if (!closeUnknown()) return false;
        rows.Remove(attempt); Save(root); return true;
    }

    // The service-owned callback must durably settle the exact consumer before it
    // returns true. Its mailbox COW runs under this lock; it must not perform RPC.
    // A failed callback retains the row. An ambiguous Save retains the watermark.
    internal bool CompactWithDurableConsumer(string stream, long number, string attempt, JsonObject identity, Func<string, bool> settleConsumer)
    {
        using var held = SharedFileLock.Acquire(path);
        var root = Read(); var rows = root["rows"]!.AsObject();
        if ((string?)root["stream"] != stream || rows[attempt] is not JsonObject row ||
            (long?)row["number"] != number || !JsonNode.DeepEquals(row["identity"], identity) ||
            (string?)row["state"] is not ("precommit_aborted" or "released") ||
            (string?)row["state"] == "released" && ((long?)row["retiredRevision"] is not { } retired || retired != (long?)row["ackRevision"]) ||
            (string?)row["state"] == "precommit_aborted" && (string?)row["abortOrigin"] == "adapter" &&
                ((long?)row["retiredRevision"] is not { } abortedRetired || abortedRetired != (long?)row["abortRevision"])) return false;
        var outcome = (string?)row["state"] == "precommit_aborted" ? "not_sent" : (string)row["outcome"]!;
        if (!settleConsumer(outcome)) return false;
        rows.Remove(attempt); Save(root); return true;
    }
}
