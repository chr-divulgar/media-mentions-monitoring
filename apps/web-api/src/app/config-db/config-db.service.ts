import { Injectable, Logger, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import { Collection, Document, MongoClient, ObjectId } from 'mongodb';

/**
 * The configuration data (platforms with their slots, clients, media types) and the backup copy
 * of the users, in the local MongoDB `config` database. The documents are the same ones the .NET
 * worker reads, so they are handled as plain documents rather than TypeORM entities.
 *
 * Firestore keeps only what user authentication needs (see AuthService); this replaced it for
 * everything else because its free tier ran out and took the login and the worker down with it.
 */
export const CONFIG_DATABASE = 'config';

export interface UserBackupDoc extends Document {
  uid: string;
  email?: string | null;
  name?: string | null;
  role?: string;
  phone?: string;
  notifyPlatformErrors?: boolean;
  photoURL?: string | null;
  createdAt?: Date;
  updatedAt?: Date;
}

@Injectable()
export class ConfigDbService implements OnModuleInit, OnModuleDestroy {
  private readonly logger = new Logger(ConfigDbService.name);
  private readonly client: MongoClient;

  constructor(config: ConfigService) {
    this.client = new MongoClient(config.get<string>('MONGODB_URI') ?? 'mongodb://localhost:27017');
  }

  async onModuleInit(): Promise<void> {
    try {
      await this.client.connect();
      await Promise.all([
        // Several documents may have no sourceId (WhatsApp-only entries), hence the partial index.
        this.platforms.createIndex(
          { sourceId: 1 },
          { unique: true, partialFilterExpression: { sourceId: { $type: 'string' } } },
        ),
        this.clients.createIndex({ name: 1 }, { unique: true }),
        this.mediaTypes.createIndex({ name: 1 }, { unique: true }),
        this.users.createIndex({ uid: 1 }, { unique: true }),
      ]);
      this.logger.log(`Config database '${CONFIG_DATABASE}' ready.`);
    } catch (error) {
      // The API still starts; the calls that need the database will report the failure.
      this.logger.error(
        `Could not prepare the '${CONFIG_DATABASE}' database: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }

  async onModuleDestroy(): Promise<void> {
    await this.client.close();
  }

  private collection<T extends Document>(name: string): Collection<T> {
    return this.client.db(CONFIG_DATABASE).collection<T>(name);
  }

  get platforms(): Collection<Document> {
    return this.collection('platforms');
  }

  get clients(): Collection<Document> {
    return this.collection('clients');
  }

  get mediaTypes(): Collection<Document> {
    return this.collection('mediaTypes');
  }

  get users(): Collection<UserBackupDoc> {
    return this.collection<UserBackupDoc>('users');
  }
}

/** A Mongo `_id` from the string the API exposes; undefined when it is not a valid id. */
export function toObjectId(id: string | undefined): ObjectId | undefined {
  return id && ObjectId.isValid(id) ? new ObjectId(id) : undefined;
}

/** The document as the API shows it: `_id` becomes `id`, and bookkeeping fields stay out. */
export function toApiDocument<T extends Document>(doc: T): Record<string, unknown> {
  const { _id, firestoreId: _firestoreId, ...rest } = doc as Document;
  void _firestoreId;
  return { id: (_id as ObjectId).toHexString(), ...rest };
}

/** MongoDB's duplicate key error (a unique index such as the platform's sourceId was hit). */
export function isDuplicateKey(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { code?: number }).code === 11000;
}
