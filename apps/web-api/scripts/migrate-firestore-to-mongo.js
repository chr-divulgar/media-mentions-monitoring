/**
 * One-time migration: copies the configuration that used to live in Firestore into the local
 * MongoDB `config` database, and the users into their backup copy.
 *
 *   Firestore platforms    -> config.platforms   (matched by sourceId, else by firestoreId)
 *   Firestore clients      -> config.clients     (matched by name)
 *   Firestore media_types  -> config.mediaTypes  (matched by name)
 *   Firestore users        -> config.users       (matched by uid)
 *
 * Safe to run again: every document is upserted, nothing is deleted on either side. Firestore is
 * only read (about one read per document), so run it while the free-tier quota is available.
 *
 *   pnpm --filter web-api migrate:firestore -- --dry-run     # read and report, write nothing
 *   pnpm --filter web-api migrate:firestore                  # migrate
 *
 * Reads FIREBASE_* and MONGODB_URI from apps/web-api/.env (or .env.production with NODE_ENV=production).
 */
const fs = require('fs');
const path = require('path');
const admin = require('firebase-admin');
const { MongoClient } = require('mongodb');

const dryRun = process.argv.includes('--dry-run');

// dotenv-compatible enough for this file: KEY=value, "double quoted" values (which may span lines
// and use \n escapes, as the private key does) and 'single quoted' ones.
function readEnv(file) {
  const text = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
  const env = {};
  const pattern = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*("(?:[^"\\]|\\.)*"|'[^']*'|[^\n#]*)/gm;
  let match;
  while ((match = pattern.exec(text)) !== null) {
    let value = match[2].trim();
    if (value.startsWith('"') && value.endsWith('"')) {
      value = value.slice(1, -1).replace(/\\n/g, '\n').replace(/\\"/g, '"');
    } else if (value.startsWith("'") && value.endsWith("'")) {
      value = value.slice(1, -1);
    }
    env[match[1]] = value;
  }
  return env;
}

// Firestore Timestamps become plain Dates so MongoDB stores them as dates.
function plain(value) {
  if (value && typeof value.toDate === 'function') return value.toDate();
  if (Array.isArray(value)) return value.map(plain);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, inner]) => [key, plain(inner)]));
  }
  return value;
}

async function main() {
  const envFile = path.resolve(__dirname, '..', process.env.NODE_ENV === 'production' ? '.env.production' : '.env');
  const env = { ...readEnv(envFile), ...process.env };

  admin.initializeApp({
    credential: admin.credential.cert({
      projectId: env.FIREBASE_PROJECT_ID,
      clientEmail: env.FIREBASE_CLIENT_EMAIL,
      privateKey: (env.FIREBASE_PRIVATE_KEY || '').replace(/\\n/g, '\n'),
    }),
  });
  const firestore = admin.firestore();
  const mongo = new MongoClient(env.MONGODB_URI || 'mongodb://localhost:27017');
  await mongo.connect();
  const db = mongo.db('config');

  const plan = [
    {
      source: 'platforms',
      target: 'platforms',
      filter: (id, data) => (typeof data.sourceId === 'string' && data.sourceId ? { sourceId: data.sourceId } : { firestoreId: id }),
    },
    { source: 'clients', target: 'clients', filter: (id, data) => ({ name: data.name }) },
    { source: 'media_types', target: 'mediaTypes', filter: (id, data) => ({ name: data.name }) },
    {
      source: 'users',
      target: 'users',
      filter: (id) => ({ uid: id }),
      extra: (id) => ({ uid: id }),
    },
  ];

  let mismatch = false;
  for (const step of plan) {
    const snap = await firestore.collection(step.source).get();
    const target = db.collection(step.target);
    let written = 0;
    let skipped = 0;
    for (const doc of snap.docs) {
      const data = plain(doc.data());
      if (step.source !== 'users' && !data.name) {
        console.log(`  skipped ${step.source}/${doc.id}: no name`);
        skipped++;
        continue;
      }
      const document = { ...data, firestoreId: doc.id, ...(step.extra ? step.extra(doc.id) : {}) };
      if (!dryRun) {
        await target.updateOne(step.filter(doc.id, data), { $set: document }, { upsert: true });
      }
      written++;
    }
    const inMongo = await target.countDocuments();
    console.log(
      `${step.source.padEnd(12)} firestore=${snap.size}  ${dryRun ? 'would write' : 'written'}=${written}  skipped=${skipped}  mongo(${step.target})=${inMongo}`,
    );
    if (!dryRun && inMongo < written) mismatch = true;
  }

  await mongo.close();
  console.log(dryRun ? '\nDry run: nothing was written.' : mismatch ? '\nMismatch: check the counts above.' : '\nDone.');
  process.exit(mismatch ? 1 : 0);
}

main().catch((error) => {
  console.error('Migration failed:', error.code || '', error.message);
  process.exit(1);
});
