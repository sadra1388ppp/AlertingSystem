# Architecture

## Project boundaries

`Frontend` is a WPF presentation application. Its service classes are HTTP clients/adapters; they do not connect to MariaDB, execute SQL, hash passwords, or create incidents.

`Backend` is an ASP.NET Core Web API. It owns the use cases and persistence:
- account registration, authentication, soft deletion, and Argon2id hashing;
- MariaDB configuration, Entity Framework mappings, migrations, incidents, and alert history;
- security audit event creation and retry queue;
- outbound HTTP endpoint checks and related incident lifecycle updates.

`Shared` contains serializable request/response contracts only. It has no project reference to either application.

## Communication

The desktop app calls the backend over HTTP/JSON. The default local endpoint is `http://127.0.0.1:5078`. The API binds to loopback by default and is intended to be run as a separate local process alongside the WPF frontend.

## Database compatibility

The original MariaDB data store, table naming compatibility, migration, legacy JSON import, and account soft-delete behavior remain in the backend. Existing data is not intentionally dropped or reset by this separation.
