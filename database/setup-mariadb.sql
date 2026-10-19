-- Run this script once while connected to MariaDB as an administrator.
-- Replace CHANGE_THIS_PASSWORD with the same strong password you put in
-- %LOCALAPPDATA%\AlertingSystem\database-settings.json.
-- For easy setup, use a password without single quotes.

CREATE DATABASE IF NOT EXISTS alertingsystem
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

CREATE USER IF NOT EXISTS 'alertingsystem_app'@'localhost'
  IDENTIFIED BY 'CHANGE_THIS_PASSWORD';

CREATE USER IF NOT EXISTS 'alertingsystem_app'@'127.0.0.1'
  IDENTIFIED BY 'CHANGE_THIS_PASSWORD';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, INDEX
  ON alertingsystem.* TO 'alertingsystem_app'@'localhost';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, INDEX
  ON alertingsystem.* TO 'alertingsystem_app'@'127.0.0.1';

FLUSH PRIVILEGES;
