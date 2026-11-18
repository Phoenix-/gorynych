using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

internal sealed partial class ReportStore
{
    // Hold the same cross-process manifest lock used for eviction until the assignment
    // transition has durably committed. No assignment-store code acquires this lock.
    internal T WithAssignmentReport<T>(string id, string actor, string parent, string child,
        string assignmentId, string generation, ReportCause cause, Func<T> commit)
    {
        ValidateId(id);
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), id);
            var identity = IdentityFrom(item["identity"]);
            if ((actor != identity.Child && actor != identity.Parent) || identity.Parent != parent || identity.Child != child ||
                identity.AssignmentId != assignmentId || identity.AssignmentGeneration != generation || identity.Cause != cause || (string?)item["status"] == "reserved")
                throw new InvalidOperationException("Report is not a published result for this assignment and its participants");
            using var body = new FileStream(BodyPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = (long?)item["bodyBytes"] ?? throw new InvalidDataException("Missing report body size");
            if (length < 0 || length > bodyLimit || body.Length != length ||
                Convert.ToHexString(SHA256.HashData(body)) != (string?)item["digest"])
                throw new InvalidDataException("Report body is unavailable or corrupt");
            return commit();
        }
    }
}
