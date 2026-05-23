export enum Day {
  Weekday = "weekday",
  Saturday = "saturday",
  Sunday = "sunday",
}

export class SlotDto {
  readonly day!: Day;
  readonly start!: string;
  readonly end!: string;
  readonly label!: string;
  readonly audioLabel!: string;
  readonly audience?: number;
  readonly rate?: number;
}

export class PlatformDto {
  readonly id?: string;
  readonly name?: string;
  readonly url?: string;
  readonly media?: string;
  readonly zone?: string;
  readonly city?: string;
  readonly slots?: SlotDto[];
  /** Audiencia general (solo para medios no audiovisuales) */
  readonly audience?: number;
  /** Tarifa general (solo para medios no audiovisuales) */
  readonly rate?: number;
}

export interface PlatformResponseDto {
  id: string;
  name: string;
  url: string;
  media: string;
  zone: string;
  city: string;
  slots: SlotDto[];
  audience?: number;
  rate?: number;
}
