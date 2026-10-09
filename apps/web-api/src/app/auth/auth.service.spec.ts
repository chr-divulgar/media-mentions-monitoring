import { AuthService } from './auth.service';

interface Backup {
  role: string | null;
  upserts: Array<[string, Record<string, unknown>]>;
}

// A Firebase stand-in: the token always verifies as uid "u1" and Firestore holds `storedRole`
// (or fails when `firestoreDown`).
function build(storedRole: string | null, { firestoreDown = false, backup = { role: null, upserts: [] } as Backup } = {}) {
  let reads = 0;
  const firebase = {
    auth: { verifyIdToken: jest.fn().mockResolvedValue({ uid: 'u1', email: 'u1@x.co', name: 'U One' }) },
    firestore: {
      collection: () => ({
        doc: () => ({
          get: async () => {
            reads++;
            if (firestoreDown) throw new Error('Firestore call timed out');
            return { exists: storedRole !== null, data: () => ({ role: storedRole, phone: '+573001112233' }) };
          },
          set: async () => undefined,
        }),
      }),
    },
  };
  const usersBackup = {
    getRole: jest.fn(async () => backup.role),
    upsert: jest.fn(async (uid: string, fields: Record<string, unknown>) => {
      backup.upserts.push([uid, fields]);
    }),
  };
  const service = new AuthService(firebase as never, usersBackup as never);
  return { service, usersBackup, backup, reads: () => reads };
}

describe('AuthService roles', () => {
  const original = process.env.INITIAL_AS_ADMIN;
  afterEach(() => {
    if (original === undefined) delete process.env.INITIAL_AS_ADMIN;
    else process.env.INITIAL_AS_ADMIN = original;
  });

  it("keeps the stored role, 'initial' included, by default", async () => {
    delete process.env.INITIAL_AS_ADMIN;

    expect((await build('initial').service.authenticate('t')).role).toBe('initial');
    expect((await build('user').service.authenticate('t')).role).toBe('user');
  });

  it("gives 'initial' users the admin role while INITIAL_AS_ADMIN is on, and only them", async () => {
    process.env.INITIAL_AS_ADMIN = 'true';

    expect((await build('initial').service.authenticate('t')).role).toBe('admin');
    expect((await build(null).service.authenticate('t')).role).toBe('admin');
    expect((await build('user').service.authenticate('t')).role).toBe('user');
  });
});

describe('AuthService with Firestore down', () => {
  beforeEach(() => {
    delete process.env.INITIAL_AS_ADMIN;
  });

  it("uses the role in the backup copy instead of 'initial'", async () => {
    const { service } = build(null, { firestoreDown: true, backup: { role: 'admin', upserts: [] } });

    expect((await service.authenticate('t')).role).toBe('admin');
  });

  it("falls back to 'initial' only when there is no backup either", async () => {
    const { service } = build(null, { firestoreDown: true });

    expect((await service.authenticate('t')).role).toBe('initial');
  });

  it('does not ask Firestore again for every request after it failed', async () => {
    const { service, reads } = build(null, { firestoreDown: true, backup: { role: 'user', upserts: [] } });

    await service.authenticate('t');
    await service.authenticate('t');
    await service.authenticate('t');

    expect(reads()).toBe(1);
  });
});

describe('AuthService.getProfile', () => {
  beforeEach(() => {
    delete process.env.INITIAL_AS_ADMIN;
  });

  it('reads Firestore once and refreshes the backup copy from it', async () => {
    const { service, reads, backup } = build('user');

    const profile = await service.getProfile('t');

    expect(profile).toEqual({ uid: 'u1', role: 'user' });
    expect(reads()).toBe(1);
    expect(backup.upserts[0]).toEqual([
      'u1',
      expect.objectContaining({ role: 'user', phone: '+573001112233', email: 'u1@x.co' }),
    ]);
  });

  it('answers from the backup copy when Firestore is down', async () => {
    const { service } = build(null, { firestoreDown: true, backup: { role: 'admin', upserts: [] } });

    expect(await service.getProfile('t')).toEqual({ uid: 'u1', role: 'admin' });
  });
});
