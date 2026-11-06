-- CLR objects of every kind, added by the tests to the demo database. They stay out of
-- db/Create-DemoDatabase.sql, which runs on any database: a CLR object needs an assembly, and the
-- assembly a database marked TRUSTWORTHY. The fixture creates the assembly, clr_sample, before this
-- script. Nothing here is ever executed: the tests read the objects from the catalog.

create schema clr;
go

create table clr.journal (journal_id int not null constraint pk_journal primary key, label nvarchar(100) null);
go

-- Trap: a procedure with no text. Its body is in the assembly: list_objects has no length to give,
-- and search_modules nothing to read.
create procedure clr.greet @name nvarchar(100), @greeting nvarchar(200) output
as external name clr_sample.[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Samples].Greet;
go

create function clr.twice (@value int) returns int
as external name clr_sample.[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Samples].Twice;
go

-- Trap: SMO writes the table this function returns, with a COLLATE on its text column equal to the
-- database's, which script_object removes as it does for a table.
create function clr.split (@text nvarchar(max)) returns table (item nvarchar(100))
as external name clr_sample.[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Samples].Split;
go

-- Trap: an aggregate, which SMO keeps in a collection of its own.
create aggregate clr.concat (@value nvarchar(4000)) returns nvarchar(4000)
external name clr_sample.[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Concat];
go

create trigger clr.tr_journal_touch on clr.journal after insert
as external name clr_sample.[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Samples].Touch;
go
