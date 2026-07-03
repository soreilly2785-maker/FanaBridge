# FanaBridge.ItmTool

A standalone console REPL for sending raw col03 ITM display commands directly to a
connected Fanatec wheel, without building or running SimHub/FanaBridge.

Used for manually probing and confirming ITM protocol behaviour (page layouts, handle
numbering, keepalive/kick timing, etc.) against real hardware — see
[docs/reference/protocol.md](../docs/reference/protocol.md) for the resulting protocol
documentation.

**Not part of the SimHub plugin** — this is a developer-only diagnostic tool, built and
run separately (`FanaBridge.ItmTool.csproj`).

> **Important:** close SimHub (or disable "Enable ITM Display" in FanaBridge) before
> using this tool. Both write to the same HID interface and will conflict.

Run it and type `help` for the full command list, or see the top of
[Program.cs](Program.cs) for an overview.
