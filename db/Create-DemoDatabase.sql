-- Demo database of SharpPyxis.SqlServer.SchemaMcp, and the one its integration tests run against.
--
-- Run it in an empty database: it creates four schemas and about twenty objects, and no data —
-- the server reads structure only. It lets anyone try every tool without going near a database
-- that matters.
--
-- Several objects are traps on purpose, and each one says which.

create schema sales;
go
create schema stock;
go
create schema legacy;
go
create schema audit;
go

-- sales holds half of the objects, and most of its names share the order_ prefix: what the spread
-- falls back on when a single schema is left.

create table sales.customer
(
    customer_id int not null constraint pk_customer primary key,
    name nvarchar(200) not null,
    siret char(14) null
);
go

create table sales.order_header
(
    order_id int not null constraint pk_order_header primary key,
    customer_id int not null constraint fk_order_header_customer references sales.customer (customer_id),
    ordered_at datetime2 not null
);
go

create table sales.order_line
(
    order_id int not null constraint fk_order_line_order_header references sales.order_header (order_id),
    line_number int not null,
    item_code varchar(20) not null,
    quantity decimal(18, 3) not null,
    unit_price decimal(18, 4) not null,
    constraint pk_order_line primary key (order_id, line_number)
);
go

create sequence sales.order_number as int start with 1;
go

create view sales.order_summary
as
select h.order_id, h.customer_id, count(*) as line_count
from sales.order_header as h
join sales.order_line as l on l.order_id = h.order_id
group by h.order_id, h.customer_id;
go

create function sales.order_total (@order_id int)
returns decimal(18, 4)
as
begin
    return (select sum(quantity * unit_price) from sales.order_line where order_id = @order_id);
end;
go

create procedure sales.order_place @customer_id int
as
begin
    insert into sales.order_header (order_id, customer_id, ordered_at)
    values (next value for sales.order_number, @customer_id, sysdatetime());
end;
go

create procedure sales.order_cancel @order_id int
as
begin
    delete from sales.order_line where order_id = @order_id;
    delete from sales.order_header where order_id = @order_id;
end;
go

create procedure sales.customer_find @name nvarchar(200)
as
select customer_id, name from sales.customer where name like @name;
go

-- Trap: the table is named inside a string only. The dependency graph cannot see it; a search
-- through the text of the modules does.
create procedure sales.order_export @order_id int
as
begin
    declare @sql nvarchar(max) = N'select * from sales.order_line where order_id = @id';
    exec sys.sp_executesql @sql, N'@id int', @id = @order_id;
end;
go

-- Trap: a run of blank lines inside the text. script_object collapses it, the stored definition
-- keeps it, so the line numbers of the two differ past this point.
create procedure sales.order_report
as
begin
    select order_id, sales.order_total(order_id) as total
    from sales.order_summary;




    select count(*) as customers from sales.customer;
end;
go

-- Trap: item_a and itemXa differ only where LIKE reads _ as any character.

create table stock.item_a (item_code varchar(20) not null constraint pk_item_a primary key);
go

create table stock.itemXa (item_code varchar(20) not null constraint pk_itemXa primary key);
go

create procedure stock.item_restock @item_code varchar(20)
as
select sum(quantity) as ordered from sales.order_line where item_code = @item_code;
go

-- Trap: the same name as sales.customer, so an unqualified name is ambiguous. It also carries a
-- siret column, like its namesake.
create table legacy.customer (customer_id int not null, name nvarchar(200) null, siret char(14) null);
go

-- Traps: names holding the characters LIKE reads as a pattern.
create table legacy.[100%_done] (id int not null);
go

create table legacy.[odd[name] (id int not null);
go

-- Trap: a view left behind by a dropped column. It no longer compiles, and asking for its
-- dependencies at column level fails.
create table legacy.contact (contact_id int not null, name nvarchar(200) null, fax varchar(20) null);
go

create view legacy.contact_list
as
select contact_id, name, fax from legacy.contact;
go

alter table legacy.contact drop column fax;
go

-- Trap: audit.journal does not exist. SQL Server creates the procedure anyway, and it fails only
-- when it runs.
create procedure audit.audit_write @message nvarchar(400)
as
insert into audit.journal (message, written_at) values (@message, sysdatetime());
go

-- A reference to another database.
create procedure audit.audit_copy
as
select name, date_modified from msdb.dbo.sysjobs;
go
