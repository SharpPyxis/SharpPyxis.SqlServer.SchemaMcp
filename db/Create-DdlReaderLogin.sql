-- Creates the login SharpPyxis.SqlServer.SchemaMcp connects with, and grants it one right: to read the
-- definitions of the objects of a database. It reads no row, and writes nothing.
--
-- Run it in SQLCMD mode (in SSMS: Query > SQLCMD Mode), as a login allowed to create logins and users.
-- The names below are examples: change them, and the password, before running.

:setvar Login    ddl_reader_mcp
:setvar Password "Change-this-password-1"
:setvar Role     schema_mcp_reader
:setvar Database demo

use master;
go

create login [$(Login)]
    with password = N'$(Password)', check_policy = on, default_database = [$(Database)];
go

use [$(Database)];
go

create user [$(Login)] for login [$(Login)];
go

-- A role rather than a grant to the user: a Windows account, or a second login, joins it without a
-- second grant to review.
create role [$(Role)];
alter role [$(Role)] add member [$(Login)];
go

-- The one right granted. VIEW DEFINITION reads the text and the structure of objects, never their rows.
-- Whole database:
grant view definition to [$(Role)];
-- One schema instead: comment out the line above, and uncomment this one.
-- grant view definition on schema::[sales] to [$(Role)];
go

-- public grants EXECUTE on the four procedures of the SSMS diagram designer, which write into
-- dbo.sysdiagrams: the only doors to a write in the default rights of a database. Each gets a deny
-- where it exists. No other deny: one set on the whole database would also refuse the catalog views
-- of sys, which are what the server reads.
if object_id(N'dbo.sp_creatediagram') is not null deny execute on dbo.sp_creatediagram to [$(Role)];
if object_id(N'dbo.sp_alterdiagram') is not null deny execute on dbo.sp_alterdiagram to [$(Role)];
if object_id(N'dbo.sp_renamediagram') is not null deny execute on dbo.sp_renamediagram to [$(Role)];
if object_id(N'dbo.sp_dropdiagram') is not null deny execute on dbo.sp_dropdiagram to [$(Role)];
go

-- To check what the login can and cannot do, connect as it and run Verify-DdlReaderRights.sql.
