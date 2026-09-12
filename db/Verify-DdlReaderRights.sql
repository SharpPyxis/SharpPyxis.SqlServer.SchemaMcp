-- Verify-DdlReaderRights.sql
--
-- Checks what a login can and cannot do in a database, and gives a verdict, PASS or FAIL, for each check.
-- It is the proof that goes with Create-DdlReaderLogin.sql: that script shows what is granted, this one
-- shows that nothing else is.
--
-- Run it connected AS THE LOGIN the MCP server uses, in the database the MCP server reads. Every check
-- reads the rights of the connection running it: run by an administrator, it would answer for the
-- administrator. In SSMS, open a new connection, choose "SQL Server Authentication", type the name and
-- the password of the login, then select the database.
--
-- The script changes nothing. Its one attempt to write, the creation of a table, runs inside a
-- transaction that is rolled back: even if the attempt succeeded, nothing would remain.
--
-- The four checks:
--
--   1. The login sees the definitions: it counts the objects it can see, and the module texts it can read.
--   2. The login reads no data: no table or view grants it SELECT, and an actual read of a table of the
--      database is refused by SQL Server with error 229.
--   3. The login writes nothing: it holds no right to insert, update, delete or execute, no right to
--      create, alter or control anything in the database, and an actual attempt to create a table is
--      refused with error 262.
--   4. The dependency functions answer: the MCP server reads them to tell what uses an object.
--
-- A FAIL on check 2 or 3 means the login holds more rights than it should: through another role, through
-- a right granted to public, or because it is an administrator. The detail column says what was found.
-- Run as a sysadmin, this script fails on both checks, as it should.

set nocount on;

declare @verdicts table (step int, check_name nvarchar(40), verdict char(4), detail nvarchar(400));
declare @count int, @modules int, @table nvarchar(300), @outcome nvarchar(400), @refused bit;

-- The table the reads are tried on: the first user table of the database, by name, taken from the catalog.
select top (1) @table = quotename(schema_name(schema_id)) + N'.' + quotename(name)
from sys.tables
where is_ms_shipped = 0
order by name;

-- 1. It sees the definitions.
select @count = count(*) from sys.objects where is_ms_shipped = 0;
select @modules = count(*) from sys.sql_modules where definition is not null;

insert into @verdicts
values (1, N'sees the definitions', iif(@count > 0, 'PASS', 'FAIL'),
        concat(@count, N' objects visible, ', @modules, N' module texts readable'));

-- 2. It reads no data. First, what the rights say: the number of tables and views that grant it SELECT,
--    as SQL Server computes them for the current login. Then what the engine does: an actual read.
select @count = count(*)
from sys.objects as o
cross apply (select quotename(schema_name(o.schema_id)) + N'.' + quotename(o.name) as name) as q
where o.is_ms_shipped = 0
  and o.type in ('U', 'V')
  and has_perms_by_name(q.name, N'OBJECT', N'SELECT') = 1;

set @refused = 1;
if @table is null
    set @outcome = N'no table to try a read on';
else
begin
    begin try
        -- The one dynamic statement of this script: the name of the table comes from the catalog, quoted.
        -- The row read, if any, goes into a variable and is never displayed.
        exec (N'declare @x int; select top (1) @x = 1 from ' + @table + N';');
        select @refused = 0, @outcome = concat(N'read a row of ', @table);
    end try
    begin catch
        select @refused = iif(error_number() = 229, 1, 0),
               @outcome = iif(error_number() = 229,
                              concat(N'reading ', @table, N' refused (229)'),
                              concat(N'error ', error_number(), N': ', error_message()));
    end catch;
end;

insert into @verdicts
values (2, N'reads no data', iif(@count = 0 and @refused = 1, 'PASS', 'FAIL'),
        concat(@count, N' tables or views readable; ', @outcome));

-- 3. It writes nothing. First, what the rights say: rights to change a table or a view, rights to
--    execute a procedure, since a procedure can write, and rights on the database itself, such as
--    creating or altering objects. Then what the engine does: an actual attempt to create a table.
select @count = count(*)
from sys.objects as o
cross apply (select quotename(schema_name(o.schema_id)) + N'.' + quotename(o.name) as name) as q
where (o.type in ('U', 'V')
       and o.is_ms_shipped = 0
       and (has_perms_by_name(q.name, N'OBJECT', N'INSERT') = 1
            or has_perms_by_name(q.name, N'OBJECT', N'UPDATE') = 1
            or has_perms_by_name(q.name, N'OBJECT', N'DELETE') = 1))
   or (o.type in ('P', 'PC', 'X')
       and has_perms_by_name(q.name, N'OBJECT', N'EXECUTE') = 1);

select @count += count(*)
from fn_my_permissions(null, N'DATABASE')
where permission_name like N'CREATE%'
   or permission_name like N'ALTER%'
   or permission_name like N'IMPERSONATE%'
   or permission_name like N'BACKUP%'
   or permission_name in (N'CONTROL', N'INSERT', N'UPDATE', N'DELETE', N'EXECUTE', N'TAKE OWNERSHIP');

begin try
    begin transaction;
    create table dbo.schema_mcp_write_probe (id int);
    rollback transaction;
    select @refused = 0, @outcome = N'created a table';
end try
begin catch
    if @@trancount > 0
        rollback transaction;

    select @refused = iif(error_number() = 262, 1, 0),
           @outcome = iif(error_number() = 262,
                          N'creating a table refused (262)',
                          concat(N'error ', error_number(), N': ', error_message()));
end catch;

insert into @verdicts
values (3, N'writes nothing', iif(@count = 0 and @refused = 1, 'PASS', 'FAIL'),
        concat(@count, N' rights to write held; ', @outcome));

-- 4. The dependency functions answer. find_references, one of the tools of the MCP server, relies on
--    sys.dm_sql_referencing_entities, which works with VIEW DEFINITION alone.
if @table is null
    insert into @verdicts values (4, N'reads the dependencies', 'PASS', N'no table to try them on');
else
begin
    begin try
        select @count = count(*) from sys.dm_sql_referencing_entities(@table, N'OBJECT');
        insert into @verdicts
        values (4, N'reads the dependencies', 'PASS', concat(@count, N' objects reference ', @table));
    end try
    begin catch
        insert into @verdicts
        values (4, N'reads the dependencies', 'FAIL', concat(N'error ', error_number(), N': ', error_message()));
    end catch;
end;

select step, check_name, verdict, detail
from @verdicts
order by step;

select iif(exists (select 1 from @verdicts where verdict = 'FAIL'),
           N'FAIL: this login does not match what the server needs, and nothing more.',
           N'PASS: this login reads the definitions, and neither reads nor writes the data.') as conclusion;
