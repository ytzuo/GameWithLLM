# Warehouse Demo

The repository SampleScene is the warehouse sample. Its NPC, NavMesh, target,
inventory, and save-game code consumes the protocol-independent runtime
contracts from this package. Conversations use the A2A adapter, while tool
execution always uses the outbound Runtime Gateway transport. Local development
connects to the same Gateway on loopback.
