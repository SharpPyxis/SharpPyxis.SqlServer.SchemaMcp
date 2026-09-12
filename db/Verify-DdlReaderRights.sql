-- Proves what a login can and cannot do in a database. Run it connected as the login
-- SharpPyxis.SqlServer.SchemaMcp uses, in the database the server reads: every check reads the rights
-- of the login running it. Each check ends with a verdict, PASS or FAIL.
--
-- It changes nothing. Its one attempt to write runs in a transaction that is rolled back, and is
-- expected to be refused.

set nocount on;

declare @verdicts table (step int, check_name nvarchar(40), verdict char(4), detail nvarchar(400));
declare @count int, @modules int, @table nvarchar(300), @outcome nvarchar(400), @refused bit;

-- A table to try the reads on, named from the catalog.
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

-- 2. It reads no data: no table or view grants it SELECT, and reading one is refused (error 229).
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

-- 3. It writes nothing: no table or view grants it INSERT, UPDATE or DELETE, no procedure EXECUTE, the
-- database no right to create, alter or control, and creating a table is refused (error 262).
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

-- 4. The dependency functions answer: the server reads them to tell what uses an object.
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
