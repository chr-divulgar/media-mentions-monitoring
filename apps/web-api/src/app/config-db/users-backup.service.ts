import { Injectable, Logger } from '@nestjs/common';
import { ConfigDbService, UserBackupDoc } from './config-db.service';

/**
 * A copy of the Firestore `users` collection in MongoDB. Firestore stays the source of truth;
 * this copy only answers when Firestore cannot (AuthService falls back to its role) and is what
 * the worker reads to know who is told about platform errors.
 *
 * Writing the copy is best effort: it must never fail the operation that caused it.
 */
@Injectable()
export class UsersBackupService {
  private readonly logger = new Logger(UsersBackupService.name);

  constructor(private readonly db: ConfigDbService) {}

  /** Creates or updates the copy of one user; fields left undefined keep what the copy has. */
  async upsert(uid: string, fields: Partial<Omit<UserBackupDoc, 'uid'>>): Promise<void> {
    try {
      const set: Record<string, unknown> = { updatedAt: new Date() };
      for (const [key, value] of Object.entries(fields)) {
        if (value !== undefined && key !== 'createdAt') set[key] = value;
      }
      await this.db.users.updateOne(
        { uid },
        { $set: set, $setOnInsert: { uid, createdAt: fields.createdAt ?? new Date() } },
        { upsert: true },
      );
    } catch (error) {
      this.logger.warn(`Could not back up user ${uid}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  async remove(uid: string): Promise<void> {
    try {
      await this.db.users.deleteOne({ uid });
    } catch (error) {
      this.logger.warn(`Could not remove the backup of user ${uid}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  /** The backed-up role, or null when there is no copy (or the database is unreachable). */
  async getRole(uid: string): Promise<string | null> {
    try {
      const doc = await this.db.users.findOne({ uid }, { projection: { role: 1 } });
      return doc?.role ?? null;
    } catch (error) {
      this.logger.warn(`Could not read the backup role of user ${uid}: ${error instanceof Error ? error.message : String(error)}`);
      return null;
    }
  }
}
