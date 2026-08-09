import { Injectable, NotFoundException } from '@nestjs/common';
import { PlatformDto, SlotDto, Day } from '@repo/shared';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';

const PLATFORMS_COLLECTION = 'platforms';

function nameToDisplay(name: string): string {
  // Split camelCase into words: "CaracolBucaramanga" → "Caracol Bucaramanga"
  return name.replaceAll(/([A-Z])/g, ' $1').trim();
}

function nameToAudio(name: string): string {
  return nameToDisplay(name).toUpperCase().replaceAll(/\s+/g, '_');
}

function generateDefaultSlots(name: string): SlotDto[] {
  const display = nameToDisplay(name);
  const audio = nameToAudio(name);
  return [
    {
      day: Day.Weekday,
      start: '00:00',
      end: '04:00',
      label: `${display}: Noticias de la Madrugada:`,
      audioLabel: `_${audio}_AM_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Weekday,
      start: '04:00',
      end: '12:00',
      label: `${display}: Noticias de la Mañana:`,
      audioLabel: `_${audio}_AM_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Weekday,
      start: '12:00',
      end: '13:00',
      label: `${display}: Noticias del Medio Día:`,
      audioLabel: `_${audio}_MD_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Weekday,
      start: '13:00',
      end: '19:00',
      label: `${display}: Noticias de la Tarde:`,
      audioLabel: `_${audio}_TARDE_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Weekday,
      start: '19:00',
      end: '23:59',
      label: `${display}: Noticias de la Noche:`,
      audioLabel: `_${audio}_NOCHE_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Saturday,
      start: '00:00',
      end: '23:59',
      label: `${display}: Fin de semana:`,
      audioLabel: `_${audio}_FIN_DE_SEMANA_`,
      audience: 5000,
      rate: 105000,
    },
    {
      day: Day.Sunday,
      start: '00:00',
      end: '23:59',
      label: `${display}: Fin de semana:`,
      audioLabel: `_${audio}_FIN_DE_SEMANA_`,
      audience: 5000,
      rate: 105000,
    },
  ];
}

interface PlatformDoc {
  id: string;
  name: string;
  url: string;
  media: string;
  zone: string;
  city: string;
  slots: SlotDto[];
  audience?: number;
  rate?: number;
  sourceId?: string;
  streamUrl?: string;
  primaryUrl?: string;
  country?: string;
  fallbackStreamUrls?: string[];
}

function buildOptionalPlatformFields(
  dto: PlatformDto,
  existing: Omit<PlatformDoc, 'id'>,
): Partial<Omit<PlatformDoc, 'id'>> {
  const candidates: Partial<Omit<PlatformDoc, 'id'>> = {
    audience: dto.audience == null ? existing.audience : Number(dto.audience),
    rate: dto.rate == null ? existing.rate : Number(dto.rate),
    sourceId: dto.sourceId !== undefined ? dto.sourceId : existing.sourceId,
    streamUrl: dto.streamUrl !== undefined ? dto.streamUrl : existing.streamUrl,
    primaryUrl:
      dto.primaryUrl !== undefined ? dto.primaryUrl : existing.primaryUrl,
    country: dto.country !== undefined ? dto.country : existing.country,
    fallbackStreamUrls:
      dto.fallbackStreamUrls !== undefined
        ? dto.fallbackStreamUrls
        : existing.fallbackStreamUrls,
  };

  return Object.fromEntries(
    Object.entries(candidates).filter(([, value]) => value !== undefined),
  ) as Partial<Omit<PlatformDoc, 'id'>>;
}

@Injectable()
export class SettingsService {
  constructor(private readonly firebaseAdmin: FirebaseAdminService) {}

  private get db() {
    return this.firebaseAdmin.firestore;
  }

  // ─── Platforms ────────────────────────────────────────────────────────────

  async getPlatforms(media: string): Promise<PlatformDoc[]> {
    const snap = await this.db
      .collection(PLATFORMS_COLLECTION)
      .where('media', '==', media)
      .get();
    return snap.docs.map((doc) => ({
      id: doc.id,
      ...(doc.data() as Omit<PlatformDoc, 'id'>),
    }));
  }

  async createPlatform(dto: PlatformDto): Promise<PlatformDoc> {
    const slots = dto.slots?.length
      ? dto.slots
      : generateDefaultSlots(dto.name ?? '');
    const data: Omit<PlatformDoc, 'id'> = {
      name: dto.name ?? '',
      url: dto.url ?? '',
      media: dto.media ?? '',
      zone: dto.zone ?? 'Nacional',
      city: dto.city ?? 'Nacional',
      slots,
      ...(dto.audience == null ? {} : { audience: Number(dto.audience) }),
      ...(dto.rate == null ? {} : { rate: Number(dto.rate) }),
      ...(dto.sourceId ? { sourceId: dto.sourceId } : {}),
      ...(dto.streamUrl ? { streamUrl: dto.streamUrl } : {}),
      ...(dto.primaryUrl ? { primaryUrl: dto.primaryUrl } : {}),
      ...(dto.country ? { country: dto.country } : {}),
      ...(dto.fallbackStreamUrls?.length
        ? { fallbackStreamUrls: dto.fallbackStreamUrls }
        : {}),
    };
    const ref = await this.db.collection(PLATFORMS_COLLECTION).add(data);
    return { id: ref.id, ...data };
  }

  async updatePlatform(dto: PlatformDto): Promise<PlatformDoc> {
    if (!dto.id) throw new NotFoundException('Platform id required');
    const ref = this.db.collection(PLATFORMS_COLLECTION).doc(dto.id);
    const snap = await ref.get();
    if (!snap.exists) throw new NotFoundException('Platform not found');

    const existing = snap.data() as Omit<PlatformDoc, 'id'>;
    const optionalFields = buildOptionalPlatformFields(dto, existing);

    const updated: Omit<PlatformDoc, 'id'> = {
      name: dto.name ?? existing.name,
      url: dto.url ?? existing.url,
      media: dto.media ?? existing.media,
      zone: dto.zone ?? existing.zone ?? 'Nacional',
      city: dto.city ?? existing.city ?? 'Nacional',
      slots: dto.slots ?? existing.slots,
      ...optionalFields,
    };
    await ref.update(updated);
    return { id: dto.id, ...updated };
  }

  async deletePlatform(id: string): Promise<{ success: boolean }> {
    await this.db.collection(PLATFORMS_COLLECTION).doc(id).delete();
    return { success: true };
  }

  // ─── Users (Firebase Auth + Firestore) ────────────────────────────────────────────

  async getUsers() {
    const result = await this.firebaseAdmin.auth.listUsers(1000);
    const uids = result.users.map((u) => u.uid);

    // Fetch Firestore docs for all users in batches of 10
    const fs = this.firebaseAdmin.firestore;
    const firestoreData = new Map<string, Record<string, unknown>>();
    for (let i = 0; i < uids.length; i += 10) {
      const batch = uids.slice(i, i + 10);
      const snaps = await Promise.all(
        batch.map((uid) => fs.collection('users').doc(uid).get()),
      );
      snaps.forEach((snap) => {
        if (snap.exists)
          firestoreData.set(snap.id, snap.data() as Record<string, unknown>);
      });
    }

    return result.users.map((u) => {
      const doc = firestoreData.get(u.uid) ?? {};
      return {
        uid: u.uid,
        email: u.email,
        displayName: u.displayName,
        disabled: u.disabled,
        role: doc.role ?? 'initial',
        phone: doc.phone ?? '',
      };
    });
  }

  async createUser(dto: {
    email: string;
    password: string;
    displayName?: string;
    role?: string;
    phone?: string;
  }) {
    const user = await this.firebaseAdmin.auth.createUser({
      email: dto.email,
      password: dto.password,
      displayName: dto.displayName,
    });
    const role = dto.role ?? 'initial';
    const phone = dto.phone ?? '';
    await this.firebaseAdmin.firestore
      .collection('users')
      .doc(user.uid)
      .set({
        email: dto.email,
        name: dto.displayName ?? dto.email,
        role,
        phone,
        createdAt: new Date(),
        updatedAt: new Date(),
      });
    return {
      uid: user.uid,
      email: user.email,
      displayName: user.displayName,
      role,
      phone,
    };
  }

  async updateUser(dto: {
    uid: string;
    displayName?: string;
    role?: string;
    phone?: string;
    password?: string;
    disabled?: boolean;
  }) {
    const authUpdate: Record<string, unknown> = {};
    if (dto.displayName !== undefined) authUpdate.displayName = dto.displayName;
    if (dto.password) authUpdate.password = dto.password;
    if (dto.disabled !== undefined) authUpdate.disabled = dto.disabled;

    if (Object.keys(authUpdate).length > 0) {
      await this.firebaseAdmin.auth.updateUser(dto.uid, authUpdate);
    }

    const fsUpdate: Record<string, unknown> = { updatedAt: new Date() };
    if (dto.displayName !== undefined) fsUpdate.name = dto.displayName;
    if (dto.role !== undefined) fsUpdate.role = dto.role;
    if (dto.phone !== undefined) fsUpdate.phone = dto.phone;

    await this.firebaseAdmin.firestore
      .collection('users')
      .doc(dto.uid)
      .set(fsUpdate, { merge: true });

    return { success: true };
  }

  async deleteUser(uid: string) {
    await this.firebaseAdmin.auth.deleteUser(uid);
    await this.firebaseAdmin.firestore.collection('users').doc(uid).delete();
    return { success: true };
  }
}
