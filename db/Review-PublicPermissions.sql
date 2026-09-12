-- Lists what the role public is granted in the current database, outside the catalog views. Every user
-- of the database holds these rights, the login of SharpPyxis.SqlServer.SchemaMcp included, on top of
-- what it is granted by name.
--
-- Run it as a database administrator: sys.database_permissions shows a login only the rights it can
-- see, so run as the restricted login it would list less than there is.
--
-- The rows to read are the rights that write. In a database where the SSMS diagram designer has been
-- used, EXECUTE on dbo.sp_creatediagram, sp_alterdiagram, sp_renamediagram and sp_dropdiagram: the
-- four procedures Create-DdlReaderLogin.sql denies.

select p.class_desc as class,
       -- The descriptive columns of the catalog carry a collation of their own, and a name the one of
       -- the database: each branch is brought to the same one, or the CASE refuses to mix them (451).
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
