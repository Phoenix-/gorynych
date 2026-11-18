using System.Text.Json.Nodes;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReplyReminderStoreTests
{
    [TestMethod]
    public void ScheduleUsesConfirmedDeliveryTimeAndRetainsDeadlineAcrossRestart()
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, clock: clock);
        store.Accept("passive", "a", "b", "old");
        store.Accept("q", "a", "b", "question", expectReply: true,
            schedule: new("project", "group", now, now.AddMinutes(10), TimeSpan.FromMinutes(2)));
        clock.Advance(TimeSpan.FromMinutes(3)); store.MarkDelivered("q", "b");
        var wait = new MailboxStore(path, clock: clock).ListReplyWaits("a", 0, 10)["items"]![0]!;
        Assert.AreEqual(now.AddMinutes(10), DateTimeOffset.Parse((string)wait["reminders"]!["overdueAt"]!));
        Assert.AreEqual(now.AddMinutes(5), DateTimeOffset.Parse((string)wait["reminders"]!["reviewAt"]!));
        Assert.IsNull(store.ClaimReminder("q", false, clock.GetUtcNow(), "project", "group"));
        clock.Advance(TimeSpan.FromMinutes(2));
        var item = store.ClaimReminder("q", false, clock.GetUtcNow(), "project", "group");
        Assert.IsNotNull(item); Assert.AreEqual(true, (bool?)item["started"]);
        Assert.IsNull(store.ClaimReminder("q", false, clock.GetUtcNow(), "project", "group"));
        var recovered = new MailboxStore(path, clock: clock);
        Assert.AreEqual((string?)item["id"], (string?)recovered.Read("b", 0, 10, true).Last()["id"]);
        Assert.AreEqual("started", (string?)recovered.ListReplyWaits("a", 0, 10)["items"]![0]!["reminders"]!["reminder"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReplyOrCancelBeforeClaimSuppressesReminder(bool cancel)
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var store = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"), clock: clock);
        store.Accept("q", "a", "b", "question", expectReply: true, schedule: new("p", "g", now, now.AddMinutes(1), TimeSpan.FromSeconds(1)));
        store.MarkDelivered("q", "b"); clock.Advance(TimeSpan.FromSeconds(2));
        if (cancel) store.CancelReplyWait("q", "a"); else store.Accept("r", "b", "a", "reply", "q");
        Assert.IsNull(store.ClaimReminder("q", false, clock.GetUtcNow(), "p", "g"));
        Assert.AreEqual(0, store.Read("b", 0, 10, true).Count);
    }

    [TestMethod]
    public void ReciprocalWaitsEscalateWithoutReminderPingPong()
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var store = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"), clock: clock);
        var schedule = new ReplyReminderSchedule("p", "g", now, now.AddSeconds(10), TimeSpan.FromSeconds(1));
        store.Accept("ab", "a", "b", "question", expectReply: true, schedule: schedule);
        store.Accept("ba", "b", "a", "question", expectReply: true, schedule: schedule);
        store.MarkDelivered("ab", "b"); store.MarkDelivered("ba", "a"); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsNull(store.ClaimReminder("ab", false, clock.GetUtcNow(), "p", "g"));
        Assert.AreEqual("ba", (string?)store.ListReplyWaits("a", 0, 10)["items"]![0]!["reminders"]!["reciprocalMessageId"]);
        var notice = store.ClaimReminder("ab", true, clock.GetUtcNow(), "p", "g");
        Assert.AreEqual("a", (string?)notice!["recipient"]); StringAssert.Contains((string?)notice["text"], "ba");
        Assert.IsNull(store.ClaimReminder("ab", true, clock.GetUtcNow(), "p", "g"));
    }

    [TestMethod]
    public void FailedClaimWriteLeavesNoIntentOrBudgetSpent()
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path, clock: clock);
        store.Accept("q", "a", "b", "question", expectReply: true, schedule: new("p", "g", now, now.AddMinutes(1), TimeSpan.FromSeconds(1)));
        store.MarkDelivered("q", "b"); clock.Advance(TimeSpan.FromSeconds(2));
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.ClaimReminder("q", false, clock.GetUtcNow(), "p", "g"));
        Assert.AreEqual(0, store.Read("b", 0, 10, true).Count);
        Assert.AreEqual("pending", (string?)new MailboxStore(path, clock: clock).ListReplyWaits("a", 0, 10)["items"]![0]!["reminders"]!["reminder"]);
    }
    [TestMethod]
    public void MissingOrInvalidPolicyIsDisabled()
    {
        var file = Path.Combine(TestDirs.New(), "reply-reminders.json");
        Assert.IsNull(ReplyReminderPolicy.Load(file, out var missing)); StringAssert.Contains(missing, "disabled");
        File.WriteAllText(file, """{"projectKey":"p"}""");
        Assert.IsNull(ReplyReminderPolicy.Load(file, out var invalid)); StringAssert.Contains(invalid, "invalid");
        File.WriteAllText(file, """{"projectKey":"p","groupId":"g"}""");
        var policy = ReplyReminderPolicy.Load(file, out var status);
        Assert.IsNotNull(policy); Assert.AreEqual("enabled", status);
        Assert.AreEqual(TimeSpan.FromMinutes(2), policy.Delay); Assert.AreEqual(TimeSpan.FromMinutes(10), policy.Overdue);
    }

    [TestMethod]
    public void OverdueUndeliveredRequestRemainsVisibleAndLateReplyResolves()
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var store = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"), clock: clock);
        store.Accept("q", "a", "b", "question", expectReply: true,
            schedule: new("p", "g", now, now.AddSeconds(10), TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(10));
        store.ReminderReason("q", "recipient unavailable"); store.RecordOverdue("q", clock.GetUtcNow());
        store.RecordOverdue("q", clock.GetUtcNow().AddSeconds(5));
        var wait = store.ListReplyWaits("a", 0, 10)["items"]![0]!;
        Assert.AreEqual("recipient unavailable", (string?)wait["reminders"]!["overdueNotice"]!["reason"]);
        Assert.AreEqual(clock.GetUtcNow(), DateTimeOffset.Parse((string)wait["reminders"]!["overdueNotice"]!["observedAt"]!));
        Assert.AreEqual(true, (bool?)wait["reminders"]!["overdue"]);
        Assert.AreEqual("queued", (string?)wait["requestDelivery"]);
        var notice = store.ClaimReminder("q", true, clock.GetUtcNow(), "p", "g");
        Assert.IsNotNull(notice); StringAssert.Contains((string?)notice["text"], "queued");
        store.Accept("r", "b", "a", "late reply", "q");
        Assert.AreEqual("replied", (string?)store.ListReplyWaits("a", 0, 10)["items"]![0]!["state"]);
        Assert.IsNull(store.ClaimReminder("q", true, clock.GetUtcNow(), "p", "g"));
    }

    [TestMethod]
    public void ScheduledAdmissionFailurePublishesNeitherWaitNorMessage()
    {
        var clock = new ManualTime(); var now = clock.GetUtcNow();
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, clock: clock) { WriteFile = (_, _) => throw new IOException("injected") };
        Assert.ThrowsExactly<IOException>(() => store.Accept("q", "a", "b", "question", expectReply: true,
            schedule: new("p", "g", now, now.AddMinutes(1), TimeSpan.FromSeconds(2))));
        Assert.IsNull(store.Find("q")); Assert.AreEqual(0, store.ListReplyWaits("a", 0, 10)["items"]!.AsArray().Count);
        Assert.AreEqual(0, new MailboxStore(path, clock: clock).ListReplyWaits("a", 0, 10)["items"]!.AsArray().Count);
    }

}
