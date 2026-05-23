import { Entity, ObjectIdColumn, Column } from 'typeorm';

@Entity()
export class Note {
  @ObjectIdColumn()
  id!: string;

  @Column()
  text!: string;

  @Column()
  summary!: string;

  @Column()
  title!: string;

  @Column()
  index!: string;

  @Column()
  program!: string;

  @Column()
  startTime!: string;

  @Column()
  duration!: number;

  @Column()
  alert_id!: string;

  @Column()
  audioLabel!: string;

  @Column()
  message!: string;

  @Column()
  date!: string;

  @Column()
  media!: string;

  @Column()
  mediaName!: string;

  @Column()
  topic!: string;

  @Column()
  subtopic!: string;

  @Column()
  subsubtopic!: string;

  @Column()
  origin!: string;

  @Column()
  department!: string;

  @Column()
  zone!: string;

  @Column()
  rate!: string;

  @Column()
  sentiment!: string;

  @Column()
  value!: string;

  @Column()
  audience!: string;

  @Column()
  link!: string;

  @Column()
  source!: string;

  @Column()
  attachment!: string;
}
