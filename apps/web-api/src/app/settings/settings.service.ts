import { Injectable, NotFoundException } from '@nestjs/common';
import { PlatformDto } from '@repo/shared';
import { DataSource, MongoRepository } from 'typeorm';
import { ObjectId } from 'mongodb';
import { Platform } from '../entities';
import { InjectDataSource } from '@nestjs/typeorm';

@Injectable()
export class SettingsService {
  platformRepo: MongoRepository<Platform>;

  constructor(
    @InjectDataSource('monitoring') private readonly dataSource: DataSource,
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
}
