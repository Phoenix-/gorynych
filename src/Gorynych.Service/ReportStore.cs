using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

// Only service/host code constructs this value. It must not be built from untrusted client metadata.
internal enum ReportCause { Unknown, Spawn, User, Delegation, Peer, Reply, Native }
internal sealed record ReportIdentity(string TurnKey, string Child, string Parent, ReportCause Cause,
    string? PromptId = null, string? AssignmentId = null, string? MessageId = null,
    string? HostId = null, string? HostGeneration = null, int? HostProcessId = null, string? HostProcessStart = null,
    bool LegacyCapture = false, string? AssignmentGeneration = null,
    string? ReplyWaitId = null, long? ReplyWaitSequence = null, long? ReplyMessageSequence = null);
internal sealed record ReportDeliveryClaim(string AttemptId, string Parent, string MessageId, string PromptId,
    string Route, string? HostId, string? HostGeneration, bool DispatchStarted = false,
    bool? ReceiptAckPending = null, bool? ReceiptConflict = null, int? HostProcessId = null, string? HostProcessStart = null,
    string? ReceiptReleaseReason = null);
internal sealed record ReportReceipt(string ReportId, string Status, bool Created = false);
internal sealed record ReportPage(string ReportId, ReportIdentity Identity, string Text, string? NextCursor, string Status,
    string? DispositionReason, string Outcome, string? Error, bool HasText, string? IncompleteReason, long TotalBytes,
    string? CaptureConfidenceReason = null, ReportDeliveryClaim? Delivery = null);
internal sealed record ReportInfo(string ReportId, ReportIdentity Identity, string Status, string? DispositionReason, bool ProducerAck,
    string? ProducerEvidence = null, string? ProducerFailure = null, string? ProducerOutcome = null, string? ProducerError = null,
    bool EndJournaled = false, string? Outcome = null, string? Error = null, bool? HasText = null,
    string? IncompleteReason = null, string? CaptureConfidenceReason = null, long? TotalBytes = null,
    ReportDeliveryClaim? Delivery = null);
internal sealed record ReportListPage(IReadOnlyList<ReportInfo> Items, string? NextCursor);
internal sealed record ReportPreview(string Text, bool Truncated, bool HasText, string? IncompleteReason);

// Shared by supporting hosts and legacy-terminal import. Never replace a known stop with success.
internal static class ReportTerminalOutcome
{
    internal static string From(JsonNode? result, bool failed)
    {
        if (failed) return "failed";
        var stop = result?["stopReason"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (stop is null or "end_turn") return "ended";
        if (stop == "cancelled") return "cancelled";
        if (stop.Length > ReportCapture.OutcomeBytes) return "stopped: [reason truncated]";
        var outcome = "stopped: " + stop;
        return Encoding.UTF8.GetByteCount(outcome) <= ReportCapture.OutcomeBytes ? outcome : "stopped: [reason truncated]";
    }
}

/// <summary>
/// Bounded report metadata and immutable bodies. The manifest is the publication boundary for
/// a result and its outbox entry. A shared file lock protects metadata across processes; capture
/// producers write their own journal independently. File flush/rename protects process restarts,
/// not arbitrary power loss. Admission uses conservative per-capture reservation.
/// </summary>
internal sealed partial class ReportStore
{
    internal const int DefaultBodyLimit = 8 * 1024 * 1024;
    internal const long DefaultTotalLimit = 512L * 1024 * 1024;
    internal const int DefaultRecordLimit = 5000;
    internal const int DefaultPinnedLimit = 1000;
    static readonly UTF8Encoding Utf8 = new(false, true);
    readonly string directory;
    readonly string manifest;
    readonly int bodyLimit, recordLimit, pinnedLimit;
    readonly long totalLimit;
    readonly Lock gate = new();
    internal Func<DateTimeOffset> Now { get; set; } = static () => DateTimeOffset.UtcNow;
    internal Action<string, string> WriteBodyStage { get; set; } = static (path, text) => File.WriteAllText(path, text, Utf8);
    internal Action<string, string> WriteManifest { get; set; } = static (path, json) => AtomicFile.WriteAllText(path, json);

    internal ReportStore(string directory, int bodyLimit = DefaultBodyLimit, long totalLimit = DefaultTotalLimit,
        int recordLimit = DefaultRecordLimit, int pinnedLimit = DefaultPinnedLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bodyLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pinnedLimit);
        this.directory = Path.GetFullPath(directory);
        this.bodyLimit = bodyLimit; this.totalLimit = totalLimit; this.recordLimit = recordLimit; this.pinnedLimit = pinnedLimit;
        Directory.CreateDirectory(this.directory);
        manifest = Path.Combine(this.directory, "reports.json");
    }

    static bool Pinned(JsonObject item) => (string?)item["status"] is not ("completed" or "failed" or "retained") || (bool?)item["producerAck"] != true ||
        ((string?)item["delivery"]?["route"] == "note" && item["delivery"]?["settledReceiptRevision"] is not null &&
         (((long?)item["delivery"]?["acknowledgedReceiptRevision"] != (long?)item["delivery"]?["settledReceiptRevision"] &&
           (long?)item["delivery"]?["hostExitReleasedRevision"] != (long?)item["delivery"]?["settledReceiptRevision"] &&
           (long?)item["delivery"]?["ledgerAbsentReleasedRevision"] != (long?)item["delivery"]?["settledReceiptRevision"]) ||
          (long?)item["delivery"]?["nativeReceipt"]?["revision"] != (long?)item["delivery"]?["settledReceiptRevision"]));
    static void ValidateIdentity(ReportIdentity i)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(i.TurnKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(i.Child);
        ArgumentException.ThrowIfNullOrWhiteSpace(i.Parent);
        if (!Enum.IsDefined(i.Cause)) throw new ArgumentException("Invalid report cause");
        if ((i.ReplyWaitId is not null || i.ReplyWaitSequence is not null || i.ReplyMessageSequence is not null) &&
            (i.Cause != ReportCause.Reply || string.IsNullOrWhiteSpace(i.AssignmentId) ||
             string.IsNullOrWhiteSpace(i.AssignmentGeneration) || string.IsNullOrWhiteSpace(i.MessageId) ||
             string.IsNullOrWhiteSpace(i.ReplyWaitId) || i.ReplyWaitSequence is not > 0 || i.ReplyMessageSequence is not > 0))
            throw new ArgumentException("Invalid correlated reply report identity");
        if (new[] { i.TurnKey, i.Child, i.Parent, i.PromptId, i.AssignmentId, i.MessageId, i.HostId, i.HostGeneration, i.HostProcessStart, i.AssignmentGeneration, i.ReplyWaitId }.Any(s => s?.Length > 512))
            throw new ArgumentException("Report identity field exceeds 512 characters");
    }
    static JsonObject Identity(ReportIdentity i)
    {
        var value = new JsonObject { ["turnKey"] = i.TurnKey, ["child"] = i.Child,
            ["parent"] = i.Parent, ["cause"] = i.Cause.ToString().ToLowerInvariant(), ["promptId"] = i.PromptId,
            ["assignmentId"] = i.AssignmentId, ["messageId"] = i.MessageId };
        // Omit new optional properties for compatibility with pre-B3 identity equality.
        if (i.HostId is not null) value["hostId"] = i.HostId;
        if (i.HostGeneration is not null) value["hostGeneration"] = i.HostGeneration;
        if (i.HostProcessId is not null) value["hostProcessId"] = i.HostProcessId;
        if (i.HostProcessStart is not null) value["hostProcessStart"] = i.HostProcessStart;
        if (i.LegacyCapture) value["legacyCapture"] = true;
        if (i.AssignmentGeneration is not null) value["assignmentGeneration"] = i.AssignmentGeneration;
        if (i.ReplyWaitId is not null) value["replyWaitId"] = i.ReplyWaitId;
        if (i.ReplyWaitSequence is not null) value["replyWaitSequence"] = i.ReplyWaitSequence;
        if (i.ReplyMessageSequence is not null) value["replyMessageSequence"] = i.ReplyMessageSequence;
        return value;
    }
    static ReportIdentity IdentityFrom(JsonNode? n)
    {
        if (!Enum.TryParse<ReportCause>((string?)n?["cause"], true, out var cause) || !Enum.IsDefined(cause))
            throw new InvalidDataException("Invalid persisted report cause");
        return new((string)n!["turnKey"]!, (string)n["child"]!, (string)n["parent"]!, cause,
            (string?)n["promptId"], (string?)n["assignmentId"], (string?)n["messageId"],
            (string?)n["hostId"], (string?)n["hostGeneration"], (int?)n["hostProcessId"], (string?)n["hostProcessStart"], (bool?)n["legacyCapture"] == true, (string?)n["assignmentGeneration"],
            (string?)n["replyWaitId"], (long?)n["replyWaitSequence"], (long?)n["replyMessageSequence"]);
    }
    static ReportInfo Info(JsonObject item) => new((string)item["id"]!, IdentityFrom(item["identity"]),
        (string)item["status"]!, (string?)item["reason"], (bool?)item["producerAck"] == true,
        (string?)item["producerEvidence"], (string?)item["producerFailure"], (string?)item["producerOutcome"], (string?)item["producerError"],
        (bool?)item["endJournaled"] == true, (string?)item["outcome"], (string?)item["error"], (bool?)item["hasText"],
        (string?)item["incompleteReason"], (string?)item["captureConfidenceReason"], (long?)item["bodyBytes"], DeliveryFrom(item["delivery"], (string)item["identity"]!["parent"]!));
    static ReportDeliveryClaim? DeliveryFrom(JsonNode? value, string parent) => value is null ? null : new(
        (string)value["attemptId"]!, parent, (string)value["messageId"]!, (string)value["promptId"]!,
        (string)value["route"]!, (string?)value["hostId"], (string?)value["hostGeneration"], (bool?)value["dispatchStarted"] == true,
        (string?)value["route"] == "note" && value["settledReceiptRevision"] is not null ?
            ((long?)value["settledReceiptRevision"] != (long?)value["acknowledgedReceiptRevision"] &&
             (long?)value["settledReceiptRevision"] != (long?)value["hostExitReleasedRevision"] &&
             (long?)value["settledReceiptRevision"] != (long?)value["ledgerAbsentReleasedRevision"]) ||
            (long?)value["settledReceiptRevision"] != (long?)value["nativeReceipt"]?["revision"] : null,
        (string?)value["route"] == "note" ? (bool?)value["nativeReceipt"]?["contradiction"] == true ||
            (bool?)value["nativeReceipt"]?["cancelReceiptPending"] == true : null,
        (int?)value["hostProcessId"], (string?)value["hostProcessStart"],
        (long?)value["hostExitReleasedRevision"] == (long?)value["settledReceiptRevision"] && value["hostExitReleasedRevision"] is not null ? "confirmed_host_exit" :
        (long?)value["ledgerAbsentReleasedRevision"] == (long?)value["settledReceiptRevision"] && value["ledgerAbsentReleasedRevision"] is not null ? "ledger_not_retained" : null);
    JsonArray Load()
    {
        if (!File.Exists(manifest)) return [];
        var root = JsonNode.Parse(File.ReadAllText(manifest))?.AsObject() ?? throw new InvalidDataException("Invalid reports manifest");
        if ((int?)root["version"] != 1) throw new InvalidDataException("Unsupported reports manifest");
        if ((int?)root["bodyLimit"] != bodyLimit || (long?)root["totalLimit"] != totalLimit ||
            (int?)root["recordLimit"] != recordLimit || (int?)root["pinnedLimit"] != pinnedLimit)
            throw new InvalidDataException("Report store limits differ from its persisted configuration");
        var entries = root["entries"]?.DeepClone().AsArray() ?? throw new InvalidDataException("Missing reports entries");
        var activeSlots = new HashSet<int>();
        foreach (var item in entries.OfType<JsonObject>())
        {
            var slot = (int?)item["leaseSlot"];
            if (slot is null || slot < 0 || slot >= pinnedLimit ||
                ((bool?)item["producerAck"] != true && !activeSlots.Add(slot.Value)))
                throw new InvalidDataException("Invalid or duplicate active producer lease slot");
        }
        return entries;
    }
    void Save(JsonArray entries)
    {
        var json = new JsonObject { ["version"] = 1, ["bodyLimit"] = bodyLimit, ["totalLimit"] = totalLimit,
            ["recordLimit"] = recordLimit, ["pinnedLimit"] = pinnedLimit, ["entries"] = entries.DeepClone() }.ToJsonString();
        var maxManifest = RootOverhead + entries.OfType<JsonObject>().Sum(EntryBudget);
        if (Utf8.GetByteCount(json) > maxManifest) throw new InvalidOperationException("Report metadata exceeds its reserved bound");
        if (Used(entries) > totalLimit) throw new InvalidOperationException("Report storage has insufficient publication space");
        WriteManifest(manifest, json);
    }
    static JsonObject Find(JsonArray entries, string id) => entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == id)
        ?? throw new KeyNotFoundException("Report not found or expired");
    static void ValidateId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid report ID"); }
    string BodyPath(string id) { ValidateId(id); return Path.Combine(directory, id + ".body"); }
    internal string CapturePath(string id) { ValidateId(id); return Path.Combine(directory, id + ".capture"); }
    string BodyStagePath(string id) { ValidateId(id); return Path.Combine(directory, id + ".body.stage"); }
    string LeasePath(JsonObject item)
    {
        var slot = (int?)item["leaseSlot"];
        if (slot is null || slot < 0 || slot >= pinnedLimit) throw new InvalidDataException("Invalid report producer lease slot");
        return Path.Combine(directory, "producer-slot-" + slot.Value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".lock");
    }
    // Data peak: either old journal + compacted snapshot, or journal + old body + new stage.
    // A journal is bounded to 2*body+8192; a compacted UTF-8 candidate is at most body+589.
    long DataBudget => checked(4L * bodyLimit + 16384);
    const long EntryOverhead = 16384, RootOverhead = 1024, SequenceHeadroom = 2048;
    long EntryBudget(JsonObject item) => Utf8.GetByteCount(item["identity"]!.ToJsonString()) + EntryOverhead;
    // One size snapshot feeds both the actual and unused-headroom terms. Captures may append
    // concurrently; reading these quantities independently could undercount a growing file.
    long Used(JsonArray entries)
    {
        var items = entries.OfType<JsonObject>().ToArray();
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            // An independently owned capture can atomically replace its spool between the
            // directory enumeration and stat. Its full unused producer budget covers it.
            try { sizes[Path.GetFileName(path)] = new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
        }
        long Size(string name) => sizes.GetValueOrDefault(name);
        long Data(string id) => Size(id + ".capture") + Size(id + ".capture.compact") +
            Size(id + ".body") + Size(id + ".body.stage") + Size(id + ".body.tmp");
        long actual = sizes.Values.Sum();
        long dataHeadroom = items.Where(e => (bool?)e["producerAck"] != true)
            .Sum(e => Math.Max(0, DataBudget - Data((string)e["id"]!)));
        long manifestBudget = checked(2 * (RootOverhead + items.Sum(EntryBudget)));
        long actualManifests = Size("reports.json") + Size("reports.json.tmp");
        long sequenceActual = Size("id-sequence.json") + Size("id-sequence.json.tmp");
        return checked(actual + dataHeadroom + Math.Max(0, manifestBudget - actualManifests) + Math.Max(0, SequenceHeadroom - sequenceActual));
    }    // The manifest is authoritative. Remove only files with a generated ID absent from it;
    // a pending producer always has a reservation, so its capture cannot be collected here.
    void CleanupOrphans(JsonArray entries)
    {
        var known = entries.OfType<JsonObject>().ToDictionary(e => (string)e["id"]!, e => (bool?)e["producerAck"] == true, StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name is "reports.json.tmp" or "id-sequence.json.tmp") { File.Delete(file); continue; }
            var suffix = name.EndsWith(".capture.compact", StringComparison.Ordinal) ? ".capture.compact" :
                name.EndsWith(".body.stage", StringComparison.Ordinal) ? ".body.stage" :
                name.EndsWith(".body.tmp", StringComparison.Ordinal) ? ".body.tmp" :
                name.EndsWith(".body", StringComparison.Ordinal) ? ".body" :
                name.EndsWith(".capture", StringComparison.Ordinal) ? ".capture" : null;
            if (suffix is null) continue;
            var id = name[..^suffix.Length];
            if (Guid.TryParseExact(id, "N", out _) && (suffix == ".body.tmp" || !known.TryGetValue(id, out var acknowledged) || (suffix is ".capture" or ".capture.compact" && acknowledged))) File.Delete(file);
        }
    }

    // Report IDs encode this store's durable incarnation and monotonic allocation
    // sequence. IDs from different incarnations cannot be ordered safely.
    internal static bool TryCompareReportOrder(string left, string right, out int order)
    {
        order = 0;
        static bool Valid(string value, out long sequence)
        {
            sequence = 0;
            return value.Length == 32 && value == value.ToLowerInvariant() && Guid.TryParseExact(value, "N", out _) &&
                long.TryParse(value.AsSpan(16), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out sequence) && sequence > 0;
        }
        if (!Valid(left, out var a) || !Valid(right, out var b) || !left.AsSpan(0, 16).SequenceEqual(right.AsSpan(0, 16))) return false;
        order = a.CompareTo(b); return true;
    }

    // Persist allocation before making the reservation visible. Skipped numbers are harmless;
    // retaining this tiny counter prevents an evicted ID from ever aliasing a later report.
    string AllocateId()
    {
        var path = Path.Combine(directory, "id-sequence.json");
        string prefix;
        long next;
        if (File.Exists(path))
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Invalid report ID sequence");
            prefix = (string?)root["prefix"] ?? throw new InvalidDataException("Missing report ID prefix");
            next = (long?)root["next"] ?? throw new InvalidDataException("Missing report ID sequence");
            if (prefix.Length != 16 || !prefix.All(Uri.IsHexDigit) || next < 1) throw new InvalidDataException("Invalid report ID sequence");
        }
        else
        {
            if (File.Exists(manifest)) throw new InvalidDataException("Report ID sequence is missing");
            prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            next = 1;
        }
        if (next == long.MaxValue) throw new InvalidOperationException("Report ID sequence exhausted");
        AtomicFile.WriteAllText(path, new JsonObject { ["prefix"] = prefix, ["next"] = next + 1 }.ToJsonString());
        return prefix + next.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
    internal const string UnstartedSpawnReason = "The spawned session's initial native prompt was not started";
    internal ReportReceipt Reserve(ReportIdentity identity, string? unstartedError = null)
    {
        ValidateIdentity(identity);
        if (unstartedError is not null && (identity.Cause != ReportCause.Spawn || Utf8.GetByteCount(unstartedError) > ReportCapture.ErrorBytes))
            throw new ArgumentException("Invalid unstarted spawn failure evidence");
        lock (gate)
        using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load();
            CleanupOrphans(entries);
            var prior = entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["identity"]?["turnKey"] == identity.TurnKey);
            if (prior is not null)
            {
                if (!JsonNode.DeepEquals(prior["identity"], Identity(identity))) throw new InvalidOperationException("Turn identity conflicts with an existing report");
                if (unstartedError is not null &&
                    ((string?)(prior["producerOutcome"] ?? prior["outcome"]) != "not_started" ||
                     (string?)(prior["producerError"] ?? prior["error"]) != unstartedError))
                    throw new InvalidOperationException("Unstarted spawn evidence conflicts with an existing report");
                return new((string)prior["id"]!, (string)prior["status"]!);
            }
            var removed = new List<string>();
            long reclaimable = 0;
            var expiredBefore = Now().AddDays(-30).ToUnixTimeMilliseconds();
            foreach (var expired in entries.OfType<JsonObject>().Where(e => !Pinned(e) && (long?)e["terminalAt"] is { } at && at <= expiredBefore).ToArray())
            {
                var expiredId = (string)expired["id"]!;
                foreach (var path in new[] { BodyPath(expiredId), BodyStagePath(expiredId), CapturePath(expiredId), CapturePath(expiredId) + ".compact" })
                    if (File.Exists(path)) reclaimable += new FileInfo(path).Length;
                removed.Add(expiredId);
                entries.Remove(expired);
            }
            while (entries.Count >= recordLimit || entries.OfType<JsonObject>().Count(Pinned) >= pinnedLimit || Used(entries) - reclaimable + DataBudget + 2 * (Utf8.GetByteCount(Identity(identity).ToJsonString()) + EntryOverhead) > totalLimit)
            {
                var victim = entries.OfType<JsonObject>().Where(e => !Pinned(e)).OrderBy(e => (long?)e["created"] ?? 0).FirstOrDefault();
                if (victim is null) throw new InvalidOperationException("Report storage capacity is exhausted by pinned records or reservations");
                var oldId = (string)victim["id"]!;
                foreach (var path in new[] { BodyPath(oldId), BodyStagePath(oldId), CapturePath(oldId), CapturePath(oldId) + ".compact" })
                    if (File.Exists(path)) reclaimable += new FileInfo(path).Length;
                removed.Add(oldId); entries.Remove(victim);
            }
            // Commit eviction before touching its bodies; no new reservation is admitted until
            // physical cleanup succeeds. A retry cleans an interrupted eviction as orphans.
            if (removed.Count > 0)
            {
                Save(entries);
                foreach (var old in removed) { File.Delete(BodyPath(old)); File.Delete(BodyStagePath(old)); File.Delete(CapturePath(old)); File.Delete(CapturePath(old) + ".compact"); }
                if (Used(entries) + DataBudget + 2 * (Utf8.GetByteCount(Identity(identity).ToJsonString()) + EntryOverhead) > totalLimit) throw new InvalidOperationException("Report storage remains full after eviction");
            }
            var occupied = entries.OfType<JsonObject>().Where(e => (bool?)e["producerAck"] != true)
                .Select(e => (int?)e["leaseSlot"] ?? throw new InvalidDataException("Missing producer lease slot")).ToHashSet();
            var slot = Enumerable.Range(0, pinnedLimit).FirstOrDefault(i => !occupied.Contains(i), -1);
            if (slot < 0) throw new InvalidOperationException("No producer lease slot is available");
            var id = AllocateId();
            var item = new JsonObject { ["id"] = id, ["identity"] = Identity(identity), ["status"] = "reserved", ["leaseSlot"] = slot,
                ["producerAck"] = false, ["created"] = Now().ToUnixTimeMilliseconds(),
                ["producerEvidence"] = identity.HostId is null ? null : "unconfirmed" };
            // A proven unstarted spawn has no native producer to replay. Save its
            // terminal facts in the same publication as the reservation.
            if (unstartedError is not null)
            {
                item["producerEvidence"] = "failed"; item["producerOutcome"] = "not_started";
                item["producerError"] = unstartedError; item["producerFailure"] = UnstartedSpawnReason;
                item["producerFailureHash"] = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(UnstartedSpawnReason)));
            }
            entries.Add((JsonNode)item);
            Save(entries);
            return new(id, "reserved", Created: true);
        }
    }

    // Only a definitely unstarted turn may release its reservation. The caller proves this;
    // opening the capture exclusively prevents racing a producer that is still writing.
    internal void AbandonReservation(string id, ReportIdentity identity)
    {
        ValidateId(id); ValidateIdentity(identity);
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] != "reserved" || !JsonNode.DeepEquals(item["identity"], Identity(identity)))
                throw new InvalidOperationException("Reservation cannot be abandoned by this turn");
            using (new FileStream(LeasePath(item), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                entries.Remove(item);
                Save(entries);
            }
            File.Delete(CapturePath(id));
            File.Delete(CapturePath(id) + ".compact");
            File.Delete(BodyStagePath(id));
            File.Delete(BodyPath(id));
        }
    }
    // Trusted service lookup, including retained published intents and unacknowledged reservations.
    internal ReportInfo[] FindByPrompt(string child, string promptId)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
            return Load().OfType<JsonObject>()
                .Where(e => (string?)e["identity"]?["child"] == child && (string?)e["identity"]?["promptId"] == promptId)
                .Select(Info).ToArray();
    }
    internal ReportInfo GetReport(string id)
    {
        ValidateId(id);
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), id);
            return Info(item);
        }
    }
    // Written by the producing host only after its capture lease is released. A missing
    // confirmation remains unknown on recovery, even if the spool looks complete.
    internal void RecordProducerEvidence(string id, string hostId, string outcome, string? error, string? failure)
    {
        ValidateId(id); ArgumentException.ThrowIfNullOrWhiteSpace(hostId); ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (Utf8.GetByteCount(outcome) > ReportCapture.OutcomeBytes || (error is not null && Utf8.GetByteCount(error) > ReportCapture.ErrorBytes))
            throw new ArgumentException("Producer terminal exceeds its bound");
        if (failure is not null && Utf8.GetByteCount(failure) > ReportCapture.ReasonBytes)
            throw new ArgumentException("Producer failure exceeds its bound");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            var identity = IdentityFrom(item["identity"]);
            if (identity.HostId is not null && identity.HostId != hostId)
                throw new InvalidOperationException("Producer host identity does not match reservation");
            var evidence = failure is null ? "sealed" : "failed";
            var failureHash = failure is null ? null : Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(failure)));
            var prior = (string?)item["producerEvidence"];
            if (prior is "sealed" or "failed")
            {
                var priorOutcome = (string?)item["producerOutcome"] ?? (string?)item["outcome"];
                var priorError = item.ContainsKey("producerError") ? (string?)item["producerError"] : (string?)item["error"];
                if (prior != evidence || (string?)item["producerFailureHash"] != failureHash || priorOutcome != outcome || priorError != error)
                    throw new InvalidOperationException("Producer evidence is immutable");
                return;
            }
            if ((string?)item["status"] != "reserved") throw new InvalidOperationException("Result is already published");
            item["producerEvidence"] = evidence; item["producerFailure"] = failure; item["producerFailureHash"] = failureHash;
            item["producerOutcome"] = outcome; item["producerError"] = error;
            Save(entries);
        }
    }

    internal ReportCapture OpenReservedCapture(string id, string child, string promptId)
    {
        ValidateId(id);
        FileStream owned;
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), id);
            var identity = IdentityFrom(item["identity"]);
            if ((string?)item["status"] != "reserved" || (bool?)item["producerAck"] == true ||
                identity.Child != child || identity.PromptId != promptId)
                throw new InvalidOperationException("Report reservation does not match this session and prompt");
            if (File.Exists(CapturePath(id)))
                throw new InvalidOperationException("Report capture already exists; reconcile it without replaying the native prompt");
            owned = new FileStream(LeasePath(item), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        var capture = new ReportCapture(CapturePath(id), bodyLimit, owned);
        if (capture.Snapshot().Sealed) { capture.Dispose(); throw new InvalidOperationException("Report reservation is already sealed"); }
        return capture;
    }
    internal ReportCapture OpenCapture(string id)
    {
        ValidateId(id);
        var path = CapturePath(id);
        FileStream owned;
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), id);
            if ((bool?)item["producerAck"] == true) throw new InvalidOperationException("Capture has already been acknowledged");
            if ((string?)item["status"] != "reserved" && !File.Exists(path)) throw new FileNotFoundException("Committed capture spool is missing", path);
            owned = new FileStream(LeasePath(item), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        return new ReportCapture(path, bodyLimit, owned);
    }
    internal const string MissingProducerEvidence = "Producer exited after seal before durable capture evidence";
    internal ReportReceipt Commit(string id, ReportCapture capture) => CommitCore(id, capture, null);
    // The caller must first establish producer exit and hold the producer lease. The valid
    // sealed text/outcome are retained while completeness is explicitly downgraded.
    internal ReportReceipt CommitUnconfirmed(string id, ReportCapture capture) => CommitCore(id, capture, MissingProducerEvidence);
    ReportReceipt CommitCore(string id, ReportCapture capture, string? confidenceReason)
    {
        ValidateId(id);
        if (!string.Equals(capture.Path, Path.GetFullPath(CapturePath(id)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Capture does not belong to this report");
        if (capture.BodyLimit != bodyLimit || capture.JournalByteLimit != ReportCapture.JournalLimit(bodyLimit))
            throw new InvalidOperationException("Capture limits do not match the reserved producer budget");
        // The exclusive capture owner serializes commits for this ID. Write the bounded body
        // outside the global metadata lock so slow storage does not hold up other producers.
        lock (capture)
        {
            var snapshot = capture.Snapshot();
            if (!snapshot.Sealed || snapshot.Outcome is null) throw new InvalidOperationException("Capture must have a sealed terminal before publication");
            var outcome = snapshot.Outcome;
            var incompleteReason = snapshot.IncompleteReason ?? confidenceReason;
            byte[] bytes;
            try { bytes = Utf8.GetBytes(snapshot.Text); }
            catch (EncoderFallbackException)
            {
                if (incompleteReason is null) throw;
                var retained = snapshot.Text;
                if (retained.Length > 0 && char.IsHighSurrogate(retained[^1])) retained = retained[..^1];
                bytes = Utf8.GetBytes(retained);
            }
            if (bytes.Length > bodyLimit) throw new InvalidOperationException("Report body exceeds its limit");
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            var stage = BodyStagePath(id);
            try
            {
                WriteBodyStage(stage, Utf8.GetString(bytes));
                lock (gate) using (SharedFileLock.Acquire(manifest))
                {
                    var entries = Load(); var item = Find(entries, id);
                    if ((string?)item["status"] != "reserved")
                    {
                        if ((string?)item["digest"] != digest || (string?)item["outcome"] != outcome ||
                            (bool?)item["hasText"] != snapshot.HasText || (string?)item["incompleteReason"] != incompleteReason ||
                            (string?)item["captureConfidenceReason"] != confidenceReason || (string?)item["error"] != snapshot.Error)
                            throw new InvalidOperationException("Report ID is already committed with different content");
                        return new(id, (string)item["status"]!);
                    }
                    // Body move precedes the single manifest publication of result and intent.
                    File.Move(stage, BodyPath(id), overwrite: true);
                    item["digest"] = digest; item["bodyBytes"] = bytes.Length; item["outcome"] = outcome;
                    item["hasText"] = snapshot.HasText; item["incompleteReason"] = incompleteReason; item["error"] = snapshot.Error;
                    if (confidenceReason is not null) item["captureConfidenceReason"] = confidenceReason;
                    item["status"] = "pending";
                    item.Remove("producerOutcome"); item.Remove("producerError"); item.Remove("producerFailure");
                    Save(entries);
                    return new(id, "pending");
                }
            }
            finally { File.Delete(stage); }
        }
    }
    // Reconcile an unreadable, missing or otherwise unavailable producer spool without claiming
    // any text was captured. This still publishes the result and pending intent together.
    internal ReportReceipt CommitCaptureFailure(string id, string outcome, string reason, string? error = null)
    {
        ValidateId(id); ArgumentException.ThrowIfNullOrWhiteSpace(outcome); ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Utf8.GetByteCount(outcome) > ReportCapture.OutcomeBytes) throw new ArgumentException("Report outcome is too long");
        if (Utf8.GetByteCount(reason) > ReportCapture.ReasonBytes) throw new ArgumentException("Capture failure reason is too long");
        if (error is not null && Utf8.GetByteCount(error) > ReportCapture.ErrorBytes) throw new ArgumentException("Capture failure error is too long");
        var digest = Convert.ToHexString(SHA256.HashData([]));
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] != "reserved")
            {
                if ((string?)item["digest"] != digest || (string?)item["outcome"] != outcome ||
                    (bool?)item["hasText"] != false || (string?)item["incompleteReason"] != reason || (string?)item["error"] != error)
                    throw new InvalidOperationException("Report ID is already committed with different content");
                return new(id, (string)item["status"]!);
            }
            AtomicFile.WriteAllText(BodyPath(id), "", Utf8);
            item["digest"] = digest; item["bodyBytes"] = 0; item["outcome"] = outcome;
            item["hasText"] = false; item["incompleteReason"] = reason; item["error"] = error; item["status"] = "pending";
            item.Remove("producerOutcome"); item.Remove("producerError"); item.Remove("producerFailure");
            Save(entries);
            return new(id, "pending");
        }
    }
    internal void AcknowledgeProducer(string id)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] == "reserved") throw new InvalidOperationException("Cannot acknowledge an unpublished report");
            if ((bool?)item["producerAck"] == true) return;
            using (new FileStream(LeasePath(item), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                item["producerAck"] = true; Save(entries);
                File.Delete(CapturePath(id));
                File.Delete(CapturePath(id) + ".compact");
            }
        }
    }

    internal void RecordEndJournaled(string id)
    {
        ValidateId(id);
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] == "reserved" || (bool?)item["producerAck"] != true)
                throw new InvalidOperationException("Cannot record end before report import and ACK");
            if ((bool?)item["endJournaled"] == true) return;
            item["endJournaled"] = true; Save(entries);
        }
    }

    // One bounded claim is part of the same COW manifest as the immutable result. A retry can
    // release it only before the durable dispatch-start marker; no full envelope is duplicated.
    internal ReportDeliveryClaim ClaimDelivery(string id, string parent, string? hostId, string? hostGeneration,
        string route = "queued", string? envelopeDigest = null, int? hostProcessId = null, string? hostProcessStart = null)
    {
        ValidateId(id);
        if (route is not ("queued" or "note") || (route == "note" &&
            (hostId is null || hostGeneration is null || envelopeDigest is null || envelopeDigest.Length != 64 ||
             !envelopeDigest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))))
            throw new ArgumentException("Invalid report delivery route or digest");
        if (hostId is not null && Utf8.GetByteCount(hostId) > 128 || hostGeneration is not null && Utf8.GetByteCount(hostGeneration) > 256 ||
            hostProcessId is <= 0 || hostProcessStart is not null && Utf8.GetByteCount(hostProcessStart) > 64)
            throw new ArgumentException("Delivery host identity exceeds its reserved bound");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id); var identity = IdentityFrom(item["identity"]);
            if (identity.Parent != parent) throw new InvalidOperationException("Delivery must use the immutable intended parent");
            if ((string?)item["status"] != "pending") throw new InvalidOperationException("Report is not pending");
            var previous = item["delivery"];
            JsonNode? proof = previous?["previousNoteProof"]?.DeepClone();
            var provenNote = (string?)previous?["route"] == "note" && previous?["noInputProof"] is not null;
            if (provenNote)
            {
                if (route != "queued") throw new InvalidOperationException("A proved no-input note must use queued fallback");
                proof = new JsonObject { ["attemptId"] = previous?["attemptId"]?.DeepClone(),
                    ["hostId"] = previous?["hostId"]?.DeepClone(), ["hostGeneration"] = previous?["hostGeneration"]?.DeepClone(),
                    ["envelopeDigest"] = previous?["envelopeDigest"]?.DeepClone(), ["reason"] = previous?["noInputProof"]?.DeepClone() };
            }
            var attempt = provenNote ? Guid.NewGuid().ToString("N") : (string?)previous?["attemptId"] ?? Guid.NewGuid().ToString("N");
            var message = "report-" + id;
            var claim = new ReportDeliveryClaim(attempt, parent, message, "service-" + message, route, hostId, hostGeneration,
                HostProcessId: hostProcessId, HostProcessStart: hostProcessStart);
            item["delivery"] = new JsonObject { ["attemptId"] = claim.AttemptId,
                ["messageId"] = message, ["promptId"] = claim.PromptId, ["route"] = claim.Route,
                ["hostId"] = hostId, ["hostGeneration"] = hostGeneration, ["dispatchStarted"] = false,
                ["hostProcessId"] = hostProcessId, ["hostProcessStart"] = hostProcessStart,
                ["envelopeDigest"] = envelopeDigest, ["previousNoteProof"] = proof };
            item["status"] = "claimed"; item["reason"] = null; Save(entries); return claim;
        }
    }
    internal void StartDelivery(string id, string attempt)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] != "claimed" || (string?)item["delivery"]?["attemptId"] != attempt)
                throw new InvalidOperationException("Delivery claim is not current");
            item["delivery"]!["dispatchStarted"] = true; Save(entries);
        }
    }
    internal void ReleaseUnstartedDelivery(string id, string attempt, string? reason = null)
    {
        if (reason is not null && Utf8.GetByteCount(reason) > ReportCapture.ReasonBytes) throw new ArgumentException("Disposition reason is too long");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] != "claimed" || (string?)item["delivery"]?["attemptId"] != attempt ||
                (bool?)item["delivery"]?["dispatchStarted"] == true)
                throw new InvalidOperationException("Only a proven unstarted claim can be released");
            item["status"] = "pending"; item["reason"] = reason; Save(entries);
        }
    }
    internal void PendingDeliveryReason(string id, string? reason)
    {
        if (reason is not null && Utf8.GetByteCount(reason) > ReportCapture.ReasonBytes) throw new ArgumentException("Disposition reason is too long");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] != "pending") return;
            if ((string?)item["reason"] == reason) return;
            item["reason"] = reason; Save(entries);
        }
    }

    internal void SetDisposition(string id, string status, string? reason = null)
    {
        if (status is not ("pending" or "claimed" or "accepted" or "uncertain" or "completed" or "failed" or "retained")) throw new ArgumentException("Invalid report status");
        if (status is "uncertain" or "failed" && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Failure and uncertainty require a reason");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, id);
            if ((string?)item["status"] == "reserved") throw new InvalidOperationException("Report is not published");
            var previous = (string)item["status"]!;
            if (reason is not null && Utf8.GetByteCount(reason) > ReportCapture.ReasonBytes) throw new ArgumentException("Disposition reason is too long");
            var allowed = previous == status || (previous, status) switch
            {
                ("pending", "claimed" or "failed" or "retained") => true,
                ("claimed", "accepted" or "uncertain" or "failed") => true,
                ("accepted", "completed" or "uncertain" or "failed") => true,
                ("uncertain", "completed" or "failed") => true,
                _ => false,
            };
            if (!allowed) throw new InvalidOperationException("Invalid report disposition transition");
            if (previous == status && status is "completed" or "failed" or "retained" && (string?)item["reason"] != reason) throw new InvalidOperationException("A terminal disposition is immutable");
            if (previous == "uncertain" && status != previous && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Resolving uncertainty requires evidence");
            item["status"] = status; item["reason"] = reason;
            if (status is "completed" or "failed" or "retained" && previous != status) item["terminalAt"] = Now().ToUnixTimeMilliseconds();
            Save(entries);
        }
    }

    // Cursors encode a durable monotonically allocated ID. They do not depend on the
    // referenced entry remaining in this filtered view or surviving terminal eviction.
    internal ReportListPage List(string caller, string? cursor = null, int limit = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        var scope = "l" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(caller))).ToLowerInvariant();
        lock (gate) using (SharedFileLock.Acquire(manifest))
            return Page(Load().OfType<JsonObject>().Where(e => (string?)e["identity"]?["child"] == caller ||
                (string?)e["identity"]?["parent"] == caller).ToArray(), cursor, limit, scope);
    }
    internal ReportInfo[] FindDeliveryByPrompt(string parent, string promptId)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
            return Load().OfType<JsonObject>().Where(e => (string?)e["identity"]?["parent"] == parent &&
                (string?)e["delivery"]?["promptId"] == promptId).Select(Info).ToArray();
    }
    internal ReportListPage Inventory(string? cursor = null, int limit = 50)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
            return Page(Load().OfType<JsonObject>().ToArray(), cursor, limit, "r");
    }
    internal ReportListPage Recoverable(string? cursor = null, int limit = 50)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
            return Page(Load().OfType<JsonObject>().Where(e => (bool?)e["producerAck"] != true).ToArray(), cursor, limit, "r");
    }
    ReportListPage Page(JsonObject[] items, string? cursor, int limit, string scope)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        long after = 0;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] != scope || !Guid.TryParseExact(parts[1], "N", out _)) throw new ArgumentException("Invalid report list cursor");
            var sequencePath = Path.Combine(directory, "id-sequence.json");
            var sequence = File.Exists(sequencePath) ? JsonNode.Parse(File.ReadAllText(sequencePath)) : null;
            var prefix = (string?)sequence?["prefix"];
            var next = (long?)sequence?["next"];
            if (prefix is null || !parts[1].StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(parts[1].AsSpan(16), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out after) || after < 1 || next is null || after >= next)
                throw new ArgumentException("Invalid report list cursor");
        }
        long Sequence(JsonObject e) => long.Parse(((string)e["id"]!)[16..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var eligible = items.Where(e => Sequence(e) > after).OrderBy(Sequence).ToArray();
        var page = eligible.Take(limit).Select(Info).ToArray();
        return new(page, page.Length < eligible.Length ? scope + ":" + page[^1].ReportId : null);
    }    internal ReportPreview Preview(string id, string caller)
    {
        var page = Read(id, caller, limit: 16384);
        var end = Math.Min(page.Text.Length, 2000);
        if (end > 0 && end < page.Text.Length && char.IsHighSurrogate(page.Text[end - 1]) && char.IsLowSurrogate(page.Text[end])) end--;
        return new(page.Text[..end], end < page.Text.Length || page.NextCursor is not null, page.HasText, page.IncompleteReason);
    }
    internal ReportPage Read(string id, string caller, string? cursor = null, int limit = 4096)
    {
        ValidateId(id); ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        if (limit is < 4 or > 16384) throw new ArgumentOutOfRangeException(nameof(limit));
        JsonObject item;
        FileStream body;
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            item = Find(Load(), id);
            if ((string?)item["identity"]?["child"] != caller && (string?)item["identity"]?["parent"] != caller)
                throw new UnauthorizedAccessException("Report is not available to this session");
            if ((string?)item["status"] == "reserved") throw new InvalidOperationException("Report is not published");
            // Open the immutable body while the reference is valid. Delete sharing lets eviction
            // proceed after the lock is released while this handle keeps its snapshot readable.
            body = new FileStream(BodyPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        using (body)
        {
            var expected = (long?)item["bodyBytes"] ?? throw new InvalidDataException("Missing report body size");
            if (expected < 0 || expected > bodyLimit || body.Length != expected) throw new InvalidDataException("Invalid report body size");
            var data = new byte[(int)expected];
            body.ReadExactly(data);
            if (body.Length != expected || Convert.ToHexString(SHA256.HashData(data)) != (string?)item["digest"])
                throw new InvalidDataException("Report body digest mismatch");
            var offset = 0;
            if (cursor is not null)
            {
                var parts = cursor.Split(':');
                if (parts.Length != 2 || parts[0] != id || !int.TryParse(parts[1], out offset) || offset < 0 || offset > data.Length ||
                    (offset < data.Length && (data[offset] & 0xC0) == 0x80)) throw new ArgumentException("Invalid report cursor");
            }
            var end = Math.Min(data.Length, offset + limit);
            while (end < data.Length && (data[end] & 0xC0) == 0x80) end--;
            return new(id, IdentityFrom(item["identity"]), Utf8.GetString(data, offset, end - offset), end < data.Length ? $"{id}:{end}" : null,
                (string)item["status"]!, (string?)item["reason"], (string)item["outcome"]!, (string?)item["error"], (bool)item["hasText"]!, (string?)item["incompleteReason"], data.Length,
                (string?)item["captureConfidenceReason"], DeliveryFrom(item["delivery"], (string)item["identity"]!["parent"]!));
        }
    }
}
