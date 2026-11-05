import { BadRequestException, ConflictException, Injectable, NotFoundException } from '@nestjs/common';
import { ClientDto, MediaTypeDto } from '@repo/shared';
import {
  ConfigDbService,
  isDuplicateKey,
  toApiDocument,
  toObjectId,
} from '../config-db/config-db.service';

const DEFAULT_MEDIA: Array<Omit<MediaTypeDto, 'id'> & { order: number }> = [
  { name: 'internet', label: 'Internet', order: 0 },
  { name: 'radio', label: 'Radio', order: 1 },
  { name: 'tv', label: 'Televisión', order: 2 },
  { name: 'prensa', label: 'Prensa', order: 3 },
  { name: 'redes', label: 'Redes Sociales', order: 4 },
];

const MAX_LOGO_BYTES = 500 * 1024;

// The daily report's logo: only a PNG or JPEG data URL, and not a big one (it is stored in the client document).
function checkReportLogo(logo: unknown): void {
  if (logo === undefined || logo === null || logo === '') return;
  const match = typeof logo === 'string' ? /^data:image\/(png|jpe?g);base64,([A-Za-z0-9+/=]+)$/i.exec(logo) : null;
  if (!match) throw new BadRequestException('El logo debe ser una imagen PNG o JPG.');
  if (Buffer.from(match[2], 'base64').length > MAX_LOGO_BYTES) {
    throw new BadRequestException('El logo pesa más de 500 KB.');
  }
}

const GOOGLE_ALERTS_FEED_PREFIX = 'https://www.google.com/alerts/feeds/';

// The client's Google Alerts RSS feeds, trimmed and without blanks or repeats. Only a feed link is taken: the worker
// downloads each one every 15 minutes.
function cleanGoogleAlertsFeeds(feeds: unknown): string[] {
  const links = [...new Set((Array.isArray(feeds) ? feeds : []).map((feed) => String(feed ?? '').trim()).filter(Boolean))];
  const wrong = links.find((link) => !link.startsWith(GOOGLE_ALERTS_FEED_PREFIX));
  if (wrong) {
    throw new BadRequestException(
      `"${wrong}" no es el enlace de un feed de Google Alerts (debe empezar por ${GOOGLE_ALERTS_FEED_PREFIX}).`,
    );
  }
  return links;
}

// Case and accents do not tell two names apart ("Inversión" is "inversion").
const fold = (value?: string): string =>
  (value ?? '').normalize('NFD').replace(/\p{M}/gu, '').trim().toLowerCase();

// Clients and media types live in the local MongoDB `config` database (collections `clients` and
// `mediaTypes`); the worker reads the same `clients` documents.
@Injectable()
export class ClientsService {
  constructor(private readonly configDb: ConfigDbService) {}

  // ─── Clients ──────────────────────────────────────────────────────────────

  async getClients(): Promise<ClientDto[]> {
    const docs = await this.configDb.clients.find().sort({ name: 1 }).toArray();
    return docs.map((doc) => toApiDocument(doc)) as unknown as ClientDto[];
  }

  async getClient(id: string): Promise<ClientDto> {
    const objectId = toObjectId(id);
    const doc = objectId && (await this.configDb.clients.findOne({ _id: objectId }));
    if (!doc) throw new NotFoundException('Cliente no encontrado');
    return toApiDocument(doc) as unknown as ClientDto;
  }

  async createClient(dto: ClientDto): Promise<ClientDto> {
    checkReportLogo(dto.reportLogo);
    const payload = {
      name: dto.name,
      words: dto.words ?? [],
      topics: dto.topics ?? [],
      alerts: dto.alerts ?? {},
      alertEmails: dto.alertEmails ?? {},
      notes: dto.notes ?? {},
      reportLogo: dto.reportLogo ?? '',
      reportIndicators: dto.reportIndicators ?? [],
      googleAlertsFeeds: cleanGoogleAlertsFeeds(dto.googleAlertsFeeds),
    };
    try {
      const result = await this.configDb.clients.insertOne({ ...payload });
      return { id: result.insertedId.toHexString(), ...payload };
    } catch (error) {
      if (isDuplicateKey(error)) {
        throw new ConflictException(`Ya existe un cliente llamado "${dto.name}"`);
      }
      throw error;
    }
  }

  async updateClient(id: string, dto: ClientDto): Promise<ClientDto> {
    const objectId = toObjectId(id);
    if (!objectId) throw new NotFoundException('Cliente no encontrado');
    checkReportLogo(dto.reportLogo);

    const payload: Record<string, unknown> = {};
    if (dto.name !== undefined) payload['name'] = dto.name;
    if (dto.words !== undefined) payload['words'] = dto.words;
    if (dto.topics !== undefined) payload['topics'] = dto.topics;
    if (dto.alerts !== undefined) payload['alerts'] = dto.alerts;
    if (dto.alertEmails !== undefined) payload['alertEmails'] = dto.alertEmails;
    if (dto.notes !== undefined) payload['notes'] = dto.notes;
    if (dto.reportLogo !== undefined) payload['reportLogo'] = dto.reportLogo ?? '';
    if (dto.googleAlertsFeeds !== undefined) payload['googleAlertsFeeds'] = cleanGoogleAlertsFeeds(dto.googleAlertsFeeds);
    if (dto.reportIndicators !== undefined) {
      payload['reportIndicators'] = (dto.reportIndicators ?? [])
        .filter((item) => item?.source?.trim() || item?.label?.trim())
        .map((item) => ({
          source: item.source?.trim() ?? '',
          label: item.label?.trim() ?? '',
          ...(item.url?.trim() ? { url: item.url.trim() } : {}),
        }));
    }

    try {
      // MongoDB rejects an empty $set: with nothing to change the update is just a read.
      const updated = Object.keys(payload).length
        ? await this.configDb.clients.findOneAndUpdate({ _id: objectId }, { $set: payload }, { returnDocument: 'after' })
        : await this.configDb.clients.findOne({ _id: objectId });
      if (!updated) throw new NotFoundException('Cliente no encontrado');
      return toApiDocument(updated) as unknown as ClientDto;
    } catch (error) {
      if (isDuplicateKey(error)) {
        throw new ConflictException(`Ya existe un cliente llamado "${dto.name}"`);
      }
      throw error;
    }
  }

  /**
   * Adds a subtopic to one of the client's topics from the note editor, so the next note can pick it. A name that
   * already exists (any case or accents) is not added again: the existing one is answered, to be selected.
   */
  async addSubtopic(clientId: string, topicName: string, name: string): Promise<{ name: string }> {
    return this.addTopicItem(clientId, topicName, undefined, name);
  }

  /** The same for a subsubtopic under one of that topic's subtopics. */
  async addSubsubtopic(
    clientId: string,
    topicName: string,
    subtopicName: string,
    name: string,
  ): Promise<{ name: string }> {
    return this.addTopicItem(clientId, topicName, subtopicName, name);
  }

  private async addTopicItem(
    clientId: string,
    topicName: string,
    subtopicName: string | undefined,
    rawName: string,
  ): Promise<{ name: string }> {
    const name = (rawName ?? '').trim();
    if (!name) throw new BadRequestException('Escribe el nombre.');
    if (name.length > 120) throw new BadRequestException('El nombre es demasiado largo (máx. 120 caracteres).');

    const objectId = toObjectId(clientId);
    const doc = objectId && (await this.configDb.clients.findOne({ _id: objectId }));
    if (!doc) throw new NotFoundException('Cliente no encontrado');

    type Node = { name?: string; subtopics?: Node[]; subsubtopics?: Node[] };
    const topics = ((doc.topics as Node[] | undefined) ?? []).map((topic) => ({ ...topic }));
    const topic = topics.find((candidate) => candidate.name === topicName);
    if (!topic) throw new NotFoundException(`El tema "${topicName}" no existe en este cliente`);

    // The list the new name goes into: the topic's subtopics, or one subtopic's subsubtopics.
    let list: Node[];
    if (subtopicName === undefined) {
      list = topic.subtopics = [...(topic.subtopics ?? [])];
    } else {
      const subtopics = (topic.subtopics = [...(topic.subtopics ?? [])]).map((item) => ({ ...item }));
      topic.subtopics = subtopics;
      const subtopic = subtopics.find((candidate) => candidate.name === subtopicName);
      if (!subtopic) throw new NotFoundException(`El subtema "${subtopicName}" no existe en este tema`);
      list = subtopic.subsubtopics = [...(subtopic.subsubtopics ?? [])];
    }

    const same = (a?: string, b?: string) => fold(a) === fold(b);
    const existing = list.find((item) => same(item.name, name));
    if (existing) return { name: existing.name as string };

    list.push({ name });
    await this.configDb.clients.findOneAndUpdate({ _id: objectId }, { $set: { topics } });
    return { name };
  }

  async deleteClient(id: string): Promise<void> {
    const objectId = toObjectId(id);
    const result = objectId && (await this.configDb.clients.deleteOne({ _id: objectId }));
    if (!result?.deletedCount) throw new NotFoundException('Cliente no encontrado');
  }

  // ─── Media Types ──────────────────────────────────────────────────────────

  async getMediaTypes(): Promise<MediaTypeDto[]> {
    let docs = await this.configDb.mediaTypes.find().toArray();
    if (docs.length === 0) {
      // Seed defaults on first call
      await this.configDb.mediaTypes.insertMany(DEFAULT_MEDIA.map((media) => ({ ...media })));
      docs = await this.configDb.mediaTypes.find().toArray();
    }
    // Sort by order field if present, otherwise keep insertion order
    docs.sort((a, b) => {
      const aOrd = a.order as number | undefined;
      const bOrd = b.order as number | undefined;
      if (aOrd !== undefined && bOrd !== undefined) return aOrd - bOrd;
      if (aOrd !== undefined) return -1;
      if (bOrd !== undefined) return 1;
      return 0;
    });
    return docs.map((doc) => toApiDocument(doc)) as unknown as MediaTypeDto[];
  }

  async createMediaType(dto: MediaTypeDto): Promise<MediaTypeDto> {
    const last = await this.configDb.mediaTypes.find().sort({ order: -1 }).limit(1).toArray();
    const maxOrder = last.length > 0 ? ((last[0].order as number) ?? 0) + 1 : 0;
    const payload = { name: dto.name, label: dto.label, order: maxOrder };
    try {
      const result = await this.configDb.mediaTypes.insertOne({ ...payload });
      return { id: result.insertedId.toHexString(), ...payload };
    } catch (error) {
      if (isDuplicateKey(error)) {
        throw new ConflictException(`Ya existe un tipo de medio "${dto.name}"`);
      }
      throw error;
    }
  }

  async updateMediaType(id: string, dto: MediaTypeDto): Promise<MediaTypeDto> {
    const objectId = toObjectId(id);
    if (!objectId) throw new NotFoundException('Tipo de medio no encontrado');

    const payload: Record<string, unknown> = {};
    if (dto.name !== undefined) payload['name'] = dto.name;
    if (dto.label !== undefined) payload['label'] = dto.label;

    const updated = Object.keys(payload).length
      ? await this.configDb.mediaTypes.findOneAndUpdate({ _id: objectId }, { $set: payload }, { returnDocument: 'after' })
      : await this.configDb.mediaTypes.findOne({ _id: objectId });
    if (!updated) throw new NotFoundException('Tipo de medio no encontrado');
    return toApiDocument(updated) as unknown as MediaTypeDto;
  }

  async deleteMediaType(id: string): Promise<void> {
    const objectId = toObjectId(id);
    const result = objectId && (await this.configDb.mediaTypes.deleteOne({ _id: objectId }));
    if (!result?.deletedCount) throw new NotFoundException('Tipo de medio no encontrado');
  }
}
