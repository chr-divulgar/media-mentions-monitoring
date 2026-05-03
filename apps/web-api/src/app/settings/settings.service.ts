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

  // ─── Users (Firebase Auth) ────────────────────────────────────────────────

  async getUsers() {
    const result = await this.firebaseAdmin.auth.listUsers(1000);
    return result.users.map((u) => ({
      uid: u.uid,
      email: u.email,
      displayName: u.displayName,
      disabled: u.disabled,
      role: (u.customClaims as Record<string, string> | undefined)?.role,
      phone: (u.customClaims as Record<string, string> | undefined)?.phone,
    }));
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
    const claims = {
      role: dto.role ?? 'initial',
      phone: dto.phone ?? '',
    };
    await this.firebaseAdmin.auth.setCustomUserClaims(user.uid, claims);
    return {
      uid: user.uid,
      email: user.email,
      displayName: user.displayName,
      role: claims.role,
      phone: claims.phone,
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
    const updateData: Record<string, unknown> = {};
    if (dto.displayName !== undefined) updateData.displayName = dto.displayName;
    if (dto.password) updateData.password = dto.password;
    if (dto.disabled !== undefined) updateData.disabled = dto.disabled;

    await this.firebaseAdmin.auth.updateUser(dto.uid, updateData);

    if (dto.role !== undefined || dto.phone !== undefined) {
      const existing = await this.firebaseAdmin.auth.getUser(dto.uid);
      const prevClaims = (existing.customClaims as Record<string, string>) ?? {};
      await this.firebaseAdmin.auth.setCustomUserClaims(dto.uid, {
        ...prevClaims,
        ...(dto.role === undefined ? {} : { role: dto.role }),
        ...(dto.phone === undefined ? {} : { phone: dto.phone }),
      });
    }
    return { success: true };
  }

  async deleteUser(uid: string) {
    await this.firebaseAdmin.auth.deleteUser(uid);
    return { success: true };
  }
}
