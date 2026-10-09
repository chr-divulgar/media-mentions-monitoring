import { ConflictException, NotFoundException } from '@nestjs/common';
import { ClientsService } from '../clients/clients.service';
import { SettingsService, buildPlatformUpdate } from '../settings/settings.service';
import { FakeCollection } from './fake-collection';
import { UsersBackupService } from './users-backup.service';

function buildDb() {
  return {
    platforms: new FakeCollection(['sourceId']),
    clients: new FakeCollection(['name']),
    mediaTypes: new FakeCollection(['name']),
    users: new FakeCollection(['uid']),
  };
}

describe('SettingsService platforms (MongoDB)', () => {
  const noFirebase = {} as never;
  const noBackup = {} as never;

  it('creates a platform with default slots, lists it by media and exposes its id', async () => {
    const db = buildDb();
    const service = new SettingsService(noFirebase, db as never, noBackup);

    const created = await service.createPlatform({ name: 'CaracolRadio', media: 'radio', sourceId: 'caracol' });
    await service.createPlatform({ name: 'RCN', media: 'tv', sourceId: 'rcn_tv' });

    const radio = await service.getPlatforms('radio');
    expect(radio).toHaveLength(1);
    expect(radio[0]).toMatchObject({ id: created.id, name: 'CaracolRadio', zone: 'Nacional' });
    expect(radio[0].slots.length).toBeGreaterThan(0);
  });

  it('refuses a second platform with the same sourceId', async () => {
    const service = new SettingsService(noFirebase, buildDb() as never, noBackup);
    await service.createPlatform({ name: 'A', media: 'radio', sourceId: 'dup' });

    await expect(service.createPlatform({ name: 'B', media: 'radio', sourceId: 'dup' })).rejects.toBeInstanceOf(ConflictException);
  });

  it("updates only the fields sent: what the worker keeps in the document is not overwritten", async () => {
    const db = buildDb();
    const service = new SettingsService(noFirebase, db as never, noBackup);
    const created = await service.createPlatform({ name: 'A', media: 'radio', sourceId: 'a', streamUrl: 'https://old/live' });
    // The worker excludes the source and re-resolves its stream after the page was loaded.
    db.platforms.docs[0].isExcluded = true;
    db.platforms.docs[0].fallbackStreamUrls = ['https://worker/found'];

    const updated = await service.updatePlatform({ id: created.id, zone: 'Bogotá' });

    expect(updated).toMatchObject({ zone: 'Bogotá', isExcluded: true, fallbackStreamUrls: ['https://worker/found'], streamUrl: 'https://old/live' });
  });

  it('reports a missing platform on update and delete', async () => {
    const service = new SettingsService(noFirebase, buildDb() as never, noBackup);

    await expect(service.updatePlatform({ id: '507f1f77bcf86cd799439011', name: 'x' })).rejects.toBeInstanceOf(NotFoundException);
    await expect(service.updatePlatform({ id: 'not-an-id' })).rejects.toBeInstanceOf(NotFoundException);
    await expect(service.deletePlatform('not-an-id')).rejects.toBeInstanceOf(NotFoundException);
  });

  it('deletes a platform', async () => {
    const db = buildDb();
    const service = new SettingsService(noFirebase, db as never, noBackup);
    const created = await service.createPlatform({ name: 'A', media: 'radio' });

    expect(await service.deletePlatform(created.id)).toEqual({ success: true });
    expect(db.platforms.docs).toHaveLength(0);
  });

  it('buildPlatformUpdate leaves out every field the request does not carry', () => {
    expect(buildPlatformUpdate({ zone: 'Z', audience: 5, rate: undefined })).toEqual({ zone: 'Z', audience: 5 });
    expect(buildPlatformUpdate({})).toEqual({});
  });
});

describe('ClientsService (MongoDB)', () => {
  it('creates, lists sorted by name, updates and deletes clients', async () => {
    const service = new ClientsService(buildDb() as never);

    const b = await service.createClient({ name: 'Bravo' });
    await service.createClient({ name: 'Alfa', words: [{ value: 'petroleo', adds: [] }] });

    expect((await service.getClients()).map((client) => client.name)).toEqual(['Alfa', 'Bravo']);

    const updated = await service.updateClient(b.id as string, { name: 'Bravo', alerts: { radio: ['+573001112233'] } });
    expect(updated).toMatchObject({ name: 'Bravo', alerts: { radio: ['+573001112233'] } });
    expect((await service.getClient(b.id as string)).alerts).toEqual({ radio: ['+573001112233'] });

    await service.deleteClient(b.id as string);
    await expect(service.getClient(b.id as string)).rejects.toBeInstanceOf(NotFoundException);
  });

  it('an update with nothing to change returns the client as it is', async () => {
    const service = new ClientsService(buildDb() as never);
    const created = await service.createClient({ name: 'Alfa' });

    expect(await service.updateClient(created.id as string, {} as never)).toMatchObject({ name: 'Alfa' });
  });

  it('refuses two clients with the same name', async () => {
    const service = new ClientsService(buildDb() as never);
    await service.createClient({ name: 'Alfa' });

    await expect(service.createClient({ name: 'Alfa' })).rejects.toBeInstanceOf(ConflictException);
  });

  it('seeds the default media types on first use, in order, and appends new ones last', async () => {
    const service = new ClientsService(buildDb() as never);

    const first = await service.getMediaTypes();
    expect(first.map((media) => media.name)).toEqual(['internet', 'radio', 'tv', 'prensa', 'redes']);

    const extra = await service.createMediaType({ name: 'cine', label: 'Cine' });
    expect(extra).toMatchObject({ name: 'cine', order: 5 });
    expect((await service.getMediaTypes()).map((media) => media.name).at(-1)).toBe('cine');
  });
});

describe('UsersBackupService', () => {
  it('keeps one copy per user, remembers when it was created and updates only what is sent', async () => {
    const db = buildDb();
    const backup = new UsersBackupService(db as never);

    await backup.upsert('u1', { email: 'u1@x.co', role: 'user', phone: '+57300' });
    const createdAt = db.users.docs[0].createdAt;
    await backup.upsert('u1', { role: 'admin' });

    expect(db.users.docs).toHaveLength(1);
    expect(db.users.docs[0]).toMatchObject({ uid: 'u1', email: 'u1@x.co', role: 'admin', phone: '+57300' });
    expect(db.users.docs[0].createdAt).toBe(createdAt);
    expect(await backup.getRole('u1')).toBe('admin');
    expect(await backup.getRole('nobody')).toBeNull();
  });

  it('removes the copy', async () => {
    const db = buildDb();
    const backup = new UsersBackupService(db as never);
    await backup.upsert('u1', { role: 'user' });

    await backup.remove('u1');

    expect(db.users.docs).toHaveLength(0);
  });

  it('never throws when MongoDB fails', async () => {
    const broken = {
      users: {
        updateOne: async () => { throw new Error('mongo down'); },
        deleteOne: async () => { throw new Error('mongo down'); },
        findOne: async () => { throw new Error('mongo down'); },
      },
    };
    const backup = new UsersBackupService(broken as never);

    await expect(backup.upsert('u1', { role: 'user' })).resolves.toBeUndefined();
    await expect(backup.remove('u1')).resolves.toBeUndefined();
    await expect(backup.getRole('u1')).resolves.toBeNull();
  });
});
