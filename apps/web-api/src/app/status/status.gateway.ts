import { Logger } from '@nestjs/common';
import {
  OnGatewayConnection,
  OnGatewayDisconnect,
  WebSocketGateway,
  WebSocketServer,
} from '@nestjs/websockets';
import { Namespace, Socket } from 'socket.io';
import { isAllowedOrigin } from '../cors-origin';
import { StatusService } from './status.service';

const POLL_INTERVAL_MS = 10000;

// Only "today" is ever live — a past date is already static once fetched over REST (see
// CaptureStatusPage.tsx), so there is nothing to broadcast for it. One global channel, no rooms:
// every connected client is (by definition) watching today, so they all get the same payload.
@WebSocketGateway({
  namespace: '/status',
  cors: {
    origin: (origin: string | undefined, callback: (err: Error | null, allow?: boolean) => void) => {
      if (isAllowedOrigin(origin)) return callback(null, true);
      return callback(new Error('Not allowed by CORS'));
    },
    credentials: true,
  },
})
export class StatusGateway implements OnGatewayConnection, OnGatewayDisconnect {
  private readonly logger = new Logger(StatusGateway.name);

  // Typed as Namespace, not Server: NestJS injects the /status namespace instance here (because
  // the gateway declares `namespace: '/status'`), and Server's own `.sockets` getter is overridden
  // for Socket.IO v2 back-compat to return the *default* namespace, not this one's client map.
  @WebSocketServer()
  private server!: Namespace;

  private pollTimer?: NodeJS.Timeout;

  constructor(private readonly statusService: StatusService) {}

  async handleConnection(client: Socket): Promise<void> {
    if (this.server.sockets.size === 1) {
      this.pollTimer = setInterval(() => this.pollAndBroadcast(), POLL_INTERVAL_MS);
    }

    // Send the current snapshot immediately rather than waiting up to POLL_INTERVAL_MS for the
    // next shared tick, so a newly opened page isn't blank for several seconds.
    try {
      client.emit('captureStatus', await this.statusService.getCaptureStatus({}));
    } catch (error) {
      this.logger.warn(`[StatusGateway] Initial snapshot failed: ${error}`);
    }
  }

  handleDisconnect(): void {
    if (this.server.sockets.size === 0 && this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = undefined;
    }
  }

  private async pollAndBroadcast(): Promise<void> {
    try {
      const status = await this.statusService.getCaptureStatus({});
      this.server.emit('captureStatus', status);
    } catch (error) {
      this.logger.warn(`[StatusGateway] Poll failed, skipping this tick: ${error}`);
    }
  }
}
