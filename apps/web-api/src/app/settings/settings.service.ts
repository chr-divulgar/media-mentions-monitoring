import { Injectable, NotFoundException } from '@nestjs/common';
import { PlatformDto } from '@repo/shared';
import { DataSource, MongoRepository } from 'typeorm';
import { ObjectId } from 'mongodb';
import { Platform } from '../entities';
import { InjectDataSource } from '@nestjs/typeorm';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';

@Injectable()
export class SettingsService {
  platformRepo: MongoRepository<Platform>;

  constructor(
    @InjectDataSource('monitoring') private readonly dataSource: DataSource,
    private readonly firebaseAdmin: FirebaseAdminService,
  ) {
    this.platformRepo = this.dataSource.getMongoRepository(Platform);
  }

  // ─── Platforms ────────────────────────────────────────────────────────────

  async getPlatforms(media: string): Promise<Platform[]> {
    return this.platformRepo.find({ where: { media } });
  }

  async createPlatform(dto: PlatformDto): Promise<Platform> {
    const platform = this.platformRepo.create({
      name: dto.name,
      url: dto.url ?? '',
      media: dto.media,
    });
    return this.platformRepo.save(platform);
  }

  async updatePlatform(dto: PlatformDto): Promise<Platform> {
    const existing = await this.platformRepo.findOneBy({
      _id: new ObjectId(dto.id),
    });
    if (!existing) throw new NotFoundException('Platform not found');

    existing.name = dto.name ?? existing.name;
    existing.url = dto.url ?? existing.url;
    existing.media = dto.media ?? existing.media;

    return this.platformRepo.save(existing);
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
