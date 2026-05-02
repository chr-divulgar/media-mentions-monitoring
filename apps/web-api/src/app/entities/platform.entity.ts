import { Entity, ObjectIdColumn, Column } from 'typeorm';

@Entity()
export class Platform {
  @ObjectIdColumn()
  id!: string;

  @Column()
  name!: string;

  @Column({ default: '' })
  url!: string;

  @Column()
  media!: string;
}
