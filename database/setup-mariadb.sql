-- LOCAL DEVELOPMENT ONLY: this script uses the easy password "123".
-- The app database user is limited to local connections. Choose a strong password
-- before using this on any shared or internet-accessible machine.

CREATE DATABASE IF NOT EXISTS alertingsystem
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

CREATE USER IF NOT EXISTS 'alertingsystem_app'@'localhost'
  IDENTIFIED BY '123';

CREATE USER IF NOT EXISTS 'alertingsystem_app'@'127.0.0.1'
  IDENTIFIED BY '123';

-- Ensure the password is "123" even if you ran an earlier version of this script.
ALTER USER 'alertingsystem_app'@'localhost' IDENTIFIED BY '123';
ALTER USER 'alertingsystem_app'@'127.0.0.1' IDENTIFIED BY '123';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, INDEX
  ON alertingsystem.* TO 'alertingsystem_app'@'localhost';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, INDEX
  ON alertingsystem.* TO 'alertingsystem_app'@'127.0.0.1';

FLUSH PRIVILEGES;
