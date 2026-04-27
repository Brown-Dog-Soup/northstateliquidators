-- ============================================================================
-- Grant the Function App's system-assigned managed identity access to the
-- NSL inventory database. Run AFTER the Bicep deploy completes successfully,
-- as the SQL Entra admin.
--
-- Usage:
--   sqlcmd -S <sql-server-fqdn> -d sqldb-nsl-prod -G -i grant-function-app-sql-access.sql
--
-- Replace <FUNCTION_APP_NAME> below with the actual func name from the deploy
-- output (default: func-nsl-api).
-- ============================================================================

-- Create a SQL user mapped to the Function App's managed identity.
-- The "FROM EXTERNAL PROVIDER" clause makes Azure look the principal up in
-- Entra ID by name (the Function App's managed identity uses the app name).
DECLARE @sql NVARCHAR(MAX) = N'
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = ''func-nsl-api'')
    CREATE USER [func-nsl-api] FROM EXTERNAL PROVIDER;
';
EXEC sp_executesql @sql;
GO

-- Grant read/write on the schema. Managed identity rotates keys/tokens by
-- itself; no password management needed.
ALTER ROLE db_datareader  ADD MEMBER [func-nsl-api];
ALTER ROLE db_datawriter  ADD MEMBER [func-nsl-api];
GRANT EXECUTE TO [func-nsl-api];

PRINT 'Function App managed identity granted db_datareader, db_datawriter, EXECUTE.';
