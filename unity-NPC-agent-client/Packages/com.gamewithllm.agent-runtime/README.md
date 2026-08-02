# GameWithLLM Agent Runtime

This package owns the protocol-independent runtime contracts used by production
Unity code:

- `IAgentEntity` and `IGameObjectAgentEntity`
- `IAgentTool`, `AgentToolDescriptor`, and `AgentToolContext`
- `AgentToolResult`
- `RuntimeCommand` and `RuntimeManifest`
- `IRuntimeTransport`
- `AgentResponseEvent`

The project under `Assets` implements game-specific adapters on top of these
contracts. It must not define parallel command, result, manifest, tool, entity,
or transport interfaces.

The production flow is:

```text
IRuntimeTransport
  -> RuntimeCommand
  -> CommandDispatcher / IAgentEntity
  -> IAgentTool
  -> AgentToolResult
  -> IRuntimeTransport
```

`RuntimeGatewayClient` is the WebSocket/WSS transport implementation.
`NpcEntity` and `NpcTool<TArgs>` are Warehouse-specific adapters. A different
game can provide other entity and tool implementations without changing the
transport or orchestration pipeline.
