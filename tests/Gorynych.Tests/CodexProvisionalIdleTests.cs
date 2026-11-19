using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Codex.Protocol;

namespace Gorynych.Tests;

[TestClass]
public sealed class CodexProvisionalIdleTests
{
    static CodexThread Thread() => new("thread", "cwd", new CodexMap.CodexSettings("model", "low", null, null, "user"));

    [TestMethod]
    public void OnlyNamedMatchingSuccessfulTurnCanHaveProvisionalIdle()
    {
        var thread = Thread(); var turn = thread.Begin(1)!;
        thread.ObserveProvisionalIdle(); thread.Named("one");
        thread.Completed(new Turn { Id = "one", Status = TurnStatus.Completed });
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "one"), "idle before named turn is not evidence");
        thread.End(turn);
        var next = thread.Begin(2)!; thread.Named("two"); thread.ObserveProvisionalIdle();
        thread.Completed(new Turn { Id = "two", Status = TurnStatus.Completed });
        Assert.IsTrue(thread.HasProvisionalIdleFor(next, "two"));
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "two"));
        Assert.IsFalse(thread.HasProvisionalIdleFor(next, "one"));
        thread.End(next); Assert.IsFalse(thread.HasProvisionalIdleFor(next, "two"));
    }

    [TestMethod]
    [DataRow("wrong")]
    [DataRow("failed")]
    [DataRow("interrupted")]
    [DataRow("cancel")]
    [DataRow("activity")]
    [DataRow("unloaded")]
    public void ConflictingEventsNeverProveTheTurn(string conflict)
    {
        var thread = Thread(); var turn = thread.Begin(1)!; thread.Named("turn"); thread.ObserveProvisionalIdle();
        if (conflict == "cancel") thread.Stop(2);
        if (conflict == "activity") thread.InvalidateProvisionalIdle();
        if (conflict == "unloaded") thread.Loaded = false;
        thread.Completed(new Turn { Id = conflict == "wrong" ? "other" : "turn", Status = conflict switch
        { "failed" => TurnStatus.Failed, "interrupted" => TurnStatus.Interrupted, _ => TurnStatus.Completed } });
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "turn")); thread.End(turn);
    }
    [TestMethod]
    public void AFailedFirstCompletionCannotBeReplacedByLateSuccess()
    {
        var thread = Thread(); var turn = thread.Begin(1)!; thread.Named("turn"); thread.ObserveProvisionalIdle();
        thread.Completed(new Turn { Id = "turn", Status = TurnStatus.Failed });
        thread.Completed(new Turn { Id = "turn", Status = TurnStatus.Completed });
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "turn")); thread.End(turn);
    }
    [TestMethod]
    public void AlreadyReceivedInterveningEventInvalidatesQueuedIdleAndRetiredWitness()
    {
        var thread = Thread(); var turn = thread.Begin(1)!; thread.Named("turn");
        var idleArrival = thread.CaptureProvisionalIdleIngress();
        thread.InvalidateProvisionalIdle(); // ingress arrives while the idle handler is queued
        thread.ObserveProvisionalIdle(idleArrival);
        thread.Completed(new Turn { Id = "turn", Status = TurnStatus.Completed });
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "turn")); thread.End(turn);
        var second = thread.Begin(2)!; thread.Named("second"); thread.ObserveProvisionalIdle(thread.CaptureProvisionalIdleIngress());
        thread.Completed(new Turn { Id = "second", Status = TurnStatus.Completed });
        var witness = thread.CaptureProvisionalWitness(second, "prompt", "second"); Assert.IsNotNull(witness);
        thread.End(second); Assert.IsTrue(thread.ValidateProvisionalWitness(witness));
        thread.Stop(3); Assert.IsFalse(thread.ValidateProvisionalWitness(witness));
        var third = thread.Begin(3)!; thread.End(third); Assert.IsFalse(thread.ValidateProvisionalWitness(witness));
    }
    [TestMethod]
    public void IdleReceivedBeforeNamingCannotAttachToLaterNamedTurn()
    {
        var thread = Thread(); var turn = thread.Begin(1)!;
        var beforeNaming = thread.CaptureProvisionalIdleIngress();
        Assert.IsNull(beforeNaming);
        thread.Named("named-later"); thread.ObserveProvisionalIdle(beforeNaming);
        thread.Completed(new Turn { Id = "named-later", Status = TurnStatus.Completed });
        Assert.IsFalse(thread.HasProvisionalIdleFor(turn, "named-later")); thread.End(turn);
    }
}
