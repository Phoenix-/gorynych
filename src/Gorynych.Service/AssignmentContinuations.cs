using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;

namespace Gorynych.Service;

// Caller holds the owner's store lock. Receipt IDs share the assignment operation namespace.
internal static class AssignmentContinuations
{
    internal static (bool Changed, JsonObject Result) Update(JsonObject state, string actor, string generation,
        long revision, string operation, string action, JsonObject? target)
    {
        if ((string?)state["child"] != actor || (string?)state["generation"] != generation)
            throw new InvalidOperationException("Assignment unavailable or generation conflict");
        var selector = (string?)target?["selector"];
        var selectorFingerprint = selector is null ? null : Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(selector)));
        var ops = state["operations"]!.AsObject();
        if (ops[operation] is JsonObject prior)
        {
            if ((string?)prior["kind"] != "continuation" || (string?)prior["actor"] != actor ||
                (long?)prior["expectedRevision"] != revision || (string?)prior["action"] != action ||
                (string?)prior["messageId"] != (string?)target?["messageId"] ||
                (string?)prior["targetKind"] != (string?)target?["kind"] ||
                (string?)prior["targetSource"] != (string?)target?["source"] ||
                (string?)prior["targetId"] != (string?)target?["assignmentId"] ||
                (string?)prior["targetGeneration"] != (string?)target?["generation"] ||
                (string?)prior["taskId"] != (string?)target?["taskId"] ||
                (string?)prior["nativeType"] != (string?)target?["nativeType"] ||
                (string?)prior["selectorFingerprint"] != selectorFingerprint)
                throw new InvalidOperationException("Operation ID is already used");
            return (false, prior["result"]!.DeepClone().AsObject());
        }
        if ((long?)state["revision"] != revision) throw new InvalidOperationException("Assignment revision conflict");
        if ((string?)state["status"] is "completed" or "failed" or "cancelled")
            throw new InvalidOperationException("Assignment is already terminal");
        if (action == "register")
        {
            if (state["continuation"] is not null) throw new InvalidOperationException("Withdraw the existing continuation first");
            if ((bool?)target?["confirmed"] != true) throw new InvalidOperationException("Continuation target unavailable");
            var retained = target!.DeepClone().AsObject(); retained.Remove("confirmed"); retained.Remove("selector");
            state["continuation"] = retained;
        }
        else if (action == "withdraw")
        {
            if (state["continuation"] is null) throw new InvalidOperationException("No active continuation");
            state.Remove("continuation");
        }
        else throw new ArgumentException("Unknown continuation action");
        state["revision"] = revision + 1;
        var result = state.DeepClone().AsObject(); result.Remove("operations"); result.Remove("fingerprint");
        while (ops.Count >= 64)
            ops.Remove(ops.OrderBy(p => (long)p.Value!["expectedRevision"]!).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key);
        ops[operation] = new JsonObject { ["kind"] = "continuation", ["actor"] = actor, ["expectedRevision"] = revision,
            ["action"] = action, ["messageId"] = (string?)target?["messageId"], ["targetKind"] = (string?)target?["kind"],
            ["targetSource"] = (string?)target?["source"], ["targetId"] = (string?)target?["assignmentId"],
            ["targetGeneration"] = (string?)target?["generation"], ["taskId"] = (string?)target?["taskId"], ["nativeType"] = (string?)target?["nativeType"], ["selectorFingerprint"] = selectorFingerprint, ["result"] = result.DeepClone() };
        return (true, result);
    }
}
