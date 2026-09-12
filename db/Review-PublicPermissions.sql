-- Review-PublicPermissions.sql
--
-- Lists the rights that the role public holds in the current database, outside the catalog views.
--
-- Every user of a database is a member of public, and cannot be removed from it. A right granted to
-- public is therefore held by every user, the login of the MCP server included, on top of the rights
-- granted to it by name. Create-DdlReaderLogin.sql grants that login one right, VIEW DEFINITION; this
-- script shows what it receives anyway, through public.
--
-- Run it as an administrator of the database. The view that lists the rights, sys.database_permissions,
-- only shows a login the rights it is allowed to see: run as the restricted login, the list would be
-- shorter than the truth.
--
-- How to read the result:
--
--   * On a new database, two rows appear: VIEW ANY COLUMN ENCRYPTION KEY DEFINITION and VIEW ANY COLUMN
--     MASTER KEY DEFINITION. They show the metadata of Always Encrypted keys, and no data.
--   * EXECUTE on dbo.sp_creatediagram, sp_alterdiagram, sp_renamediagram and sp_dropdiagram appears in a
--     database where the SSMS diagram designer has been used. These four procedures write, and
--     Create-DdlReaderLogin.sql denies them to its role.
--   * Any other SELECT, INSERT, UPDATE, DELETE or EXECUTE granted to public reads or writes data, for
--     every user of the database. It deserves a look, whatever the MCP server.

select p.class_desc as class,
       -- The descriptive columns of the catalog carry a collation of their own, and a name the one of the
       -- database: each branch is brought to the same one, or the CASE refuses to mix them (error 451).
       case p.class
           when 0 then quotename(db_name()) collate database_default
           when 1 then quotename(s.name) + N'.' + quotename(o.name) collate database_default
           when 3 then quotename(schema_name(p.major_id)) collate database_default
           else concat(p.class_desc, N' ', p.major_id) collate database_default
       end as securable,
       o.type_desc as object_type,
       p.permission_name,
       p.state_desc as state
from sys.database_permissions as p
left join sys.all_objects as o on p.class = 1 and o.object_id = p.major_id
left join sys.schemas as s on s.schema_id = o.schema_id
where p.grantee_principal_id = database_principal_id(N'public')
  and (p.class <> 1 or s.name not in (N'sys', N'INFORMATION_SCHEMA'))
order by class, securable, p.permission_name;
