using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Codex.Protocol;

namespace Gorynych.Tests;

[TestClass]
public sealed class GuardedNotificationTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    static JsonObject Frame(string state) => new() { ["_meta"] = new JsonObject { ["state"] = state, ["revision"] = 7 } };
    static (CodexThread Thread, CodexProvisionalIdleWitness Witness) Candidate()
    {
        var thread = new CodexThread("session", "cwd", new CodexMap.CodexSettings("model", "low", null, null, "user"));
        var turn = thread.Begin(1)!; thread.Named("native"); thread.ObserveProvisionalIdle();
        thread.Completed(new Turn { Id = "native", Status = TurnStatus.Completed });
        var witness = thread.CaptureProvisionalWitness(turn, "prompt", "native")!;
        thread.End(turn); return (thread, witness);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task IngressBeforeGuardSuppressesConfirmationAndOrdinaryFrameArrives()
    {
        var link = new DuplexLink(); var (input, _) = link.ClientEnd; var output = new BarrierStream();
        await using var rpc = new JsonRpcConnection(input, output, "guarded-test");
        var (thread, witness) = Candidate(); var invoked = false;
        var ordinary = rpc.SendNotificationAsync("_ordinary", Frame("ordinary"));
        try
        {
            await output.Writing.Task.WaitAsync(Wait);
            var confirmation = rpc.SendGuardedNotificationAsync("_confirmation", Frame("confirmed"), () =>
            { invoked = true; return thread.CommitProvisionalWitness(witness); });
            Assert.IsNull(thread.InvalidateProvisionalIdle()); // ingress before the blocked writer releases the gate
            output.Release.TrySetResult();
            await ordinary.WaitAsync(Wait);
            Assert.IsFalse(await confirmation.WaitAsync(Wait)); Assert.IsTrue(invoked);
            var frames = output.Frames(); Assert.AreEqual(1, frames.Length);
            Assert.AreEqual("_ordinary", (string?)frames[0]["method"]);
        }
        finally { output.Release.TrySetResult(); }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RevocationAfterCommitIsWrittenAfterBlockedConfirmation()
    {
        var link = new DuplexLink(); var (input, _) = link.ClientEnd; var output = new BarrierStream();
        await using var rpc = new JsonRpcConnection(input, output, "guarded-test");
        var (thread, witness) = Candidate();
        Assert.IsFalse(thread.CommitProvisionalWitness(witness with { PromptId = "other" }), "another prompt cannot reuse the same epoch and revision");
        var confirmation = rpc.SendGuardedNotificationAsync("_confirmation", Frame("confirmed"), () => thread.CommitProvisionalWitness(witness));
        try
        {
            await output.Writing.Task.WaitAsync(Wait); // commit has completed; physical write is still blocked
            var revoked = thread.InvalidateProvisionalIdle();
            Assert.AreEqual(witness, revoked, "ingress sees the exact committed witness");
            var revocation = rpc.SendNotificationAsync("_confirmation", Frame("revoked"));
            output.Release.TrySetResult();
            Assert.IsTrue(await confirmation.WaitAsync(Wait)); await revocation.WaitAsync(Wait);
            var frames = output.Frames(); Assert.AreEqual(2, frames.Length);
            Assert.AreEqual("confirmed", (string?)frames[0]["params"]!["_meta"]!["state"]);
            Assert.AreEqual("revoked", (string?)frames[1]["params"]!["_meta"]!["state"]);
            Assert.AreEqual((long?)frames[0]["params"]!["_meta"]!["revision"], (long?)frames[1]["params"]!["_meta"]!["revision"]);
        }
        finally { output.Release.TrySetResult(); }
    }

    sealed class BarrierStream : Stream
    {
        readonly MemoryStream bytes = new();
        int writes;
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref writes) == 1) { Writing.TrySetResult(); await Release.Task.WaitAsync(ct); }
            lock (bytes) bytes.Write(buffer.Span);
        }
        public JsonNode[] Frames()
        {
            string text; lock (bytes) text = Encoding.UTF8.GetString(bytes.ToArray());
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!).ToArray();
        }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
