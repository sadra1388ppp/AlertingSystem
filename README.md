# AlertingSystem

AlertingSystem is split into three independent .NET projects:

- **Frontend** — WPF desktop interface. It communicates with the backend only through HTTP/JSON.
- **Backend** — ASP.NET Core Web API. It owns authentication, Argon2id password hashing, MariaDB access, incident/history persistence, security auditing, and endpoint checks.
- **Shared** — request/response contracts only. It contains no database, WPF, or business logic.

## API and security boundary

The backend is a separate local process. By default it listens on **http://127.0.0.1:5078** and accepts only loopback bind addresses. The WPF client also rejects non-loopback API URLs. This is intentional: the current desktop API has no remote-user authentication or TLS deployment configuration, so it must not be exposed to a LAN or the public internet.

- \`GET /health\` and \`GET /health/live\` report process liveness.
- \`GET /health/ready\` verifies that the configured MariaDB database can be reached. The WPF app checks readiness before opening the login screen.
- All application operations are under \`/api\`.
- Security event requests identify an account by ID. The backend reloads the account from MariaDB instead of trusting profile details supplied by the desktop client.
- Alert history is capped at the latest 200 entries by the backend.

To support remote clients later, add real user/session authentication, authorization policies, HTTPS, secret management, and deployment-specific configuration before changing the loopback restriction.

## Database persistence

All application persistence is handled through **Entity Framework Core** in the backend:

- AlertingDbContext defines the account, incident, alert-history, and metadata entities.
- AccountStore uses EF Core queries and SaveChanges() for registration, sign-in, account lookup, and soft deletion.
- MariaDbDataStore uses EF Core LINQ queries and change tracking for incidents and alert history.
- Database initialization and the existing table-rename migration are handled through EF Core APIs.

The application source does not create MySqlCommand objects or contain hand-written SELECT, INSERT, UPDATE, or ALTER TABLE statements. The Pomelo EF Core provider generates database commands internally. The low-level MySqlConnector package remains pinned as the provider's transport dependency; application services do not call it directly.

## Run locally

Use the .NET 10 SDK and MariaDB.

1. Configure the database in \`%LOCALAPPDATA%\\AlertingSystem\\database-settings.json\`, or set \`ALERTINGSYSTEM_DB_CONNECTION_STRING\`.
2. Start the backend in one terminal:

   \`\`\`powershell
   dotnet run --project Backend/AlertingSystem.Backend.csproj
   \`\`\`

3. Verify readiness at [http://127.0.0.1:5078/health/ready](http://127.0.0.1:5078/health/ready).
4. Start the desktop application in a second terminal:

   \`\`\`powershell
   dotnet run --project Frontend/AlertingSystem.Frontend.csproj
   \`\`\`

The backend listens on the loopback address by default. To change its local port or loopback address, set \`ALERTINGSYSTEM_API_LISTEN_URL\`. To change the WPF client's local API address, set \`ALERTINGSYSTEM_API_URL\`. Non-loopback addresses are rejected until remote authentication and HTTPS are implemented.

## Build

\`\`\`powershell
dotnet build AlertingSystem.sln --configuration Release
\`\`\`

The WPF project has no reference to the backend project or to MariaDB/Entity Framework packages; its only project dependency is the shared HTTP contract assembly.
