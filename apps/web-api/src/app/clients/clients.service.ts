import { ConflictException, Injectable, NotFoundException } from '@nestjs/common';
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
    const payload = {
      name: dto.name,
      words: dto.words ?? [],
      topics: dto.topics ?? [],
      alerts: dto.alerts ?? {},
      alertEmails: dto.alertEmails ?? {},
      notes: dto.notes ?? {},
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

    const payload: Record<string, unknown> = {};
    if (dto.name !== undefined) payload['name'] = dto.name;
    if (dto.words !== undefined) payload['words'] = dto.words;
    if (dto.topics !== undefined) payload['topics'] = dto.topics;
    if (dto.alerts !== undefined) payload['alerts'] = dto.alerts;
    if (dto.alertEmails !== undefined) payload['alertEmails'] = dto.alertEmails;
    if (dto.notes !== undefined) payload['notes'] = dto.notes;

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
