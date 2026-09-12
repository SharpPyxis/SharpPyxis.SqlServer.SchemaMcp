-- Create-DdlReaderLogin.sql
--
-- Creates the login that SharpPyxis.SqlServer.SchemaMcp, an MCP server, uses to connect to a SQL Server
-- database, and grants it a single right: VIEW DEFINITION. With that right, the login reads the structure
-- of the database: the tables and their columns, the text of views, procedures and functions, the
-- indexes, the keys. It reads no row of any table, and it can change nothing.
--
-- This is the principle of least privilege. A login receives what its task needs, and nothing more. The
-- MCP server needs to read definitions. It never needs to read data, and it never needs to write.
--
-- Before running this script:
--
--   1. The instance must accept SQL Server authentication, also called mixed mode. To check it:
--
--          select serverproperty('IsIntegratedSecurityOnly');
--
--      A result of 1 means Windows authentication only. On such an instance, this script still creates
--      the login, but every connection with it is refused (error 18456). Switching to mixed mode is done
--      in the properties of the server, on the Security page, and needs a restart of the service.
--
--   2. Run it as a login allowed to create logins and users: in practice, an administrator of the
--      instance.
--
--   3. Run it in SQLCMD mode. The four :setvar lines below are commands of sqlcmd, not T-SQL. SSMS
--      understands them only in SQLCMD mode (menu Query > SQLCMD Mode). Without it, the script stops on
--      its first line, and nothing is created.
--
--   4. Change the four values below. The names are examples. The password must be yours: it is checked
--      against the password policy of Windows.
--
-- After running it, connect to the database as the new login, and run Verify-DdlReaderRights.sql. That
-- script checks what the login can and cannot do. This one, run by an administrator, cannot tell.

:setvar Login    ddl_reader_mcp
:setvar Password "Change-this-password-1"
:setvar Role     schema_mcp_reader
:setvar Database demo

-- 1. The login. It is created at the level of the instance, in master, and it is what opens a
--    connection. check_policy applies the password rules of Windows, such as length and complexity.
--    default_database is the database a connection lands in when it names none.
use master;
go

create login [$(Login)]
    with password = N'$(Password)', check_policy = on, default_database = [$(Database)];
go

-- 2. The user. A login opens a connection to the instance; to enter a database, it needs a user in that
--    database, mapped to it.
use [$(Database)];
go

create user [$(Login)] for login [$(Login)];
go

-- 3. A role holds the right, and the user becomes a member of it. When another account needs the same
--    access, such as a second login or a Windows account, it joins the role, and no new right has to be
--    granted or reviewed. For a Windows account, for example:
--
--        create login [DOMAIN\someone] from windows;                -- in master
--        create user [DOMAIN\someone] for login [DOMAIN\someone];   -- in the database
--        alter role [schema_mcp_reader] add member [DOMAIN\someone];
create role [$(Role)];
alter role [$(Role)] add member [$(Login)];
go

-- 4. The one right granted. VIEW DEFINITION reads the definition of objects: columns and their types,
--    the text of modules, indexes, constraints. It does not read the rows of a table, it does not run a
--    procedure, and it changes nothing. Every tool of the MCP server works with this right alone.
--
--    By default the right covers the whole database. To limit it to one schema, comment out the first
--    line below and uncomment the second, with the name of your schema. The MCP server is not told of
--    that limit: it simply sees fewer objects. The limit lives in the rights, where you control it.
grant view definition to [$(Role)];
-- grant view definition on schema::[sales] to [$(Role)];
go

-- 5. The only writes left open by the default rights of a database. When database diagrams are used in
--    SSMS, four procedures are installed in the dbo schema. They write into the table dbo.sysdiagrams,
--    and the role public, which every user of the database belongs to, may execute them. Each one gets
--    a deny, when it exists.
--
--    No other deny, on purpose. A deny on the whole database, such as a deny select, would also refuse
--    the catalog views of the sys schema, the views that describe the objects: exactly what the MCP
--    server reads. The login would see nothing. Rights that your database grants to public beyond the
--    defaults stay granted; Review-PublicPermissions.sql lists them.
if object_id(N'dbo.sp_creatediagram') is not null deny execute on dbo.sp_creatediagram to [$(Role)];
if object_id(N'dbo.sp_alterdiagram') is not null deny execute on dbo.sp_alterdiagram to [$(Role)];
if object_id(N'dbo.sp_renamediagram') is not null deny execute on dbo.sp_renamediagram to [$(Role)];
if object_id(N'dbo.sp_dropdiagram') is not null deny execute on dbo.sp_dropdiagram to [$(Role)];
go

-- To undo what this script created, as an administrator, with your own names:
--
--     use [your_database];
--     drop user [ddl_reader_mcp];
--     drop role [schema_mcp_reader];
--     use master;
--     drop login [ddl_reader_mcp];
