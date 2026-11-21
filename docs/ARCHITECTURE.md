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

## Communication

The desktop app calls the backend over HTTP/JSON. The default local endpoint is http://127.0.0.1:5078. The API binds to loopback by default and is intended to be run as a separate local process alongside the WPF frontend.

## Database compatibility

The MariaDB table naming compatibility, existing table-rename migration, legacy JSON import, Argon2id hashes, account soft-delete behavior, incidents, and alert history remain in the backend. Existing account, incident, and history records are not intentionally deleted or reset by the EF Core refactor.
