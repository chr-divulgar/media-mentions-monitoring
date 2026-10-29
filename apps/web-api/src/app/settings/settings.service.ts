import { ConflictException, Injectable, NotFoundException } from '@nestjs/common';
import { PlatformDto, SlotDto, Day } from '@repo/shared';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';
import {
  ConfigDbService,
  isDuplicateKey,
  toApiDocument,
  toObjectId,
} from '../config-db/config-db.service';
import { UsersBackupService } from '../config-db/users-backup.service';

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
  isExcluded?: boolean;
}

/**
 * The fields an edit changes, and only those: the document is updated with `$set`, never
 * rewritten whole, so what the capture worker keeps in the same document (isExcluded, a
 * re-resolved streamUrl, discovered fallback URLs) is not overwritten with a stale copy.
 * A field the request leaves out keeps its value.
 */
export function buildPlatformUpdate(dto: PlatformDto): Record<string, unknown> {
  const candidates: Record<string, unknown> = {
    name: dto.name,
    url: dto.url,
    media: dto.media,
    zone: dto.zone,
    city: dto.city,
    slots: dto.slots,
    audience: dto.audience == null ? undefined : Number(dto.audience),
    rate: dto.rate == null ? undefined : Number(dto.rate),
    sourceId: dto.sourceId,
    streamUrl: dto.streamUrl,
    primaryUrl: dto.primaryUrl,
    country: dto.country,
    fallbackStreamUrls: dto.fallbackStreamUrls,
    isExcluded: dto.isExcluded,
  };
  return Object.fromEntries(Object.entries(candidates).filter(([, value]) => value !== undefined));
}

@Injectable()
export class SettingsService {
  constructor(
    private readonly firebaseAdmin: FirebaseAdminService,
    private readonly configDb: ConfigDbService,
    private readonly usersBackup: UsersBackupService,
  ) {}

  // ─── Platforms (MongoDB, config.platforms) ───────────────────────────────

  async getPlatforms(media: string): Promise<PlatformDoc[]> {
    const docs = await this.configDb.platforms.find({ media }).toArray();
    return docs.map((doc) => toApiDocument(doc)) as unknown as PlatformDoc[];
  }

  /**
   * The worker only captures documents with a sourceId, and the screen leaves it optional: a source
   * with a stream saved without one was never recorded. It gets one from its name ("Canal Tró" is
   * "Canal_Tro"), with _2, _3... when another source already has it. An entry with no stream (only
   * for WhatsApp, say) is not a capture source and keeps none.
   */
  private async missingSourceId(
    name: string | undefined,
    current: Record<string, unknown>,
  ): Promise<string | undefined> {
    const hasStream = Boolean(current.streamUrl || current.primaryUrl);
    if (current.sourceId || !hasStream) return undefined;

    const base =
      (name ?? '')
        .normalize('NFD')
        .replace(/[̀-ͯ]/g, '')
        .replace(/[^A-Za-z0-9]+/g, '_')
        .replace(/^_+|_+$/g, '') || 'fuente';
    for (let n = 1; ; n++) {
      const candidate = n === 1 ? base : `${base}_${n}`;
      if (!(await this.configDb.platforms.findOne({ sourceId: candidate }))) return candidate;
    }
  }

  async createPlatform(dto: PlatformDto): Promise<PlatformDoc> {
    dto = {
      ...dto,
      sourceId: dto.sourceId || (await this.missingSourceId(dto.name, { ...dto })),
    };
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
      ...(dto.isExcluded !== undefined ? { isExcluded: dto.isExcluded } : {}),
    };
    try {
      const result = await this.configDb.platforms.insertOne({ ...data });
      return { id: result.insertedId.toHexString(), ...data };
    } catch (error) {
      if (isDuplicateKey(error)) {
        throw new ConflictException(`Ya existe una fuente con el Source ID "${dto.sourceId}"`);
      }
      throw error;
    }
  }

  async updatePlatform(dto: PlatformDto): Promise<PlatformDoc> {
    const id = toObjectId(dto.id);
    if (!id) throw new NotFoundException('Platform id required');

    const stored = await this.configDb.platforms.findOne({ _id: id });
    if (!stored) throw new NotFoundException('Platform not found');
    const update = buildPlatformUpdate(dto);
    const sourceId = await this.missingSourceId(
      (update.name as string | undefined) ?? (stored.name as string | undefined),
      { ...stored, ...update },
    );
    if (sourceId) update.sourceId = sourceId;

    try {
      const updated = await this.configDb.platforms.findOneAndUpdate(
        { _id: id },
        { $set: update },
        { returnDocument: 'after' },
      );
      if (!updated) throw new NotFoundException('Platform not found');
      return toApiDocument(updated) as unknown as PlatformDoc;
    } catch (error) {
      if (isDuplicateKey(error)) {
        throw new ConflictException(`Ya existe una fuente con el Source ID "${dto.sourceId}"`);
      }
      throw error;
    }
  }

  async deletePlatform(id: string): Promise<{ success: boolean }> {
    const objectId = toObjectId(id);
    if (!objectId) throw new NotFoundException('Platform not found');
    await this.configDb.platforms.deleteOne({ _id: objectId });
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
        notifyPlatformErrors: doc.notifyPlatformErrors === true,
      };
    });
  }

  async createUser(dto: {
    email: string;
    password: string;
    displayName?: string;
    role?: string;
    phone?: string;
    notifyPlatformErrors?: boolean;
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
        notifyPlatformErrors: dto.notifyPlatformErrors === true,
        createdAt: new Date(),
        updatedAt: new Date(),
      });
    await this.usersBackup.upsert(user.uid, {
      email: dto.email,
      name: dto.displayName ?? dto.email,
      role,
      phone,
      notifyPlatformErrors: dto.notifyPlatformErrors === true,
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
    notifyPlatformErrors?: boolean;
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
    if (dto.notifyPlatformErrors !== undefined)
      fsUpdate.notifyPlatformErrors = dto.notifyPlatformErrors;

    await this.firebaseAdmin.firestore
      .collection('users')
      .doc(dto.uid)
      .set(fsUpdate, { merge: true });
    await this.usersBackup.upsert(dto.uid, {
      name: fsUpdate.name as string | undefined,
      role: fsUpdate.role as string | undefined,
      phone: fsUpdate.phone as string | undefined,
      notifyPlatformErrors: fsUpdate.notifyPlatformErrors as boolean | undefined,
    });

    return { success: true };
  }

  async deleteUser(uid: string) {
    await this.firebaseAdmin.auth.deleteUser(uid);
    await this.firebaseAdmin.firestore.collection('users').doc(uid).delete();
    await this.usersBackup.remove(uid);
    return { success: true };
  }
}
