import { AlertsService } from './alerts.service';
import { TextModel } from '../ai/text-model';

function build(textModel: TextModel) {
  const noteRepo = { update: jest.fn().mockResolvedValue({ affected: 1 }) };
  const dataSource = { getMongoRepository: () => noteRepo };
  return { service: new AlertsService(dataSource as never, textModel), noteRepo };
}

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
