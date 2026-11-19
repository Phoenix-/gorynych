using System.Text.Json.Nodes;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

[TestClass]
public sealed class CompletionHostLedgerTests
{
    static CompletionReminderBinding Binding() => new("project", "group", Guid.NewGuid().ToString("N"), "prompt",
        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null);

    [TestMethod]
    public void CompactionPastOldCapacityPreservesWatermarkAndRejectsDelayedSubmitAfterRestart()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "host-ledger.json");
        var mailboxPath = Path.Combine(dir, "mailbox.json"); var mailbox = new MailboxStore(mailboxPath);
        var ledger = new CompletionHostLedger(path, capacity: 2);
        string? firstStream = null, firstAttempt = null; JsonObject? firstIdentity = null; long firstNumber = 0;
        for (var i = 0; i < 70; i++)
        {
            var id = "task-" + i; mailbox.Accept(id, "owner", "child", "work", delegation: true);
            mailbox.MarkStarted(id, "child"); mailbox.MarkDelivered(id, "child");
            var generation = (string)mailbox.FindAssignment(id, "owner")!["generation"]!; var attempt = Guid.NewGuid().ToString("N");
            var binding = Binding(); var claim = ledger.ClaimFromMailbox(mailbox, "delegation", id, "owner", "child", generation, 1, attempt, binding);
            Assert.IsTrue(claim.Acquired); var row = claim.Claim!;
            var identity = CompletionHostLedger.Identity("delegation", id, "owner", "child", generation, 1, attempt, binding, row.Stream!, row.Number);
            if (i == 0) { firstStream = row.Stream; firstNumber = row.Number; firstAttempt = attempt; firstIdentity = identity.DeepClone().AsObject(); }
            Assert.AreEqual(firstStream, row.Stream); Assert.AreEqual(i + 1L, row.Number);
            Assert.AreEqual(CompletionHostLedger.Admission.Retained, ledger.ReadAdmission(row.Stream!, row.Number, attempt, identity));
            Assert.IsTrue(ledger.AbortBeforeForward(row.Stream!, row.Number, attempt, identity));
            var durableAbort = new CompletionHostLedger(path, capacity: 2).ReadAttempt(attempt)!;
            Assert.AreEqual("confirmed_precommit", (string?)durableAbort["abortVerdict"]);
            Assert.AreEqual("host_before_forward", (string?)durableAbort["abortOrigin"]);
            Assert.AreEqual(attempt, (string?)durableAbort["identity"]?["attemptId"]);
            Assert.IsFalse(new CompletionHostLedger(path, capacity: 2).BeginForward(row.Stream!, row.Number, attempt, identity));
            Assert.AreEqual(CompletionHostLedger.Admission.RetainedAborted, ledger.ReadAdmission(row.Stream!, row.Number, attempt, identity));
            Assert.IsTrue(ledger.CompactWithDurableConsumer(row.Stream!, row.Number, attempt, identity,
                outcome => outcome == "not_sent" && mailbox.SettleCompletionReminder("delegation", id, "owner", "child", generation, 1, attempt, CompletionReminderSettlement.ProvenNotSent)));
        }
        ledger = new CompletionHostLedger(path, capacity: 2); mailbox = new MailboxStore(mailboxPath);
        Assert.AreEqual(CompletionHostLedger.Admission.Stale, ledger.ReadAdmission(firstStream!, firstNumber, firstAttempt!, firstIdentity!));
        Assert.AreEqual(CompletionHostLedger.Admission.Stale, ledger.ReadAdmission(Guid.NewGuid().ToString("N"), 1000, Guid.NewGuid().ToString("N"), new JsonObject { ["x"] = 1 }));
        Assert.AreEqual("not_sent", mailbox.ReadCompletionReminder("task-0", "owner", "child", (string)firstIdentity!["assignmentGeneration"]!)!.Disposition);
    }

    [TestMethod]
    public void MismatchedRowsDoNotInvokeConsumer()
    {
        var dir = TestDirs.New(); var ledger = new CompletionHostLedger(Path.Combine(dir, "ledger.json"), capacity: 1);
        var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        mailbox.Accept("task", "owner", "child", "work", delegation: true); mailbox.MarkStarted("task", "child"); mailbox.MarkDelivered("task", "child");
        var generation = (string)mailbox.FindAssignment("task", "owner")!["generation"]!; var attempt = Guid.NewGuid().ToString("N"); var binding = Binding();
        var created = ledger.ClaimFromMailbox(mailbox, "delegation", "task", "owner", "child", generation, 1, attempt, binding).Claim!;
        var identity = CompletionHostLedger.Identity("delegation", "task", "owner", "child", generation, 1, attempt, binding, created.Stream!, created.Number);
        var called = false;
        Assert.IsFalse(ledger.CompactWithDurableConsumer(created.Stream!, created.Number, attempt, new JsonObject { ["x"] = 2 }, _ => { called = true; return true; }));
        Assert.IsFalse(called);
        Assert.IsFalse(ledger.CompactWithDurableConsumer(created.Stream!, created.Number, attempt, identity, _ => { called = true; return true; }));
        Assert.IsFalse(called);
        Assert.IsTrue(ledger.BeginForward(created.Stream!, created.Number, attempt, identity));
        Assert.IsFalse(ledger.AbortBeforeForward(created.Stream!, created.Number, attempt, identity));
        Assert.IsFalse(ledger.CompactWithDurableConsumer(created.Stream!, created.Number, attempt, identity, _ => false));
        Assert.AreEqual(CompletionHostLedger.Admission.Retained, ledger.ReadAdmission(created.Stream!, created.Number, attempt, identity));
        Assert.IsNotNull(mailbox.Accept("ordinary", "a", "b", "hello"));
    }
    [TestMethod]
    public void MissingLedgerWithRetainedIncarnationCannotBeReinitialized()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ledger.json"); var stream = Guid.NewGuid().ToString("N");
        var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json")); stream = mailbox.PrepareCompletionStream();
        var ledger = new CompletionHostLedger(path); ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream));
        mailbox.Accept("task", "owner", "child", "work", delegation: true); mailbox.MarkStarted("task", "child"); mailbox.MarkDelivered("task", "child");
        var generation = (string)mailbox.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsTrue(ledger.ClaimFromMailbox(mailbox, "delegation", "task", "owner", "child", generation, 1, Guid.NewGuid().ToString("N"), Binding()).Acquired);
        File.Delete(path);
        Assert.ThrowsExactly<InvalidDataException>(() => ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream)));
        Assert.ThrowsExactly<InvalidDataException>(() => ledger.ReadAdmission(stream, 1, Guid.NewGuid().ToString("N"), new JsonObject { ["x"] = 1 }));
    }
    [TestMethod]
    public void LostLedgerCannotBeRecreatedAfterAnyMailboxClaim()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ledger.json"); var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        var stream = mailbox.PrepareCompletionStream(); var ledger = new CompletionHostLedger(path);
        ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream));
        mailbox.Accept("task", "owner", "child", "work", delegation: true); mailbox.MarkStarted("task", "child"); mailbox.MarkDelivered("task", "child");
        var generation = (string)mailbox.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsTrue(mailbox.ClaimCompletionReminder("delegation", "task", "owner", "child", generation, 1, Guid.NewGuid().ToString("N"), Binding()).Acquired);
        File.Delete(path); File.Delete(path + ".anchor");
        Assert.ThrowsExactly<InvalidOperationException>(() => ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream)));
    }
    [TestMethod]
    public void HostCapacityDefersNewClaimWithoutBlockingOrdinaryMessages()
    {
        var dir = TestDirs.New(); var ledger = new CompletionHostLedger(Path.Combine(dir, "ledger.json"), capacity: 1);
        var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        foreach (var id in new[] { "one", "two" })
        {
            mailbox.Accept(id, "owner", "child", "work", delegation: true);
            mailbox.MarkStarted(id, "child"); mailbox.MarkDelivered(id, "child");
        }
        var one = (string)mailbox.FindAssignment("one", "owner")!["generation"]!;
        var two = (string)mailbox.FindAssignment("two", "owner")!["generation"]!;
        var attempt = Guid.NewGuid().ToString("N"); var binding = Binding();
        var first = ledger.ClaimFromMailbox(mailbox, "delegation", "one", "owner", "child", one, 1, attempt, binding);
        Assert.IsTrue(first.Acquired);
        var retry = ledger.ClaimFromMailbox(mailbox, "delegation", "one", "owner", "child", one, 1, attempt, binding);
        Assert.IsFalse(retry.Acquired); Assert.AreEqual(first.Claim, retry.Claim);
        var deferred = ledger.ClaimFromMailbox(mailbox, "delegation", "two", "owner", "child", two, 1, Guid.NewGuid().ToString("N"), Binding());
        Assert.IsFalse(deferred.Acquired); Assert.IsNull(deferred.Claim);
        Assert.IsNull(mailbox.ReadCompletionReminder("two", "owner", "child", two));
        Assert.IsNotNull(mailbox.Accept("ordinary", "a", "b", "hello"));
    }
    [TestMethod]
    public void LostHostAdmissionAfterMailboxClaimNeverReacquires()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ledger.json"); var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        var ledger = new CompletionHostLedger(path); var stream = mailbox.PrepareCompletionStream();
        ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream));
        mailbox.Accept("task", "owner", "child", "work", delegation: true); mailbox.MarkStarted("task", "child"); mailbox.MarkDelivered("task", "child");
        var generation = (string)mailbox.FindAssignment("task", "owner")!["generation"]!; var attempt = Guid.NewGuid().ToString("N"); var binding = Binding();
        var write = ledger.WriteFile; ledger.WriteFile = (file, text) => { write(file, text); throw new IOException("after host replacement"); };
        Assert.ThrowsExactly<IOException>(() => ledger.ClaimFromMailbox(mailbox, "delegation", "task", "owner", "child", generation, 1, attempt, binding));
        ledger = new CompletionHostLedger(path); mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        var retry = ledger.ClaimFromMailbox(mailbox, "delegation", "task", "owner", "child", generation, 1, attempt, binding);
        Assert.IsFalse(retry.Acquired); Assert.AreEqual("uncertain", retry.Claim!.Disposition);
        var identity = CompletionHostLedger.Identity("delegation", "task", "owner", "child", generation, 1, attempt, binding, retry.Claim.Stream!, retry.Claim.Number);
        Assert.AreEqual(CompletionHostLedger.Admission.Retained, ledger.ReadAdmission(retry.Claim.Stream!, retry.Claim.Number, attempt, identity));
    }
    [TestMethod]
    public void PristineMatchingAnchorCanRepairInterruptedInitialization()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ledger.json"); var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        var stream = mailbox.PrepareCompletionStream(); File.WriteAllText(path + ".anchor", stream);
        var ledger = new CompletionHostLedger(path);
        ledger.InitializeNewStream(stream, () => mailbox.CompletionStreamIsPristine(stream));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void SimulatedExitProofClosesUnknownAndCompactsWithoutAllowingReplay()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ledger.json"); var mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json"));
        mailbox.Accept("task", "owner", "child", "work", delegation: true); mailbox.MarkStarted("task", "child"); mailbox.MarkDelivered("task", "child");
        var generation = (string)mailbox.FindAssignment("task", "owner")!["generation"]!; var attempt = Guid.NewGuid().ToString("N"); var binding = Binding();
        var ledger = new CompletionHostLedger(path); var result = ledger.ClaimFromMailbox(mailbox, "delegation", "task", "owner", "child", generation, 1, attempt, binding);
        Assert.IsTrue(result.Acquired); var claim = result.Claim!;
        var identity = CompletionHostLedger.Identity("delegation", "task", "owner", "child", generation, 1, attempt, binding, claim.Stream!, claim.Number);
        Assert.IsTrue(ledger.BeginForward(claim.Stream!, claim.Number, attempt, identity));
        Assert.IsFalse(ledger.CompactUnknownAfterProducerExit(claim.Stream!, claim.Number, attempt, identity, () => false));
        Assert.IsTrue(ledger.CompactUnknownAfterProducerExit(claim.Stream!, claim.Number, attempt, identity,
            () => mailbox.CloseCompletionReminderUnknown("delegation", "task", "owner", "child", generation, 1, attempt)));
        mailbox = new MailboxStore(Path.Combine(dir, "mailbox.json")); ledger = new CompletionHostLedger(path);
        Assert.AreEqual("unknown_closed", mailbox.ReadCompletionReminder("task", "owner", "child", generation)!.Disposition);
        Assert.AreEqual(1, mailbox.ListCompletionUnknownReceipts("owner")["items"]!.AsArray().Count);
        Assert.AreEqual(CompletionHostLedger.Admission.Stale, ledger.ReadAdmission(claim.Stream!, claim.Number, attempt, identity));
    }
    [TestMethod]
    public void RecoveryRetriesCompactionAfterMailboxSettlementAndLedgerFailure()
    {
        var dir = TestDirs.New(); var ledgerPath = Path.Combine(dir, "ledger.json"); var mailboxPath = Path.Combine(dir, "mailbox.json");
        var mailbox = new MailboxStore(mailboxPath); var ledger = new CompletionHostLedger(ledgerPath);
        mailbox.Accept("recovery", "owner", "child", "work", delegation: true); mailbox.MarkStarted("recovery", "child"); mailbox.MarkDelivered("recovery", "child");
        var generation = (string)mailbox.FindAssignment("recovery", "owner")!["generation"]!; var attempt = Guid.NewGuid().ToString("N"); var binding = Binding();
        var claim = ledger.ClaimFromMailbox(mailbox, "delegation", "recovery", "owner", "child", generation, 1, attempt, binding).Claim!;
        var identity = CompletionHostLedger.Identity("delegation", "recovery", "owner", "child", generation, 1, attempt, binding, claim.Stream!, claim.Number);
        Assert.IsTrue(ledger.AbortBeforeForward(claim.Stream!, claim.Number, attempt, identity));
        var saved = mailbox.ReadCompletionRecovery("recovery", "owner", "child", generation, attempt)!;
        ledger.WriteFile = (_, _) => throw new IOException("injected ledger failure after mailbox settlement");
        Assert.ThrowsExactly<IOException>(() => ledger.RecoverAfterProducerExit(saved, mailbox));
        mailbox = new MailboxStore(mailboxPath); ledger = new CompletionHostLedger(ledgerPath);
        saved = mailbox.ReadCompletionRecovery("recovery", "owner", "child", generation, attempt)!;
        Assert.AreEqual("not_sent", saved.Disposition);
        Assert.AreEqual("not_sent", ledger.RecoverAfterProducerExit(saved, mailbox));
        Assert.AreEqual(CompletionHostLedger.Admission.Stale, ledger.ReadAdmission(claim.Stream!, claim.Number, attempt, identity));
        Assert.AreEqual("not_sent", ledger.RecoverAfterProducerExit(saved, mailbox));
    }

}
