# ACP types

`Gorynych.Acp` carries the Agent Client Protocol v1 types and a JSON-RPC 2.0 connection.

## Why generated

There is no official ACP SDK for .NET (the agentclientprotocol organization ships Rust,
TypeScript, Python, Kotlin and Java). As of November 2026 the community packages on NuGet
either stopped a year ago (`nuskey8/acp-csharp`) or track older schema releases
(`timxx/dotacp`, `vezaynk/acpkit` at schema v1.21). So the types are generated from the
official schema release, vendored under `schema/acp/<version>/` (`schema.json`,
`meta.json`, both unchanged from
https://github.com/agentclientprotocol/agent-client-protocol/releases).

To move to a new schema release, add its folder and run:

```
dotnet run --project tools/Gorynych.AcpGen -- . v1.29.1
```

The output, `src/Gorynych.Acp/Generated/Acp.g.cs`, is checked in.

## Mapping rules

- An object definition becomes a sealed class; required properties are non-nullable,
  optional ones nullable and omitted when null. `_meta` becomes `Meta` (`JsonObject?`).
- A string enumeration (`oneOf`/`anyOf` of `const` strings) becomes an *open* enum: a
  `readonly record struct` with one static property per known value. Unknown values
  deserialize instead of failing, because ACP adds values between releases.
- A discriminated union (`discriminator.propertyName`, or variants with a `const` property)
  becomes an abstract base class with a generated converter:
  - a variant that is just a reference to a definition used nowhere else makes that
    definition the derived class (`TextContent : ContentBlock`, `ToolCall : SessionUpdate`);
  - other variants get a class named base + variant (`SessionUpdateAgentMessageChunk`);
  - a variant without the discriminator (`McpServerStdio`) is chosen when the property is absent;
  - an unrecognized discriminator yields `Base.Unknown`, which keeps the raw JSON and writes
    it back unchanged, so a conductor can forward variants it does not know.
- An untagged union of objects is flattened into one class with every variant's properties.
- Anything else (`RequestId`, `ElicitationContentValue`, extension payloads) stays `JsonNode`.
- All metadata is source-generated (`AcpJsonContext`), so the library is Native AOT
  compatible and reflection-based serialization is disabled repository-wide.

## JSON-RPC

`JsonRpcConnection` speaks JSON-RPC 2.0 over any duplex byte stream, one message per line,
which is the ACP stdio framing. Requests from the peer run concurrently; notifications are
handled in arrival order before a later response completes, so a prompt's
`session/update` notifications always precede its result. Cancelling an outgoing request
sends `$/cancel_request`; an incoming one cancels the handler's token. `DuplexLink` joins
an in-process client and agent with two in-memory pipes.
