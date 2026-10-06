'use strict';

// WhatsApp sidecar: wraps Baileys behind a tiny local HTTP contract
// (POST /send, GET /status, GET /qr, GET /groups) so the .NET worker never talks to WhatsApp's protocol
// directly. Launched as a child process by WhatsAppSidecarProcessSupervisor
// (apps/media-core-worker/src/Workers/Operations.Worker/WhatsAppSidecarProcessSupervisor.cs),
// which sets WHATSAPP_SIDECAR_PORT and WHATSAPP_AUTH_STATE_PATH. Never run this standalone in
// production — see the supervisor for lifecycle/restart handling.

const http = require('http');
const fs = require('fs/promises');
const path = require('path');
const pino = require('pino');
const QRCode = require('qrcode');
const {
  default: makeWASocket,
  useMultiFileAuthState,
  DisconnectReason,
} = require('@whiskeysockets/baileys');

const PORT = parseInt(process.env.WHATSAPP_SIDECAR_PORT || '5101', 10);
const AUTH_STATE_PATH = process.env.WHATSAPP_AUTH_STATE_PATH || path.join(__dirname, 'auth-state');
const RECONNECT_DELAY_MS = 5000;

const logger = pino({ level: process.env.WHATSAPP_LOG_LEVEL || 'warn' });

let sock = null;
let latestQr = null;
// 'connected' | 'pending_qr' | 'disconnected' — polled by the .NET worker's
// /whatsapp/status proxy (YouTubeCookiesHttpService.cs) roughly every 30s from the UI.
let connectionStatus = 'pending_qr';

async function startSocket() {
  const { state, saveCreds } = await useMultiFileAuthState(AUTH_STATE_PATH);

  sock = makeWASocket({
    auth: state,
    logger,
    printQRInTerminal: false,
  });

  sock.ev.on('creds.update', saveCreds);
  sock.ev.on('connection.update', handleConnectionUpdate);
}

function handleConnectionUpdate(update) {
  const { connection, lastDisconnect, qr } = update;

  if (qr) {
    latestQr = qr;
    connectionStatus = 'pending_qr';
  }

  if (connection === 'open') {
    latestQr = null;
    connectionStatus = 'connected';
    logger.info('WhatsApp connected.');
  }

  if (connection === 'close') {
    connectionStatus = 'disconnected';
    const statusCode = lastDisconnect?.error?.output?.statusCode;
    const loggedOut = statusCode === DisconnectReason.loggedOut;

    if (loggedOut) {
      // Session invalidated on the phone side (unlinked device, etc.) — clear the stale auth
      // state and re-pair from scratch automatically, so the status tab just shows a fresh QR
      // without anyone needing to restart the worker or press a "log out" button.
      logger.warn('WhatsApp session logged out; clearing auth state and re-pairing.');
      clearAuthStateAndRestart();
    } else if (statusCode === DisconnectReason.restartRequired) {
      // WhatsApp closes the socket once right after a successful QR pairing and expects an
      // immediate reconnect using the now-saved session — this is a normal, guaranteed-to-work
      // part of the handshake, not a failure. Skip the backoff so the status tab doesn't sit on
      // "disconnected" for several seconds right after a successful scan.
      startSocket().catch((error) => logger.error({ error }, 'Post-pairing reconnect failed.'));
    } else {
      setTimeout(() => startSocket().catch((error) => logger.error({ error }, 'Reconnect failed.')), RECONNECT_DELAY_MS);
    }
  }
}

async function clearAuthStateAndRestart() {
  try {
    await fs.rm(AUTH_STATE_PATH, { recursive: true, force: true });
  } catch (error) {
    logger.warn({ error }, 'Failed to clear auth state directory.');
  }

  setTimeout(() => startSocket().catch((error) => logger.error({ error }, 'Re-pair failed.')), 1000);
}

// WhatsApp's own ceiling for a video message is well under this; the limit only stops a clip that
// could never be delivered from tying up the session.
const MAX_VIDEO_BYTES = 64 * 1024 * 1024;

async function sendMessage(number, message, audioPath, videoPath) {
  if (!sock || connectionStatus !== 'connected') {
    throw new Error('WhatsApp session is not connected.');
  }

  const jid = number.includes('@') ? number : `${number.replace(/\D/g, '')}@s.whatsapp.net`;
  if (message) {
    await sock.sendMessage(jid, { text: message });
  }

  if (audioPath) {
    // Only mp3 clips are ever sent; anything else is refused so this local endpoint can't be
    // used to read arbitrary files off disk.
    if (path.extname(audioPath).toLowerCase() !== '.mp3') {
      throw new Error('Only .mp3 audio files can be sent.');
    }

    const audio = await fs.readFile(audioPath);
    await sock.sendMessage(jid, { audio, mimetype: 'audio/mpeg' });
  }

  if (videoPath) {
    // Same rule as the audio: only the clip format the editor produces, so this endpoint cannot be
    // used to read arbitrary files off disk.
    if (path.extname(videoPath).toLowerCase() !== '.mp4') {
      throw new Error('Only .mp4 video files can be sent.');
    }

    const { size } = await fs.stat(videoPath);
    if (size > MAX_VIDEO_BYTES) {
      throw new Error(`El clip pesa ${(size / 1048576).toFixed(0)} MB; el máximo para enviar por WhatsApp es ${MAX_VIDEO_BYTES / 1048576} MB.`);
    }

    const video = await fs.readFile(videoPath);
    await sock.sendMessage(jid, { video, mimetype: 'video/mp4' });
  }
}

function sendJson(res, statusCode, body) {
  const payload = JSON.stringify(body);
  res.writeHead(statusCode, { 'Content-Type': 'application/json' });
  res.end(payload);
}

function readRequestBody(req) {
  return new Promise((resolve, reject) => {
    let raw = '';
    req.on('data', (chunk) => {
      raw += chunk;
    });
    req.on('end', () => resolve(raw));
    req.on('error', reject);
  });
}

async function handleSend(req, res) {
  try {
    const raw = await readRequestBody(req);
    const { number, message, audioPath, videoPath } = JSON.parse(raw || '{}');

    if (!number || (!message && !audioPath && !videoPath)) {
      sendJson(res, 400, { success: false, message: 'Missing number, or all of message, audioPath and videoPath.' });
      return;
    }

    await sendMessage(number, message, audioPath, videoPath);
    sendJson(res, 200, { success: true });
  } catch (error) {
    logger.warn({ error }, 'Failed to send WhatsApp message.');
    sendJson(res, 502, { success: false, message: error.message });
  }
}

function handleStatus(req, res) {
  sendJson(res, 200, { status: connectionStatus });
}

async function handleQr(req, res) {
  if (!latestQr) {
    sendJson(res, 200, { qr: null });
    return;
  }

  try {
    const dataUrl = await QRCode.toDataURL(latestQr);
    sendJson(res, 200, { qr: dataUrl });
  } catch (error) {
    logger.warn({ error }, 'Failed to render QR code.');
    sendJson(res, 500, { qr: null });
  }
}

// Lets the portal offer the linked number's groups as alert recipients: a group's JID
// ("<id>@g.us") is accepted by sendMessage as-is, so the id returned here is what gets stored.
async function handleGroups(req, res) {
  if (!sock || connectionStatus !== 'connected') {
    sendJson(res, 200, { groups: [] });
    return;
  }

  try {
    const participating = await sock.groupFetchAllParticipating();
    const groups = Object.values(participating)
      .map((group) => ({ id: group.id, name: group.subject || group.id }))
      .sort((a, b) => a.name.localeCompare(b.name, 'es'));
    sendJson(res, 200, { groups });
  } catch (error) {
    logger.warn({ error }, 'Failed to list WhatsApp groups.');
    sendJson(res, 502, { groups: [] });
  }
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/groups') {
    handleGroups(req, res);
    return;
  }

  if (req.method === 'POST' && req.url === '/send') {
    handleSend(req, res);
    return;
  }

  if (req.method === 'GET' && req.url === '/status') {
    handleStatus(req, res);
    return;
  }

  if (req.method === 'GET' && req.url === '/qr') {
    handleQr(req, res);
    return;
  }

  res.writeHead(404);
  res.end();
});

server.listen(PORT, 'localhost', () => {
  logger.info(`WhatsApp sidecar listening on http://localhost:${PORT}`);
});

startSocket().catch((error) => {
  logger.error({ error }, 'Failed to start WhatsApp socket.');
});
