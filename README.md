# AlertingSystem

AlertingSystem is split into three independent .NET projects:

- **Frontend** — WPF desktop interface. It communicates only with the backend through HTTP/JSON.
- **Backend** — ASP.NET Core Web API. It owns authentication, Argon2id password hashing, MariaDB access, incident/history persistence, security auditing, and endpoint checks.
- **Shared** — API request/response contracts only. It contains no database, WPF, or business logic.

## Database persistence

All application persistence is handled through **Entity Framework Core** in the backend:

- AlertingDbContext defines the account, incident, alert-history, and metadata entities.
- AccountStore uses EF Core queries and SaveChanges() for registration, sign-in, account lookup, and soft deletion.
- MariaDbDataStore uses EF Core LINQ queries and change tracking for incidents and alert history.
- Database initialization and the existing table-rename migration are handled through EF Core APIs.

The application source does not create MySqlCommand objects or contain hand-written SELECT, INSERT, UPDATE, or ALTER TABLE statements. The Pomelo EF Core provider generates database commands internally. The low-level MySqlConnector package remains pinned as the provider's transport dependency; application services do not call it directly.

## Run locally

Use .NET 10 SDK and MariaDB.

1. Configure the database in %LOCALAPPDATA%\\AlertingSystem\\database-settings.json, or set ALERTINGSYSTEM_DB_CONNECTION_STRING.
2. Start the backend in one terminal:

   dotnet run --project Backend/AlertingSystem.Backend.csproj

   The API listens on http://127.0.0.1:5078 by default. Open http://127.0.0.1:5078/health to check it.
3. Start the desktop application in a second terminal:

   dotnet run --project Frontend/AlertingSystem.Frontend.csproj

The frontend checks the backend health endpoint before showing the sign-in screen. To use a different API address, set ALERTINGSYSTEM_API_URL for the frontend process (for example, http://127.0.0.1:5078/). To change the backend listening address, set ALERTINGSYSTEM_API_LISTEN_URL.

## Build

dotnet build AlertingSystem.sln --configuration Release

The backend is a separate process. The WPF project has no reference to the backend project or to MariaDB/Entity Framework packages; its only project dependency is the shared HTTP contract assembly.
