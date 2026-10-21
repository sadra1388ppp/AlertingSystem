# AlertingSystem database

AlertingSystem uses MariaDB with Entity Framework Core. Ordinary reads and writes use C# and EF Core; table creation and the one-time legacy table rename are managed automatically.

## Database and table names

The existing database is kept as `alertingsystem` so the app preserves its data and current user grants. Its name is already short and lowercase; moving the database itself would require a separate data-and-permissions migration.

The table names are simplified automatically:

| Table | Contents |
|---|---|
| `incidents` | Incident records and status |
| `checks` | Endpoint check history |
| `settings` | Internal app metadata |

On startup the app applies the EF Core migration that renames the previous tables (`Incidents`, `AlertHistory`, `AppMetadata`) without deleting their rows. A new database is bootstrapped through the same migration.

## Local settings

On first launch the app creates this settings file if it does not exist:

`%LOCALAPPDATA%\AlertingSystem\database-settings.json`

- Server: `127.0.0.1`
- Port: `3306`
- Database: `alertingsystem`
- User ID: `alertingsystem_app`
- Password: `123`

These are local-development defaults only. Do not use the example password on a shared or internet-accessible machine. Credentials remain outside the repository. You can override the connection string with the `ALERTINGSYSTEM_DB_CONNECTION_STRING` environment variable.

## Deletion and synchronization rules

- The app reloads database records every 3 seconds while it is open. A row deleted directly from MariaDB disappears from the app at the next refresh.
- Deleting an incident or check-history item in the app only hides it from the app. The row stays in MariaDB.
- App-hidden identifiers are stored in `%LOCALAPPDATA%\AlertingSystem\hidden-items.json`. If the corresponding database row is deleted directly, its local hidden marker is removed.
- Saving a new check or updating an incident never deletes database rows. Old history can remain in MariaDB even when the app only displays the latest 200 entries.

The local app account needs permission to read and write records and to apply this one-time table rename migration.
