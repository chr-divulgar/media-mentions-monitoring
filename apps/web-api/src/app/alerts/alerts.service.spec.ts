import { AlertsService } from './alerts.service';
import { TextModel } from '../ai/text-model';

interface Fixtures {
  workerAlerts?: Record<string, unknown>[];
  views?: Record<string, unknown>[];
  notes?: Record<string, unknown>[];
}

// In-memory stand-ins for the Mongo repositories the service uses, picked by entity name.
function build(textModel: TextModel, fixtures: Fixtures = {}) {
  const noteRepo = {
    update: jest.fn().mockResolvedValue({ affected: 1 }),
    find: jest.fn().mockResolvedValue(fixtures.notes ?? []),
  };
  const viewRepo = {
    find: jest.fn().mockResolvedValue(fixtures.views ?? []),
    updateOne: jest.fn().mockResolvedValue({ upsertedCount: 1 }),
  };
  const workerAlertRepo = { find: jest.fn().mockResolvedValue(fixtures.workerAlerts ?? []) };
  const repos: Record<string, unknown> = { Note: noteRepo, AlertView: viewRepo, WorkerAlert: workerAlertRepo };
  const dataSource = { getMongoRepository: (entity: { name: string }) => repos[entity.name] ?? {} };
  return { service: new AlertsService(dataSource as never, textModel), noteRepo, viewRepo, workerAlertRepo };
}

const unusedModel: TextModel = { label: 'unused', complete: jest.fn() };

describe('AlertsService.getChatResponse', () => {
  it('asks the text model for a title and a summary and saves them on the note', async () => {
    const textModel: TextModel = {
      label: 'fake',
      complete: jest.fn().mockResolvedValue('## **Ecopetrol coloca bonos**\nEcopetrol colocó **US$ 2.000 millones**.\nLa demanda triplicó la oferta.'),
    };
    const { service, noteRepo } = build(textModel);

    const result = await service.getChatResponse({ noteId: 'n1', text: 'Ecopetrol vendió bonos…' });

    expect(result).toEqual({
      title: 'Ecopetrol coloca bonos',
      summary: 'Ecopetrol colocó US$ 2.000 millones.\nLa demanda triplicó la oferta.',
    });
    expect(noteRepo.update).toHaveBeenCalledWith({ id: 'n1' }, result);
    const [instructions, input] = (textModel.complete as jest.Mock).mock.calls[0];
    expect(instructions).toContain('título');
    expect(input).toBe('Ecopetrol vendió bonos…');
  });

  it('does not name a provider when it fails', async () => {
    const textModel: TextModel = { label: 'fake', complete: jest.fn().mockRejectedValue(new Error('Ningún modelo de IA respondió.')) };
    const { service, noteRepo } = build(textModel);

    await expect(service.getChatResponse({ noteId: 'n1', text: 'x' })).rejects.toThrow(/Error generando el resumen.*Ningún modelo/);
    expect(noteRepo.update).not.toHaveBeenCalled();
  });
});

describe('AlertsService.getChatResponse: the length of the summary', () => {
  const modelAnswering = (): TextModel => ({ label: 'fake', complete: jest.fn().mockResolvedValue('Titulo\nResumen del audio.') });
  const asked = (textModel: TextModel) => (textModel.complete as jest.Mock).mock.calls[0][0] as string;

  it.each([
    [{ durationSeconds: 25 }, '20 palabras'],
    [{ durationSeconds: 200 }, '40 palabras'],
    [{ durationSeconds: 300 }, '60 palabras'],
    [{ durationSeconds: 900 }, '90 palabras'],
    [{ durationSeconds: 200, economicIndicators: true }, '50 palabras'],
    [{}, '40 palabras'],
  ])('with %j asks for %s', async (extra, words) => {
    const textModel = modelAnswering();
    const { service } = build(textModel);

    await service.getChatResponse({ noteId: 'n1', text: 'audio', ...extra });

    expect(asked(textModel)).toContain(words);
  });
});

describe('AlertsService.getAlerts: what has happened with each alert', () => {
  const day = { startDate: '2026-11-10', endDate: '2026-11-10', source: 'worker' as const };
  type Row = { reviewStatus?: string; viewedAt?: string; noteId?: string };
  const rowsOf = async (service: AlertsService) => (await service.getAlerts(day)) as unknown as Row[];
  const alert = (id: string) => ({ id, endTime: new Date('2026-11-10T15:00:00Z'), clientName: 'Ecopetrol' });

  it('adds the status, when it was first viewed and the note of each worker alert', async () => {
    const viewedAt = new Date('2026-11-10T16:00:00Z');
    const { service, viewRepo, noteRepo } = build(unusedModel, {
      workerAlerts: [alert('a1'), alert('a2'), alert('a3'), alert('a4'), alert('a5')],
      views: [
        { alertId: 'a2', viewedAt },
        { alertId: 'a3', viewedAt },
      ],
      notes: [
        { id: 'n3', alert_id: 'a3', text: 'transcripción' },
        { id: 'n4', alert_id: 'a4', title: 'Titular' },
        { id: 'n5', alert_id: 'a5', title: 'Titular', sentAt: new Date() },
      ],
    });

    const result = await rowsOf(service);

    expect(result.map((r) => r.reviewStatus)).toEqual(['unseen', 'viewed', 'draft', 'saved', 'sent']);
    expect(result[1].viewedAt).toBe(viewedAt.toISOString());
    expect(result[0].viewedAt).toBeUndefined();
    expect(result.map((r) => r.noteId)).toEqual([undefined, undefined, 'n3', 'n4', 'n5']);
    // One query for the views and one for the notes, for all the alerts together.
    expect(viewRepo.find).toHaveBeenCalledTimes(1);
    expect(noteRepo.find).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(viewRepo.find.mock.calls[0][0])).toContain('a1');
  });

  it('asks for nothing when there are no alerts', async () => {
    const { service, viewRepo, noteRepo } = build(unusedModel, { workerAlerts: [] });

    expect(await rowsOf(service)).toEqual([]);
    expect(viewRepo.find).not.toHaveBeenCalled();
    expect(noteRepo.find).not.toHaveBeenCalled();
  });
});

describe('AlertsService.markViewed', () => {
  const alertId = '507f1f77bcf86cd799439011';

  it('records the first view only: an alert that was already opened keeps its first date and person', async () => {
    const { service, viewRepo } = build(unusedModel);

    await service.markViewed(alertId, 'user-1');

    const [filter, update, options] = viewRepo.updateOne.mock.calls[0];
    expect(filter).toEqual({ alertId });
    expect(update.$setOnInsert).toMatchObject({ alertId, viewedBy: 'user-1' });
    expect(update.$setOnInsert.viewedAt).toBeInstanceOf(Date);
    expect(update.$set).toBeUndefined();
    expect(options).toEqual({ upsert: true });
  });

  it('works when nobody is identified (the auth guard only audits for now)', async () => {
    const { service, viewRepo } = build(unusedModel);

    await service.markViewed(alertId, undefined);

    expect(viewRepo.updateOne.mock.calls[0][1].$setOnInsert).not.toHaveProperty('viewedBy');
  });

  it('refuses an id that is not an alert id', async () => {
    const { service, viewRepo } = build(unusedModel);

    await expect(service.markViewed('not-an-id', 'user-1')).rejects.toThrow(/alertId/);
    expect(viewRepo.updateOne).not.toHaveBeenCalled();
  });
});
