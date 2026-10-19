# Connect AlertingSystem to MariaDB

For local development, this project uses the MariaDB application user `alertingsystem_app` and the password `123`. This is intentionally simple for the first setup and is **not safe for a shared or internet-accessible machine**. The account is configured for local connections only.

## 1. Run the database setup script in HeidiSQL

1. Start MariaDB and connect in HeidiSQL as an administrator.
2. In the AlertingSystem project on your computer, open `database/setup-mariadb.sql`.
3. Copy the entire SQL script into a HeidiSQL Query tab.
4. Press **F9** to execute it. The script creates the `alertingsystem` database and the local app user with password `123`.

The script sets the password to `123` each time it runs, so it also updates an existing app user.

## 2. Pull the latest code and run the app

From the project folder in the VS Code terminal:

```powershell
git pull origin main
dotnet run
```

The app saves connection settings at:

```text
%LOCALAPPDATA%\AlertingSystem\database-settings.json
```

The default settings use `127.0.0.1`, port `3306`, database `alertingsystem`, user `alertingsystem_app`, and password `123`. You normally do not need to edit this file for local testing.

## 3. Confirm the database was created

In HeidiSQL, refresh the database list. You should see `alertingsystem` and its `Incidents`, `AlertHistory`, and `AppMetadata` tables after the app successfully connects.

If the app reports **Access denied**, first execute the setup SQL script in HeidiSQL as an administrator. If it reports **Can't connect** or **connection refused**, make sure the MariaDB service is running and port `3306` is correct.

## Existing SQLite data

When the MariaDB tables are empty, the app attempts to import data from the local SQLite database at `%LOCALAPPDATA%\AlertingSystem\alertingsystem.db`. If that database is unavailable, it tries the old JSON backup files. The SQLite and JSON files remain untouched as backups. If the MariaDB database already contains incidents or history, the app skips the import.

## Security

The password `123` is a temporary local-development default and is visible in this repository. Do not use it for a shared system or expose MariaDB to the network. Before such use, change it to a strong private password in both the SQL setup and local settings, and restrict MariaDB network access.
