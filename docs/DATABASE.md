# MariaDB storage (EF Core)

AlertingSystem stores incidents and endpoint-check history in MariaDB through Entity Framework Core. The app contains no handwritten SQL queries or table-creation scripts; EF Core sends the database commands.

## Local settings

On first launch the app creates this settings file if it does not exist:

`%LOCALAPPDATA%\AlertingSystem\database-settings.json`

Default local-development settings:

- Server: `127.0.0.1`
- Port: `3306`
- Database: `alertingsystem`
- User ID: `alertingsystem_app`
- Password: `123`

These defaults match the local development account used by the earlier AlertingSystem setup. They are for local development only. Do not use the example password on any shared or internet-accessible machine. Credentials are kept outside the repository.

You can override the connection string with the `ALERTINGSYSTEM_DB_CONNECTION_STRING` environment variable.

The MariaDB server and the configured database account must already exist and be allowed to create tables. At startup, EF Core creates the mapped tables automatically when the database has no tables. The model deliberately maps to the existing `Incidents`, `AlertHistory`, and `AppMetadata` table layout, including the existing text-formatted timestamp columns.

## Existing local data

If the MariaDB tables are empty and this app has old `incidents.json` or `check-history.json` files under LocalAppData, AlertingSystem imports those records once. If MariaDB already has records, it does not merge stale JSON into the database. The original JSON files are not deleted.

## Dependency compatibility

The app targets .NET 10 and uses the stable Pomelo 9 provider with EF Core 9. The released Pomelo 9 provider targets EF Core 9; it is not pinned to the unsupported assumption that Pomelo 9 is an EF Core 10 provider.


## Deleting items in the app

Deleting an incident or an AlertHistory entry in the UI only hides it from this app. It does not delete its MariaDB row. Hidden identifiers are stored locally in `%LOCALAPPDATA%\\AlertingSystem\\hidden-items.json`. If a row is independently deleted in MariaDB, the app's three-second refresh removes it from the visible list and cleans up the obsolete local hide marker. Save operations only update existing rows or insert new records; they never delete database rows.
