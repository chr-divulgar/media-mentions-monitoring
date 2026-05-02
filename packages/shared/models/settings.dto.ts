export enum Day {
  Weekday = "weekday",
  Sunday = "sunday",
  Saturday = "saturday",
}

export class SlotDto {
  readonly id?: string;
  readonly platformId?: string;
  readonly day?: Day;
  readonly start?: string;
  readonly end?: string;
  readonly label?: string;
  readonly audioLabel?: string;
  readonly active?: boolean;
  readonly priority?: number;
  readonly tags?: string[];
  readonly keywords?: string[];
}

export class PlatformDto {
  readonly id?: string;
  readonly name?: string;
  readonly url?: string;
  readonly media?: string;
}
