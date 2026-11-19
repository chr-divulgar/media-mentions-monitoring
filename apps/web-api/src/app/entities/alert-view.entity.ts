import { Entity, ObjectIdColumn, Column } from 'typeorm';

/**
 * The first time someone opened an alert's editor (shared by the whole team). A collection of its own, not a field of
 * `workerAlert`: the worker writes those documents and must not be able to wipe this.
 */
@Entity('alertView')
export class AlertView {
  @ObjectIdColumn()
  id!: string;

  /** The id of the worker alert, as a string. */
  @Column()
  alertId!: string;

  @Column()
  viewedAt!: Date;

  /** The uid of whoever opened it; absent while the auth guard only audits. */
  @Column()
  viewedBy?: string;
}
