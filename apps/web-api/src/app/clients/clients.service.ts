import { Injectable, NotFoundException } from '@nestjs/common';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';
import { ClientDto, MediaTypeDto } from '@repo/shared';

const DEFAULT_MEDIA: Array<Omit<MediaTypeDto, 'id'> & { order: number }> = [
  { name: 'internet', label: 'Internet', order: 0 },
  { name: 'radio', label: 'Radio', order: 1 },
  { name: 'tv', label: 'Televisión', order: 2 },
  { name: 'prensa', label: 'Prensa', order: 3 },
  { name: 'redes', label: 'Redes Sociales', order: 4 },
];

@Injectable()
export class ClientsService {
  constructor(private readonly firebaseAdmin: FirebaseAdminService) {}

  private get clientsCol() {
    return this.firebaseAdmin.firestore.collection('clients');
  }

  private get mediaCol() {
    return this.firebaseAdmin.firestore.collection('media_types');
  }

  // ─── Clients ──────────────────────────────────────────────────────────────

  async getClients(): Promise<ClientDto[]> {
    const snap = await this.clientsCol.orderBy('name').get();
    return snap.docs.map((d) => ({ id: d.id, ...(d.data() as ClientDto) }));
  }

  async getClient(id: string): Promise<ClientDto> {
    const doc = await this.clientsCol.doc(id).get();
    if (!doc.exists) throw new NotFoundException('Cliente no encontrado');
    return { id: doc.id, ...(doc.data() as ClientDto) };
  }

  async createClient(dto: ClientDto): Promise<ClientDto> {
    const payload = {
      name: dto.name,
      words: dto.words ?? [],
      alerts: dto.alerts ?? {},
      notes: dto.notes ?? {},
    };
    const ref = await this.clientsCol.add(payload);
    return { id: ref.id, ...payload };
  }

  async updateClient(id: string, dto: ClientDto): Promise<ClientDto> {
    const ref = this.clientsCol.doc(id);
    const doc = await ref.get();
    if (!doc.exists) throw new NotFoundException('Cliente no encontrado');

    const existing = doc.data() as ClientDto;
    const payload: Record<string, unknown> = {};
    if (dto.name !== undefined) payload['name'] = dto.name;
    if (dto.words !== undefined) payload['words'] = dto.words;
    if (dto.alerts !== undefined) payload['alerts'] = dto.alerts;
    if (dto.notes !== undefined) payload['notes'] = dto.notes;

    await ref.update(payload);
    return { id, ...existing, ...payload };
  }

  async deleteClient(id: string): Promise<void> {
    const doc = await this.clientsCol.doc(id).get();
    if (!doc.exists) throw new NotFoundException('Cliente no encontrado');
    await this.clientsCol.doc(id).delete();
  }

  // ─── Media Types ──────────────────────────────────────────────────────────

  async getMediaTypes(): Promise<MediaTypeDto[]> {
    const snap = await this.mediaCol.get();
    if (snap.empty) {
      // Seed defaults on first call
      const batch = this.firebaseAdmin.firestore.batch();
      for (const m of DEFAULT_MEDIA) {
        batch.set(this.mediaCol.doc(), m);
      }
      await batch.commit();
      return this.getMediaTypes();
    }
    const docs = snap.docs.map((d) => ({
      id: d.id,
      ...(d.data() as MediaTypeDto & { order?: number }),
    }));
    // Sort by order field if present, otherwise keep Firestore ID order (insertion order)
    docs.sort((a, b) => {
      const aOrd = (a as { order?: number }).order;
      const bOrd = (b as { order?: number }).order;
      if (aOrd !== undefined && bOrd !== undefined) return aOrd - bOrd;
      if (aOrd !== undefined) return -1;
      if (bOrd !== undefined) return 1;
      return 0;
    });
    return docs;
  }

  async createMediaType(dto: MediaTypeDto): Promise<MediaTypeDto> {
    const snap = await this.mediaCol.get();
    const orders = snap.docs.map((d) => (d.data().order as number) ?? 0);
    const maxOrder = orders.length > 0 ? Math.max(...orders) + 1 : 0;
    const payload = { name: dto.name, label: dto.label, order: maxOrder };
    const ref = await this.mediaCol.add(payload);
    return { id: ref.id, ...payload };
  }

  async updateMediaType(id: string, dto: MediaTypeDto): Promise<MediaTypeDto> {
    const ref = this.mediaCol.doc(id);
    const doc = await ref.get();
    if (!doc.exists) throw new NotFoundException('Tipo de medio no encontrado');
    const payload: Record<string, unknown> = {};
    if (dto.name !== undefined) payload['name'] = dto.name;
    if (dto.label !== undefined) payload['label'] = dto.label;
    await ref.update(payload);
    return { id, ...(doc.data() as MediaTypeDto), ...payload };
  }

  async deleteMediaType(id: string): Promise<void> {
    const doc = await this.mediaCol.doc(id).get();
    if (!doc.exists) throw new NotFoundException('Tipo de medio no encontrado');
    await this.mediaCol.doc(id).delete();
  }
}
