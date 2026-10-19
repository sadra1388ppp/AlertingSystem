# Connect AlertingSystem to MariaDB

AlertingSystem stores incidents and alert history in the MariaDB database named \`alertingsystem\`. The app creates its tables automatically on first connection.

## 1. Create the database and app user

1. Start your MariaDB server.
2. Open HeidiSQL and connect as an administrator.
3. Open \`database/setup-mariadb.sql\` from this repository.
4. Replace both occurrences of \`CHANGE_THIS_PASSWORD\` with one strong password. For the easiest setup, avoid single quotes in the password.
5. Run the SQL script.

The script creates a dedicated application user. Do not use the MariaDB root account in the app.

## 2. Create the local connection settings

From the AlertingSystem project folder, run:

\`\`\`powershell
git pull origin main
dotnet run
\`\`\`

On the first launch, the app creates this local settings file and shows a message telling you to edit it:

\`\`\`text
%LOCALAPPDATA%\\AlertingSystem\\database-settings.json
\`\`\`

Open the file, enter the same password used in the SQL script, and save it. The file should look like this:

\`\`\`json
{
  "Server": "127.0.0.1",
  "Port": 3306,
  "Database": "alertingsystem",
  "UserId": "alertingsystem_app",
  "Password": "YOUR_PASSWORD_HERE"
}
\`\`\`

Change the port if your MariaDB server uses a port other than 3306. The settings file is outside the Git repository, so the password is not committed to GitHub. It is stored as local text; do not share that file.

## 3. Start the app

Run this again from the project folder:

\`\`\`powershell
dotnet run
\`\`\`

The app will connect to MariaDB and create its tables. Incidents and alert history will then be loaded from and saved to MariaDB.

## Existing SQLite data

On the first connection, if the MariaDB tables are empty, the app imports data from the existing local SQLite database at \`%LOCALAPPDATA%\\AlertingSystem\\alertingsystem.db\`. If that database is unavailable, it tries the old JSON backup files. The SQLite and JSON files are left untouched as backups.

If the MariaDB database already contains incidents or history, the app skips the import instead of merging or overwriting existing database data.

## Troubleshooting

- **Access denied:** verify the password in \`database-settings.json\` exactly matches the SQL script and that the script ran successfully.
- **Can't connect / connection refused:** verify the MariaDB service is running and the server and port are correct.
- **Unknown database:** run the SQL setup script using an administrator account.
- **Database changes don't appear on another machine:** each app instance must connect to the same MariaDB server and database; a local \`127.0.0.1\` address refers to the machine running the app.
