export class WordAddDto {
  readonly before?: string;
  readonly after?: string;
}

export class WordDto {
  readonly value!: string;
  readonly adds!: WordAddDto[];
}

export class ClientDto {
  readonly id?: string;
  readonly name!: string;
  readonly words?: WordDto[];
  /** mediaName → array of user phone numbers */
  readonly alerts?: Record<string, string[]>;
  /** mediaName → array of user phone numbers */
  readonly notes?: Record<string, string[]>;
}

export class MediaTypeDto {
  readonly id?: string;
  /** Internal key: internet | radio | tv | prensa | redes */
  readonly name!: string;
  /** Display label shown in the UI */
  readonly label!: string;
}
