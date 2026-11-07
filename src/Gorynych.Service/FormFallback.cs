using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// Puts a form (<c>elicitation/create</c>) to a client that declared no form support, such as
/// a plain ACP client, as one <c>session/request_permission</c> per field with the choices as
/// options. Only choice fields can be asked this way; a form with free text is declined, so
/// the agent goes on (Claude then asks in plain text) instead of waiting forever.
/// </summary>
internal static class FormFallback
{
    public const string SkipOptionId = "skip";

    public static async Task<JsonNode?> AskAsync(ClientConnection client, JsonRpcRequest request, CancellationToken ct)
    {
        if (request.ParamsAs<CreateElicitationRequest>() is not ElicitationFormMode form)
            return Respond(new CreateElicitationResponseDecline());
        var fields = (form.RequestedSchema.Properties ?? []).ToList();
        var questions = new List<(string Key, string Title, List<EnumOption> Choices, bool Many)>();
        foreach (var (key, schema) in fields)
        {
            var question = schema switch
            {
                StringPropertySchema s when s.OneOf is { Count: > 0 } options => (s.Description ?? s.Title, options, false),
                StringPropertySchema s when s.Enum is { Count: > 0 } values => (s.Description ?? s.Title, values.Select(v => new EnumOption { Const = v, Title = v }).ToList(), false),
                MultiSelectPropertySchema m when m.Items is TitledMultiSelectItems { AnyOf.Count: > 0 } t => (m.Description ?? m.Title, t.AnyOf, true),
                MultiSelectPropertySchema m when m.Items is StringMultiSelectItems { Enum.Count: > 0 } e => (m.Description ?? m.Title, e.Enum.Select(v => new EnumOption { Const = v, Title = v }).ToList(), true),
                BooleanPropertySchema b => (b.Description ?? b.Title, [new EnumOption { Const = "true", Title = "Yes" }, new EnumOption { Const = "false", Title = "No" }], false),
                _ => ((string?)null, (List<EnumOption>?)null, false),
            };
            if (question.Item2 is null)
            {
                ServiceLog.Info($"{client.Rpc.Name}: cannot show form field '{key}' without forms; declining the form");
                return Respond(new CreateElicitationResponseDecline());
            }
            questions.Add((key, question.Item1 ?? (fields.Count == 1 ? form.Message : key), question.Item2, question.Item3));
        }

        var content = new Dictionary<string, JsonNode?>();
        foreach (var (key, title, choices, many) in questions)
        {
            var options = choices.Select((c, i) => new PermissionOption
            {
                OptionId = $"choice:{i}",
                Name = c.Description is { Length: > 0 } d ? $"{c.Title} — {d}" : c.Title,
                Kind = PermissionOptionKind.AllowOnce,
            }).ToList();
            options.Add(new PermissionOption { OptionId = SkipOptionId, Name = "Skip", Kind = PermissionOptionKind.RejectOnce });
            var asked = new RequestPermissionRequest
            {
                SessionId = form.SessionId ?? "",
                ToolCall = new ToolCallUpdate
                {
                    ToolCallId = form.ToolCallId ?? $"form-{key}",
                    Title = many ? $"{title} (one choice here)" : title,
                    Kind = ToolKind.Other,
                    Status = ToolCallStatus.Pending,
                },
                Options = options,
            };
            var response = await client.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(
                AcpMethods.Client.SessionRequestPermission, asked, ct);
            if (response.Outcome is not SelectedPermissionOutcome { OptionId: var id } || !id.StartsWith("choice:", StringComparison.Ordinal))
                return Respond(response.Outcome is SelectedPermissionOutcome ? new CreateElicitationResponseDecline() : new CreateElicitationResponseCancel());
            var choice = choices[int.Parse(id.AsSpan(7))];
            content[key] = many ? new JsonArray((JsonNode)choice.Const)
                : fields.First(f => f.Key == key).Value is BooleanPropertySchema ? JsonValue.Create(choice.Const == "true")
                : choice.Const;
        }
        return Respond(new ElicitationAcceptAction { Content = content });
    }

    static JsonNode? Respond(CreateElicitationResponse response) => AcpJson.ToNode(response);
}
