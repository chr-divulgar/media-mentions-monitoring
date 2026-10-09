# Configuration database (MongoDB `config`)

The configuration the product runs on lives in the **local MongoDB**, database `config` (connection
string `MONGODB_URI`, default `mongodb://localhost:27017`). It used to live in Firestore, whose free
tier ran out and took the login and the worker down with it. Firestore now only holds what user
authentication needs (Firebase Auth and the `users` collection), and MongoDB keeps a backup copy of
the users.

| Collection | Written by | Read by | Notes |
|---|---|---|---|
| `platforms` | web-api (Plataformas screen); the worker changes 3 fields | web-api, worker | A *capture source* is a platform with a `sourceId`. Unique index on `sourceId` (partial). `media` is `tv`, `radio`, `internet`...; the worker maps `tv` to `television` in memory only. |
| `clients` | web-api (Clientes screen) | web-api, worker | `words[]` (`value`, `level`, `adds[]`), `topics` tree, `alerts` / `alertEmails` / `notes` maps (media -> recipients). Unique index on `name`. |
| `mediaTypes` | web-api | web-api | `{name, label, order}`; seeded with internet, radio, tv, prensa, redes. |
| `users` | web-api (backup of every user change and login) | web-api (role when Firestore fails), worker (who is told about platform errors) | Keyed by Firebase `uid`; a copy, never the source of truth. |
| `monitoring.captureHours` | worker | — | Hourly capture coverage audit trail (database `monitoring`). |

## Who writes what in `platforms`

The web-api edits a platform with `$set` of **only the fields in the request**. The worker changes
exactly three fields, each with a one-field `$set` and only when the stored value differs:
`streamUrl`, `fallbackStreamUrls` and `isExcluded`. Neither side rewrites the whole document, so
edits and automatic recovery do not overwrite each other.

## Moving data in from Firestore (once)

```bash
pnpm --filter web-api migrate:firestore -- --dry-run   # read and report, write nothing
pnpm --filter web-api migrate:firestore                # copy platforms, clients, media_types and users
```

It reads Firestore (about one read per document) and upserts into MongoDB by `sourceId` / `name` /
`uid`, so it can be repeated. Run it while the Firestore daily quota is available (it resets at
midnight Pacific time).

## Operating notes

- The worker waits for MongoDB to answer at startup. Make its Windows service depend on MongoDB:
  `sc config MediaMentionsMonitoringWorker depend= MongoDB`.
- The MongoDB itself is not backed up by this project: schedule `mongodump` of `config` and
  `monitoring` if the data matters.
