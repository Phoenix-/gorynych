using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReportCaptureTests
{
    [TestMethod]
    public void CaptureSurvivesRestartWithToolResetSplitSurrogateAndTerminal()
    {
        var path = Path.Combine(TestDirs.New(), "capture");
        using (var capture = new ReportCapture(path))
        {
            capture.Append("old answer"); capture.ToolCall(); capture.Append(" revised \uD83D");
        }
        using (var capture = new ReportCapture(path))
        {
            capture.Append("\uDE00 end\n"); capture.Seal("failed", "native failure");
            Assert.ThrowsExactly<InvalidOperationException>(() => capture.Append("late"));
        }
        using var recovered = new ReportCapture(path);
        var state = recovered.Snapshot();
        Assert.AreEqual(" revised 😀 end\n", state.Text);
        Assert.IsTrue(state.HasText); Assert.IsTrue(state.Sealed); Assert.IsNull(state.IncompleteReason);
        Assert.AreEqual("failed", state.Outcome); Assert.AreEqual("native failure", state.Error);
    }

    [TestMethod]
    public void FailedPublicationDoesNotMutateCaptureOrSurviveRestart()
    {
        var path = Path.Combine(TestDirs.New(), "capture");
        using (var capture = new ReportCapture(path))
        {
            capture.Append("kept");
            capture.WriteFrame = _ => throw new IOException("injected publication failure");
            Assert.ThrowsExactly<IOException>(() => capture.Append("lost"));
            Assert.AreEqual("kept", capture.Snapshot().Text);
        }
        using var recovered = new ReportCapture(path);
        Assert.AreEqual("kept", recovered.Snapshot().Text);
        Assert.IsFalse(recovered.Snapshot().Sealed);
    }

    [TestMethod]
    public void TornFrameDoesNotAppearAsACompleteCapture()
    {
        var path = Path.Combine(TestDirs.New(), "capture");
        using (var capture = new ReportCapture(path)) capture.Append("kept");
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.WriteByte(1);
        Assert.ThrowsExactly<InvalidDataException>(() => new ReportCapture(path));
    }

    [TestMethod]
    public void EmptyAbsentInvalidAndOverflowAreDistinguished()
    {
        var root = TestDirs.New();
        using var absent = new ReportCapture(Path.Combine(root, "absent"), 4);
        absent.Seal("ended"); Assert.IsFalse(absent.Snapshot().HasText);
        using var empty = new ReportCapture(Path.Combine(root, "empty"), 4);
        empty.Append(""); empty.Seal("ended"); Assert.IsTrue(empty.Snapshot().HasText);
        using var invalid = new ReportCapture(Path.Combine(root, "invalid"), 4);
        invalid.Append("\uD83D");
        Assert.ThrowsExactly<InvalidDataException>(() => invalid.Append("x"));
        invalid.Seal("cancelled", "invalid Unicode observed");
        Assert.IsNotNull(invalid.Snapshot().IncompleteReason);
        Assert.AreEqual("cancelled", invalid.Snapshot().Outcome);
        using var bounded = new ReportCapture(Path.Combine(root, "bounded"), 4);
        bounded.Append("😀");
        Assert.ThrowsExactly<InvalidOperationException>(() => bounded.Append("x"));
        Assert.IsFalse(bounded.Snapshot().Sealed);
        bounded.Seal("ended");
        Assert.AreEqual("😀", bounded.Snapshot().Text);
        Assert.IsNotNull(bounded.Snapshot().IncompleteReason);
    }

    [TestMethod]
    public void ExclusiveOwnerAndCompactionPreserveOnlyLatestCandidate()
    {
        var path = Path.Combine(TestDirs.New(), "owned.capture");
        using (var capture = new ReportCapture(path, 1024))
        {
            Assert.ThrowsExactly<IOException>(() => new ReportCapture(path, 1024));
            for (var i = 0; i < 900; i++) capture.Append("x");
            Assert.AreEqual(new string('x', 900), capture.Snapshot().Text);
            Assert.IsTrue(capture.CompactionCount > 0);
            Assert.IsTrue(capture.CompactionCount <= 40, "small chunks must not cause repeated full-body rewrites near the limit");
        }
        using (var capture = new ReportCapture(path, 1024))
        {
            Assert.AreEqual(new string('x', 900), capture.Snapshot().Text, "compacted output survives reopen");
            capture.ToolCall();
            Assert.AreEqual(0L, new FileInfo(path).Length, "pre-tool frames are discarded");
            capture.Append("final"); capture.Seal("ended");
        }
        using var reopened = new ReportCapture(path, 1024);
        Assert.AreEqual("final", reopened.Snapshot().Text);
        Assert.AreEqual("ended", reopened.Snapshot().Outcome);
    }

    [TestMethod]
    public void OverflowThenToolCanStartANewCompleteCandidate()
    {
        var path = Path.Combine(TestDirs.New(), "overflow.capture");
        using var capture = new ReportCapture(path, 4);
        capture.Append("abcd");
        Assert.ThrowsExactly<InvalidOperationException>(() => capture.Append("x"));
        Assert.IsNotNull(capture.Snapshot().IncompleteReason);
        capture.ToolCall(); capture.Append("new"); capture.Seal("ended");
        var result = capture.Snapshot();
        Assert.AreEqual("new", result.Text); Assert.IsNull(result.IncompleteReason);
    }
    [TestMethod]
    public void OversizedTerminalIsRejectedBeforeSealAndCanBeExplicitlyBounded()
    {
        var path = Path.Combine(TestDirs.New(), "terminal-limit.capture");
        using (var capture = new ReportCapture(path))
        {
            capture.Append("text");
            Assert.ThrowsExactly<ArgumentException>(() => capture.Seal("failed", new string('e', ReportCapture.ErrorBytes + 1)));
            Assert.IsFalse(capture.Snapshot().Sealed);
            capture.Seal("failed", "[error explicitly truncated]");
        }
        using var recovered = new ReportCapture(path);
        Assert.AreEqual("failed", recovered.Snapshot().Outcome);
        Assert.AreEqual("[error explicitly truncated]", recovered.Snapshot().Error);
    }
    [TestMethod]
    public void CompactionPreservesSurrogateSplitAcrossFrames()
    {
        var path = Path.Combine(TestDirs.New(), "split-compaction.capture");
        using (var capture = new ReportCapture(path, 1024))
        {
            for (var i = 0; i < 156; i++) capture.Append("x");
            capture.Append("\uD83D");
            capture.Append("\uDE00"); // forces compaction while the high surrogate is pending
            Assert.IsTrue(capture.CompactionCount > 0);
            capture.Seal("ended");
        }
        using var recovered = new ReportCapture(path, 1024);
        Assert.AreEqual(new string('x', 156) + "😀", recovered.Snapshot().Text);
        Assert.IsNull(recovered.Snapshot().IncompleteReason);
    }

    [TestMethod]
    public void SmallChunkStreamingHasBoundedCompactions()
    {
        var path = Path.Combine(TestDirs.New(), "amortized.capture");
        using var capture = new ReportCapture(path, 8192);
        for (var i = 0; i < 6000; i++) capture.Append("x");
        Assert.IsTrue(capture.CompactionCount > 0);
        Assert.IsTrue(capture.CompactionCount <= 40, "checkpoint rewrites must stay bounded for tiny chunks");
        Assert.AreEqual(6000, capture.Snapshot().Text.Length);
        capture.Seal("ended");
    }
}
