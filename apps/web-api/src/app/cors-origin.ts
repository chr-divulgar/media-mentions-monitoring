const ALLOWED_ORIGINS = [
  'https://rpt-monitoreo.github.io',
  'http://localhost:4300',
  'http://localhost:4200',
];

// Shared between the HTTP CORS setup (main.ts) and the WebSocket gateway's own handshake CORS
// (StatusGateway) — a WS upgrade doesn't go through Nest's enableCors, so it needs this checked
// again independently, and both need to stay in sync.
export function isAllowedOrigin(origin: string | undefined): boolean {
  if (!origin) return true;
  if (ALLOWED_ORIGINS.includes(origin)) return true;
  return /^https:\/\/[a-zA-Z0-9-]+\.trycloudflare\.com$/.test(origin);
}
