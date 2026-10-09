import { ObjectId } from 'mongodb';

type Doc = Record<string, unknown>;

/**
 * A tiny in-memory stand-in for the part of a MongoDB collection the config services use, so their
 * logic (what gets `$set`, duplicate keys, ordering) is tested without a database. Test support
 * only: filters are equality on top-level fields, and `uniqueKeys` mimic unique indexes.
 */
export class FakeCollection {
  readonly docs: Doc[] = [];

  constructor(private readonly uniqueKeys: string[] = []) {}

  private matches(doc: Doc, filter: Doc = {}): boolean {
    return Object.entries(filter).every(([key, expected]) => {
      const actual = doc[key];
      return actual instanceof ObjectId && expected instanceof ObjectId
        ? actual.equals(expected)
        : actual === expected;
    });
  }

  private assertUnique(candidate: Doc, ignore?: Doc): void {
    for (const key of this.uniqueKeys) {
      if (candidate[key] === undefined) continue;
      if (this.docs.some((doc) => doc !== ignore && doc[key] === candidate[key])) {
        throw Object.assign(new Error(`E11000 duplicate key: ${key}`), { code: 11000 });
      }
    }
  }

  async createIndex(): Promise<string> {
    return 'index';
  }

  async insertOne(doc: Doc) {
    this.assertUnique(doc);
    const stored = { _id: new ObjectId(), ...doc };
    this.docs.push(stored);
    return { insertedId: stored._id as ObjectId };
  }

  async insertMany(docs: Doc[]) {
    for (const doc of docs) await this.insertOne(doc);
    return { insertedCount: docs.length };
  }

  find(filter: Doc = {}) {
    let result = this.docs.filter((doc) => this.matches(doc, filter));
    const cursor = {
      sort: (spec: Record<string, 1 | -1>) => {
        const [[key, direction]] = Object.entries(spec);
        result = [...result].sort((a, b) => {
          const av = a[key] as number | string;
          const bv = b[key] as number | string;
          return (av < bv ? -1 : av > bv ? 1 : 0) * direction;
        });
        return cursor;
      },
      limit: (count: number) => {
        result = result.slice(0, count);
        return cursor;
      },
      toArray: async () => result.map((doc) => ({ ...doc })),
    };
    return cursor;
  }

  async findOne(filter: Doc = {}) {
    const doc = this.docs.find((candidate) => this.matches(candidate, filter));
    return doc ? { ...doc } : null;
  }

  async findOneAndUpdate(filter: Doc, update: { $set: Doc }) {
    const doc = this.docs.find((candidate) => this.matches(candidate, filter));
    if (!doc) return null;
    this.assertUnique({ ...doc, ...update.$set }, doc);
    Object.assign(doc, update.$set);
    return { ...doc };
  }

  async updateOne(
    filter: Doc,
    update: { $set?: Doc; $setOnInsert?: Doc },
    options: { upsert?: boolean } = {},
  ) {
    const doc = this.docs.find((candidate) => this.matches(candidate, filter));
    if (doc) {
      Object.assign(doc, update.$set);
      return { matchedCount: 1, upsertedCount: 0 };
    }
    if (!options.upsert) return { matchedCount: 0, upsertedCount: 0 };
    await this.insertOne({ ...filter, ...update.$setOnInsert, ...update.$set });
    return { matchedCount: 0, upsertedCount: 1 };
  }

  async deleteOne(filter: Doc) {
    const index = this.docs.findIndex((candidate) => this.matches(candidate, filter));
    if (index >= 0) this.docs.splice(index, 1);
    return { deletedCount: index >= 0 ? 1 : 0 };
  }
}
