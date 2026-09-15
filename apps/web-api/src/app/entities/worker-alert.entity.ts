import { Entity, ObjectIdColumn, Column, CreateDateColumn } from 'typeorm';

// Same shape as Alert (alert.entity.ts), backed by the separate `workerAlert` collection that
// apps/media-core-worker writes to during the shadow-run validation period, so the legacy
// `alert` collection (and w-service's own dedup queries against it) are never touched.
@Entity('workerAlert')
export class WorkerAlert {
  @ObjectIdColumn()
  id!: string;

  @Column()
  text!: string;

  @CreateDateColumn()
  date!: Date;

  @CreateDateColumn()
  startTime!: Date;

  @CreateDateColumn()
  endTime!: Date;

  @Column()
  media!: string;

  @Column()
  words!: string[];

  @Column()
  filePath!: string;

  @Column()
  platform!: string;

  @Column()
  clientName!: string;

  @Column()
  type!: string;
}
