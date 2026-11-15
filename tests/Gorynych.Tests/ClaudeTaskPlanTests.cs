using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// Claude's native task tools reduced into ACP v1's full-replacement plan: the shapes recorded from
/// Claude Code 2.1.302 (TaskCreate and TaskUpdate) and the legacy TodoWrite snapshot, including
/// aliases, partial updates, deletions, failures and malformed payloads.
/// </summary>
[TestClass]
public sealed class ClaudeTaskPlanTests
{
    static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    static string[] Entries(Plan plan) =>
        plan.Entries.Select(e => $"{e.Status.Value}:{e.Priority.Value}:{e.Content}").ToArray();

    static Plan? Result(ClaudeTaskPlan plan, string call, string tool, JsonNode? structured, bool failed = false) =>
        plan.ObserveToolResult(call, tool, failed, structured);

    [TestMethod]
    public void TodoWriteSnapshot_BecomesAFullPlan_AndAnEmptySnapshotClearsIt()
    {
        var plan = new ClaudeTaskPlan();
        plan.ObserveToolUse("call-todo", "TodoWrite", J("""
            {"todos":[
              {"content":"One","status":"pending","activeForm":"Doing one"},
              {"content":"Two","status":"in_progress","activeForm":"Doing two"},
              {"content":"Three","status":"completed","activeForm":"Done three"}
            ]}
            """));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count, "the input alone publishes nothing");

        var applied = Result(plan, "call-todo", "TodoWrite", J("""
            {"oldTodos":[],"newTodos":[
              {"content":"One","status":"pending","activeForm":"Doing one"},
              {"content":"Two","status":"in_progress","activeForm":"Doing two"},
              {"content":"Three","status":"completed","activeForm":"Done three"}
            ]}
            """));
        CollectionAssert.AreEqual(new[] { "pending:medium:One", "in_progress:medium:Doing two", "completed:medium:Three" }, Entries(applied!));

        plan.ObserveToolUse("call-clear", "TodoWrite", J("""{"todos":[]}"""));
        var cleared = Result(plan, "call-clear", "TodoWrite", J("""{"oldTodos":[],"newTodos":[]}"""));
        Assert.IsNotNull(cleared);
        Assert.AreEqual(0, cleared!.Entries.Count, "an empty native snapshot is an empty plan");
    }

    [TestMethod]
    public void TodoWriteResult_WinsOverItsInput_AndAFailedSnapshotChangesNothing()
    {
        var plan = new ClaudeTaskPlan();
        plan.ObserveToolUse("call-a", "TodoWrite", J("""{"todos":[{"content":"Input","status":"pending"}]}"""));
        var applied = Result(plan, "call-a", "TodoWrite", J("""{"oldTodos":[],"newTodos":[{"content":"Result","status":"completed"}]}"""));
        CollectionAssert.AreEqual(new[] { "completed:medium:Result" }, Entries(applied!));

        plan.ObserveToolUse("call-b", "TodoWrite", J("""{"todos":[{"content":"Nope","status":"pending"}]}"""));
        Assert.IsNull(Result(plan, "call-b", "TodoWrite", J("""{"oldTodos":[{"content":"Result","status":"completed"}],"newTodos":[{"content":"Nope","status":"pending"}]}"""), failed: true));
        Assert.AreEqual(1, plan.Snapshot().Entries.Count, "a failed call leaves the plan as it was");
        Assert.AreEqual("Result", plan.Snapshot().Entries[0].Content);
    }

    [TestMethod]
    public void TaskCreate_IsAppliedOnlyFromItsResult_AndUsesTheNativeTaskId()
    {
        var plan = new ClaudeTaskPlan();
        plan.ObserveToolUse("toolu_1", "TaskCreate", J("""{"subject":"Draft","activeForm":"Drafting"}"""));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count, "the assigned task ID is not in the input");

        var applied = Result(plan, "toolu_1", "TaskCreate", J("""{"task":{"id":"7","subject":"Draft"}}"""));
        CollectionAssert.AreEqual(new[] { "pending:medium:Draft" }, Entries(applied!));
        Assert.AreNotEqual("toolu_1", applied!.Entries[0].Content, "a tool call ID is not a task ID");

        plan.ObserveToolUse("toolu_2", "TaskCreate", J("""{"subject":"Other"}"""));
        Result(plan, "toolu_2", "TaskCreate", J("""{"task":{"id":"8","subject":"Other"}}"""));
        plan.ObserveToolUse("toolu_3", "TaskUpdate", J("""{"id":"8","status":"completed"}"""));
        var updated = Result(plan, "toolu_3", "TaskUpdate", J("""
            {"success":true,"taskId":"8","updatedFields":["status"],"statusChange":{"from":"pending","to":"completed"}}
            """));
        CollectionAssert.AreEqual(new[] { "pending:medium:Draft", "completed:medium:Other" }, Entries(updated!),
            "the update names the native task ID, not the tool call that created it");
    }

    [TestMethod]
    public void TaskUpdate_AppliesPartialSubjectAndStatus_AndDeletionRemovesOnlyThatTask()
    {
        var plan = new ClaudeTaskPlan();
        Create(plan, "a", "1", "First");
        Create(plan, "b", "2", "Second");

        plan.ObserveToolUse("u1", "TaskUpdate", J("""{"task_id":"2","status":"in_progress"}"""));
        var started = Result(plan, "u1", "TaskUpdate", J("""
            {"success":true,"taskId":"2","updatedFields":["status"],"statusChange":{"from":"pending","to":"in_progress"}}
            """));
        Assert.AreEqual("in_progress:medium:Working on Second", Entries(started!)[1], "the active form names the work in progress");

        plan.ObserveToolUse("u2", "TaskUpdate", J("""{"taskId":"2","subject":"Second renamed","activeForm":"Renaming"}"""));
        var renamed = Result(plan, "u2", "TaskUpdate", J("""
            {"success":true,"taskId":"2","updatedFields":["subject","activeForm"]}
            """));
        Assert.AreEqual("in_progress:medium:Renaming", Entries(renamed!)[1], "the active form names the work in progress");

        plan.ObserveToolUse("u3", "TaskUpdate", J("""{"taskId":"2","status":"deleted"}"""));
        var deleted = Result(plan, "u3", "TaskUpdate", J("""
            {"success":true,"taskId":"2","updatedFields":["deleted"],"statusChange":{"from":"in_progress","to":"deleted"}}
            """));
        CollectionAssert.AreEqual(new[] { "pending:medium:First" }, Entries(deleted!));

        plan.ObserveToolUse("u4", "TaskUpdate", J("""{"taskId":"1","status":"deleted"}"""));
        var empty = Result(plan, "u4", "TaskUpdate", J("""
            {"success":true,"taskId":"1","updatedFields":["deleted"],"statusChange":{"from":"pending","to":"deleted"}}
            """));
        Assert.AreEqual(0, empty!.Entries.Count, "removing the last task clears the plan");
    }

    [TestMethod]
    public void TaskUpdate_WithAnUnknownOrFailedOutcome_DoesNotInventANewStatus()
    {
        var plan = new ClaudeTaskPlan();
        Create(plan, "a", "1", "First");

        plan.ObserveToolUse("u1", "TaskUpdate", J("""{"taskId":"1","status":"blocked"}"""));
        var unknown = Result(plan, "u1", "TaskUpdate", J("""{"success":true,"taskId":"1","updatedFields":["status"],"statusChange":{"from":"pending","to":"blocked"}}"""));
        CollectionAssert.AreEqual(new[] { "pending:medium:First" }, Entries(unknown!), "only native pending/in_progress/completed are ACP statuses");

        plan.ObserveToolUse("u2", "TaskUpdate", J("""{"taskId":"1","status":"completed"}"""));
        Assert.IsNull(Result(plan, "u2", "TaskUpdate", J("""{"success":false,"taskId":"1","updatedFields":["status"],"error":"no"}""")));
        Assert.IsNull(Result(plan, "u2", "TaskUpdate", J("""{"success":true,"taskId":"1","updatedFields":["status"]}"""), failed: true));
        Assert.AreEqual("pending", plan.Snapshot().Entries[0].Status.Value, "a failure or refusal changes nothing");

        // The result's statusChange is authoritative: its unknown target is not replaced by the input's completed.
        plan.ObserveToolUse("u3", "TaskUpdate", J("""{"taskId":"1","status":"completed"}"""));
        var conflict = Result(plan, "u3", "TaskUpdate", J("""
            {"success":true,"taskId":"1","updatedFields":["status"],"statusChange":{"from":"pending","to":"blocked"}}
            """));
        Assert.AreEqual("pending", conflict!.Entries[0].Status.Value);
    }

    [TestMethod]
    public void FailedCalls_DoNotPublishAnUnappliedPlan()
    {
        var plan = new ClaudeTaskPlan();
        plan.ObserveToolUse("create", "TaskCreate", J("""{"subject":"Not created"}"""));
        Assert.IsNull(Result(plan, "create", "TaskCreate", J("""{"task":{"id":"1","subject":"Not created"}}"""), failed: true));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count);

        plan.ObserveToolUse("todo", "TodoWrite", J("""{"todos":[{"content":"Not applied","status":"pending"}]}"""));
        Assert.IsNull(Result(plan, "todo", "TodoWrite", J("""{"newTodos":[{"content":"Not applied","status":"pending"}]}"""), failed: true));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count);
    }

    [TestMethod]
    public void MalformedPayloads_ChangeNothing_AndDoNotThrow()
    {
        var plan = new ClaudeTaskPlan();
        plan.ObserveToolUse("t1", "TodoWrite", J("""{"todos":"not an array"}"""));
        Assert.IsNull(Result(plan, "t1", "TodoWrite", J("""{"newTodos":"not an array"}""")));
        Assert.IsNull(Result(plan, "t1", "TodoWrite", J("""{"newTodos":[{"content":7,"status":"pending"}]}""")));
        plan.ObserveToolUse("t2", "TaskCreate", J("""{"subject":7}"""));
        Assert.IsNull(Result(plan, "t2", "TaskCreate", J("""{"task":{"id":7,"subject":"No"}}""")));
        plan.ObserveToolUse("t3", "TaskUpdate", J("""{"taskId":[],"status":{}}"""));
        Assert.IsNull(Result(plan, "t3", "TaskUpdate", J("""{"taskId":7,"updatedFields":"status"}""")));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count);

        plan.ObserveToolUse("", "TodoWrite", J("""{"todos":[{"content":"Ignored","status":"pending"}]}"""));
        Assert.IsNull(Result(plan, "", "TodoWrite", J("""{"newTodos":[{"content":"Ignored","status":"pending"}]}""")));
        Assert.AreEqual(0, plan.Snapshot().Entries.Count);
    }

    [TestMethod]
    public void AResultWithoutItsCall_StillPublishesAnAuthoritativeTaskCreateOrDeletion()
    {
        var plan = new ClaudeTaskPlan();
        // A session opened mid-turn: the tool call was before the point the live stream saw.
        var created = plan.ObserveToolResult("unknown-call", "TaskCreate", false, J("""{"task":{"id":"3","subject":"Seen late"}}"""));
        CollectionAssert.AreEqual(new[] { "pending:medium:Seen late" }, Entries(created!));

        var deleted = plan.ObserveToolResult("unknown-delete", "TaskUpdate", false, J("""
            {"success":true,"taskId":"3","updatedFields":["deleted"],"statusChange":{"from":"pending","to":"deleted"}}
            """));
        Assert.AreEqual(0, deleted!.Entries.Count);
    }

    [TestMethod]
    public void ThePlanState_IsBounded()
    {
        var plan = new ClaudeTaskPlan();
        for (var i = 0; i < ClaudeTaskPlan.MaxEntries + 5; i++)
        {
            var call = "c" + i;
            plan.ObserveToolUse(call, "TaskCreate", new JsonObject { ["subject"] = $"Task {i}" });
            plan.ObserveToolResult(call, "TaskCreate", false,
                new JsonObject { ["task"] = new JsonObject { ["id"] = i.ToString(), ["subject"] = $"Task {i}" } });
        }
        Assert.AreEqual(ClaudeTaskPlan.MaxEntries, plan.Snapshot().Entries.Count);
    }

    [TestMethod]
    public void CopyFrom_ReplacesTheStateUsedByTheLiveSession()
    {
        var source = new ClaudeTaskPlan();
        Create(source, "a", "1", "Restored");
        var live = new ClaudeTaskPlan();
        live.CopyFrom(source);
        Assert.AreEqual("Restored", live.Snapshot().Entries.Single().Content);
        source.Reset();
        Assert.AreEqual(1, live.Snapshot().Entries.Count, "the copy is independent");
    }

    static void Create(ClaudeTaskPlan plan, string call, string id, string subject)
    {
        plan.ObserveToolUse(call, "TaskCreate",
            new JsonObject { ["subject"] = subject, ["activeForm"] = $"Working on {subject}" });
        var applied = plan.ObserveToolResult(call, "TaskCreate", false,
            new JsonObject { ["task"] = new JsonObject { ["id"] = id, ["subject"] = subject } });
        Assert.IsNotNull(applied);
    }
}
