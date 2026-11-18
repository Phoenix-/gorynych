using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

[TestClass]
public sealed class ClaudeSubagentRunTests
{
    [TestMethod]
    public void LateOldRunCannotEndNewRun()
    {
        var task = new ClaudeSubagentRun();
        Assert.IsTrue(task.Start("001"));
        Assert.IsTrue(task.Start("002"));
        Assert.IsFalse(task.Finish("001", "completed", "old").Accepted);
        Assert.IsFalse(task.Finish(null, "completed", "unknown").Accepted);
        var end = task.Finish("002", "completed", "new");
        Assert.IsTrue(end.Accepted);
        Assert.AreEqual("new", end.Fallback);
        Assert.IsFalse(task.Finish("002", "completed", "duplicate").Accepted);
    }

    [TestMethod]
    public void DirectAssistantSuppressesFallbackButNextRunCanProduceOne()
    {
        var task = new ClaudeSubagentRun();
        Assert.IsTrue(task.Start("001"));
        task.Answered();
        var first = task.Finish("001", "completed", "already shown");
        Assert.IsTrue(first.Accepted);
        Assert.IsNull(first.Fallback);
        Assert.IsTrue(task.Start("002"));
        Assert.AreEqual("new result", task.Finish("002", "completed", "new result").Fallback);
    }

    [TestMethod]
    public void UnidentifiedRunsDoNotGuessAtResumptions()
    {
        var task = new ClaudeSubagentRun();
        Assert.IsTrue(task.Start(null));
        Assert.IsFalse(task.Start(null));
        Assert.IsTrue(task.Finish(null, "stopped", "not a completion").Accepted);
        Assert.IsFalse(task.Start("002"));
    }

    [TestMethod]
    public void MissingRunIdUsesOnlyUnambiguousTaskOrTool()
    {
        var task = new ClaudeSubagentRun();
        Assert.IsTrue(task.Start(null, "first"));
        Assert.IsTrue(task.Finish(null, "completed", "first").Accepted);
        Assert.IsTrue(task.Start("002", "second"));
        Assert.IsFalse(task.Finish(null, "completed", "ambiguous").Accepted);
        Assert.IsFalse(task.Finish(null, "completed", "old", "first").Accepted);
        Assert.IsTrue(task.Finish(null, "completed", "second", "second").Accepted);
        var single = new ClaudeSubagentRun();
        Assert.IsTrue(single.Start("001", "only"));
        Assert.IsTrue(single.Finish(null, "completed", "restored").Accepted);
    }
}
