# Architecture

## Project boundaries

Frontend is a WPF presentation application. Its service classes are HTTP clients/adapters; they do not connect to MariaDB, execute SQL, hash passwords, or create incidents.

Backend is an ASP.NET Core Web API. It owns the use cases and persistence:
- account registration, authentication, soft deletion, and Argon2id hashing;
- MariaDB configuration, EF Core entity mappings, schema initialization, incidents, and alert history;
- security audit event creation and retry queue;
- outbound HTTP endpoint checks and related incident lifecycle updates.

Shared contains serializable request/response contracts only. It has no project reference to either application.

## Persistence rules

All application database reads and writes go through AlertingDbContext and Entity Framework Core:
- entity sets are represented by DbSet<T>;
- reads and filters use LINQ;
- inserts, updates, and soft deletes use EF Core change tracking and SaveChanges();
- the legacy table rename is represented by an EF Core migration operation.

Backend application services do not create ADO.NET commands or embed SQL statements. Pomelo translates EF Core operations to MariaDB commands through its provider implementation. The pinned MySqlConnector package is only the provider's low-level transport dependency and is not referenced from application service code.

## Communication and trust boundary

The desktop app calls the backend over HTTP/JSON. The default API endpoint is http://127.0.0.1:5078. Both backend binding and frontend target validation enforce loopback-only access. The current API is a local desktop service, not a remotely deployable public API.

The backend exposes liveness and readiness separately:
- \`/health/live\` checks that the process responds;
- \`/health/ready\` checks that the configured MariaDB database is reachable.

The WPF app requires readiness before displaying the sign-in screen. Unhandled API exceptions are returned through Problem Details rather than exposing developer exception pages.

Security audit requests carry an account ID, not a client-authored account profile. The backend looks up the persisted account and uses that authoritative record when writing the audit event. Deleted accounts remain available for recording the deletion and sign-out events, but cannot record a successful login.

## Database compatibility

The MariaDB table naming compatibility, existing table-rename migration, legacy JSON import, Argon2id hashes, account soft-delete behavior, incidents, and alert history remain in the backend. Existing account, incident, and history records are not intentionally deleted or reset by the EF Core refactor. The API returns the newest 200 history entries to the desktop view; older database rows remain stored.
