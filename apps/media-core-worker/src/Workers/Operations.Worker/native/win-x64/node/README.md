Vendored portable Node.js runtime (win-x64), for the WhatsApp sidecar only.

`node.exe` here is copied verbatim from the official Node.js LTS win-x64 zip distribution
(https://nodejs.org/dist/) — no installer, no PATH dependency. Only `node.exe` + `LICENSE` are
kept; npm/npx/corepack are not needed because the sidecar's own dependencies are already vendored
in `../../../sidecar-whatsapp/node_modules/` (see that folder), not installed at deploy time.

At build/publish time, `Operations.Worker.csproj` copies this file to:
- runtimes/win-x64/node/node.exe

`WhatsAppSidecarProcessSupervisor` resolves Node only from that runtime folder (no PATH fallback)
and launches `node.exe "<publish-dir>/sidecar-whatsapp/server.js"` as a child process — same
"vendored, no machine-level dependency, fail fast if missing" contract as ../README.md documents
for ffmpeg.

To update: download `node-vX.Y.Z-win-x64.zip` from https://nodejs.org/dist/, extract, and replace
`node.exe`/`LICENSE` here. Match the Node.js major version the sidecar's `package.json`
`engines` field (if any) expects.
