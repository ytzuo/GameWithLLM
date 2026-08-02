# GameWithLLM Agent Runtime

This UPM package owns the protocol-independent contracts used directly by the
production Unity project. It compiles as the `GameWithLLM.AgentRuntime`
assembly and currently provides contracts, not a complete network client:

- `IAgentEntity` and `IGameObjectAgentEntity`
- `IAgentTool`, `AgentToolDescriptor`, and `AgentToolContext`
- `AgentToolResult`
- `RuntimeCommand` and `RuntimeManifest`
- `IRuntimeTransport`
- `IAgentMainThreadScheduler`
- `AgentResponseEvent`

The project under `Assets` supplies the current A2A client, Runtime Gateway
transport, registries, dispatcher, UI, and game-specific adapters. Those
implementations are not shipped by this package. They must not define parallel
command, result, manifest, tool, entity, or transport contracts.

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

The embedded package is validated by the repository's Unity `6000.3.19f1`
project. See the root `ARCHITECTURE.md` for the current network protocols and
module boundaries.
