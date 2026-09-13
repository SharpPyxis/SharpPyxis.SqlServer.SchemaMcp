# SharpPyxis.SqlServer.SchemaMcp

Read-only MCP server that lets an AI agent read the structure of a SQL Server database — objects, code,
dependencies, columns — and never its data.

## The idea

How do you let someone work on a database without showing them the data it holds? The question is as
old as databases themselves, and SQL Server answers it with rights.

A login that holds only the `VIEW DEFINITION` right sees the structure of a database. It sees the list
of tables and their columns. It reads the text of views, stored procedures and functions. It sees the
indexes and the foreign keys. But it cannot read a single row of any table. And it cannot change
anything.

This MCP server applies the same answer to an AI agent. The agent queries the database through the MCP
server. The MCP server connects to the SQL Server instance with a login that holds only that right. So
the agent knows the structure of the database, and nothing more.

For a developer, the benefit is immediate. There is no need to paste the result of a catalog query into
the conversation so that the agent knows how the database is built. The agent looks up the exact names
of tables and columns, their types, the procedures that use a table. The SQL it proposes is based on the
real database, not on what it assumes.

For a DBA or an IT manager, the question is a different one: what can this agent do on the database,
and how can that be checked? *What guarantees that the agent does not read the data*, below, answers it.

## Where it runs

The MCP server is a program that runs on your own computer, next to the AI application that uses it.
That application starts it when it needs it, and talks to it through the standard input and output
of the program. The MCP standard calls this a local server.

Three things follow.

The AI application has to be installed on the same computer, and it has to support local MCP
servers. Claude Desktop does, and it is the client this MCP server is tested with. A chat opened in a
web browser runs on the servers of its provider: it cannot start a program on your computer, so it
cannot use this MCP server, whoever the provider is.

The computer has to run Windows. The MCP server keeps your connections in a file encrypted with
DPAPI, an encryption mechanism of Windows (see *The connections*, below).

The computer has to reach the SQL Server instance over the network, as SSMS would. The MCP server
opens no network port: its only connection is the one it makes to the instance, with the login you
give it.

Whatever the MCP server returns becomes part of the conversation. It is therefore sent to the
provider of the AI model, like the rest of what you type. What it returns is the structure of the
database, and never its data: the next part explains what guarantees that.

## What guarantees that the agent does not read the data

### Two protections, and only one of them is the guarantee

The first protection is the code of the MCP server. It only runs queries written in advance, which read
the SQL Server catalog, that is, the system views that describe the objects of the database. Neither the
user nor the AI agent can change these queries. The tools of the MCP server take parameters, such as a
schema name or part of an object name. They never take SQL. This code is public: you can read it in this
repository.

The second protection is the one that makes the guarantee: the rights of the login. Even if the code of
the MCP server had a bug, or had been modified, it could not do more than its login allows. And those
rights are granted by you, with a script you read before running it. So you do not have to trust the
MCP server. You can check for yourself, on your own instance, what its login can and cannot do.

### The one figure about the data: the number of rows

The `list_objects` and `describe_object` tools give the approximate number of rows of each table. At
first sight, this looks as if the MCP server had read the tables. It has not.

SQL Server keeps that number in its catalog, in the system view `sys.partitions`, as part of what it
knows about the storage of a table. The `VIEW DEFINITION` right shows it, just as SSMS shows it in the
properties of a table. The MCP server reads that number, and nothing else: it never counts the rows,
and never reads them. The number is approximate because SQL Server maintains it for its own needs, not
as an exact count.

### The four steps

Setting it up takes four steps. The three scripts they use are in the `db/` folder of this repository,
and each one is commented in detail.

1. Check that the SQL Server instance accepts SQL Server authentication.
2. Create the login, with `Create-DdlReaderLogin.sql`.
3. Check what this login can do, with `Verify-DdlReaderRights.sql`.
4. Review what the database grants to all its users, with `Review-PublicPermissions.sql`.

#### Step 1 — Does the instance accept SQL Server authentication?

The login created in step 2 is a SQL Server login: a name and a password, managed by the instance
itself. But an instance can be configured to accept Windows authentication only. In that case, creating
the login succeeds, but every connection with it is then refused (error 18456).

To find out, run:

```sql
select serverproperty('IsIntegratedSecurityOnly') as windows_only;
```

A result of 1 means the instance accepts Windows authentication only. Switching to mixed mode is done in
the properties of the server, on the Security page, and requires a restart of the service. That decision
belongs to the DBA. If the instance has to stay in Windows authentication only, the MCP server can
connect with a Windows account instead, with a limit explained in *With a Windows account*, below.

#### Step 2 — Create the login

The script `db/Create-DdlReaderLogin.sql` runs under an account allowed to create logins and users. In
practice, an administrator of the instance.

It runs in **SQLCMD mode**. The script starts with four `:setvar` lines, which set the name of the
login, its password, the name of the role and the name of the database. These lines are not T-SQL: they
are `sqlcmd` commands, which SSMS only understands in SQLCMD mode (menu *Query > SQLCMD Mode*). Without
that mode, the script stops on its first line, and nothing is created.

Before running it, replace the four values. The names are examples. The password must be your own.

The script does five things, in this order:

1. it creates the login, at the level of the instance;
2. it creates the matching user, in the database;
3. it creates a role, and makes that user a member of it;
4. it grants the role the `VIEW DEFINITION` right, on the whole database or on one schema;
5. it denies the role the execution of four procedures that write, when they exist.

Here it is in full:

```sql
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

-- 3. A role holds the right, and the user becomes a member of it.
create role [$(Role)];
alter role [$(Role)] add member [$(Login)];
go

-- 4. The one right granted. By default it covers the whole database. To limit it to one schema, comment
--    out the first line below and uncomment the second, with the name of your schema.
grant view definition to [$(Role)];
-- grant view definition on schema::[sales] to [$(Role)];
go

-- 5. The only writes left open by the default rights of a database.
if object_id(N'dbo.sp_creatediagram') is not null deny execute on dbo.sp_creatediagram to [$(Role)];
if object_id(N'dbo.sp_alterdiagram') is not null deny execute on dbo.sp_alterdiagram to [$(Role)];
if object_id(N'dbo.sp_renamediagram') is not null deny execute on dbo.sp_renamediagram to [$(Role)];
if object_id(N'dbo.sp_dropdiagram') is not null deny execute on dbo.sp_dropdiagram to [$(Role)];
go
```

The file in the repository holds the same statements, with fuller comments, and how to undo everything.

#### Why these choices

**One right: `VIEW DEFINITION`.** This right lets a login read the definition of objects: the columns of
a table and their types, the text of a view or a procedure, the indexes, the constraints. It does not let
it read the content of a table. Nor does it let it run a procedure, or change anything at all. This is
the principle of least privilege: a login receives what its task needs, and nothing else. Every tool of
the MCP server works with this right alone.

**A role, rather than a right granted to the login directly.** The right is granted to a role, and the
login becomes a member of that role. If another account ever needs the same access — a second login, or
a Windows account — it only has to be added to the role. There is no new right to grant, and none to
review. The script file shows how to do it for a Windows account.

**The whole database, or one schema.** By default, the right covers the whole database. You can limit it
to one schema: the script holds the line to use instead, as a comment. The MCP server is not told about
that limit. It simply sees fewer objects. This is on purpose: the limit lives in the rights, where you
control it, and not in a setting of the MCP server.

**Four denies, on the diagram procedures.** When database diagrams are created in SSMS, four procedures
are installed in the `dbo` schema: `sp_creatediagram`, `sp_alterdiagram`, `sp_renamediagram` and
`sp_dropdiagram`. They write into the table `dbo.sysdiagrams`. And the role `public`, which every user of
the database belongs to, is allowed to run them. This is the only way to write that the default rights
of a database leave open. The script closes it, procedure by procedure, and only where the procedure
exists.

**No other deny.** It is tempting to refuse everything at once, for example with a `deny select` on the
whole database. That would be a mistake. Such a deny would also apply to the system views of the `sys`
schema, the ones that describe the objects of the database. And that is exactly what the MCP server
reads: the login would see nothing at all. So the script only denies what has been identified as a
possible write. The rights your database grants to `public` on top of the defaults remain granted, and
step 4 is there to show them.

#### Step 3 — Check what the login can do

The script of step 2 is run by an administrator. So it says nothing about what the login itself can do.
That is the job of the script `db/Verify-DdlReaderRights.sql`.

This script runs **connected as the login created in step 2**, in the database the MCP server will read.
This matters: the script checks the rights of the connection that runs it. Run from an administrator
account, it would answer for the administrator. In SSMS, open a new connection, choose *SQL Server
Authentication*, type the name of the login and its password, then select the database.

The script changes nothing. It makes four checks and gives each one a verdict, PASS or FAIL:

1. **The login sees the definitions.** The script counts the visible objects and the readable module
   texts.
2. **The login reads no data.** No table or view grants it the `SELECT` right. And an actual attempt to
   read the first table of the database is refused by SQL Server (error 229).
3. **The login writes nothing.** It holds no right to insert, update, delete or execute, and no right to
   create or alter an object of the database. And an actual attempt to create a table is refused
   (error 262). That attempt runs inside a transaction that is rolled back: even if it succeeded,
   nothing would remain.
4. **The dependency functions answer.** The `find_references` tool of the MCP server needs them.

Here is the result on the demo database of this repository. On your database, the numbers and the name
of the table tried will be your own:

```text
step  check_name              verdict  detail
1     sees the definitions    PASS     35 objects visible, 12 module texts readable
2     reads no data           PASS     0 tables or views readable; reading [legacy].[100%_done] refused (229)
3     writes nothing          PASS     0 rights to write held; creating a table refused (262)
4     reads the dependencies  PASS     0 objects reference [legacy].[100%_done]

conclusion
PASS: this login reads the definitions, and neither reads nor writes the data.
```

When a check returns FAIL, the `detail` column says what was found. A FAIL on reading or on writing means
the login holds more rights than intended: through another role, through a right granted to `public`, or
because it is an administrator. Run under a `sysadmin` account, the script does indeed return FAIL on
both of these checks, as it should.

Once this check passes, the login is ready: you can declare the connection to the MCP server (see *The
connections*, below).

#### Step 4 — What the database grants to all its users

Every user of a database is a member of the role `public`, and cannot be removed from it. A right granted
to `public` is therefore held by every user, the login of the MCP server included, on top of what it is
granted by name.

The script `db/Review-PublicPermissions.sql` lists these rights, outside the catalog views. It runs under
an administrator account of the database. The reason is that the system view listing the rights only
shows a login the rights it is allowed to see: run by the restricted login, the script would show fewer
rights than there are.

On a new database, it shows two rows, `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` and `VIEW ANY COLUMN
MASTER KEY DEFINITION`. They give access to the metadata of *Always Encrypted* keys, and to no data. If
you see other `SELECT`, `INSERT`, `UPDATE`, `DELETE` or `EXECUTE` rights granted to `public`, they apply
to every user of the database. They deserve a look, whatever you decide about the MCP server.

#### With a Windows account

The MCP server can also connect with Windows authentication instead of a SQL Server login. It then
connects as the Windows account that started the AI application: in practice, your own account, the
one you also use in SSMS.

This changes what the rights can guarantee. The working loop of this MCP server involves two
identities: the agent reads the structure with the rights of the MCP server, and you apply the scripts
it proposes with your own rights. With Windows authentication, these two identities are one and the
same.

- If your account keeps the rights you work with, the MCP server holds them too. It still runs only
  its own queries, which read the catalog, so this MCP server will not change your database. But the
  guarantee then rests on its code, and no longer on the rights.
- If your account is limited to `VIEW DEFINITION`, the guarantee holds again, but you can no longer
  apply anything with that account. The MCP server then serves to read and review code, not to change
  it.

A SQL Server login dedicated to the MCP server, as created in step 2, keeps the two identities apart.
Windows authentication suits an account that only reads, for a review or an audit.

The rights of the login bound this MCP server, and nothing else. Your AI application may give the
agent other tools: another MCP server that runs SQL, or a command line that can start `sqlcmd`. Those
tools run under your Windows account, whichever way this MCP server connects, and with the rights of
that account. Check what they can do too.

Whichever account you use, its rights apply, and checking them is up to you. The simplest way is to run
the script of step 3 while connected with that account. To give it exactly the rights of the login of
step 2, add it to the role the script created.

The MCP server cannot make that check for you. An account can be restricted on some tables and not on
others. No global check can state that it reads nothing.

The opposite, however, can be checked. A `sysadmin` account can read all the data. So can an account
that holds the `SELECT` right on the whole database, whether it comes from the role `db_datareader`, the
role `db_owner` or a `grant`. The MCP server detects these two cases and reports them. It does so first
in the console, when you test the connection with `configure`. It says it again once, in the first
result the agent receives. And the `server_info` tool repeats it every time it is asked. When the MCP
server detects nothing, it says nothing. That silence is not a guarantee.

### What the code of your database can reveal

The `VIEW DEFINITION` right gives access to the text of views, procedures, functions and triggers, as it
was written. If you have written passwords, API tokens or other sensitive information in plain text in
that code, the AI will read them, with the few rights it has. And what it reads goes to the provider of
the AI model, along with the rest of the structure.

Writing secrets into code is a bad practice. The MCP server does not try to make up for it. This risk is
not specific to AI, either: that information can already be read by anyone who has been granted
`VIEW DEFINITION`.

Before opening access, you can look for such information in the code of the database. For example:

```sql
select object_schema_name(object_id) as schema_name, object_name(object_id) as object_name
from sys.sql_modules
where definition like N'%password%' or definition like N'%token%';
```

## What the agent gets

The MCP server supports a simple working loop. The agent reads the structure of the database through
the MCP server. It proposes a script. You review that script, and you apply it yourself. The MCP server
never writes to the database.

The MCP server provides exact facts about the database. The AI model writes the SQL. It writes it with
the real names of tables and columns, the real types, the real nullability.

The MCP server offers eight tools, and a ninth when you provide a conventions file:

| Tool | What it returns |
| --- | --- |
| `list_objects` | The list of tables, views, procedures, functions, triggers and sequences. It can be filtered by schema, name, object type or modification date. For each table, it gives its approximate number of rows, which shows at once the main table among its satellites. For each view, procedure, function or trigger, it gives the length of its text: the agent knows what an object will cost to read before asking for it. |
| `describe_object` | A compact summary of the structure of an object. For a table: its approximate number of rows, its columns and their types, its keys, indexes, foreign keys, checks and triggers. For a view: its columns. For a procedure or a function: its parameters. For a trigger: the table it fires on, and when. On request, the descriptions (`MS_Description`) of the object and of its columns. |
| `script_object` | The complete `CREATE` script of an object, as SSMS produces it. The agent uses it when it has to change the object, and `describe_object` when it only needs to know its structure. |
| `find_references` | What uses an object: the views, procedures, functions and triggers that rely on it, and the tables whose foreign keys point to it. Or, the other way round, what that object uses. |
| `search_modules` | The lines of the text of views, procedures, functions and triggers that contain a fragment, with their line numbers and, on request, a few lines around them. |
| `find_columns` | The tables and views that have a column of a given name, with the type of that column. |
| `use_connection` | The choice of the database to work on, among the connections you have declared. |
| `server_info` | Where the executable is, its version, the file of connections, and the commands that manage them. The agent can then answer if you ask it how to remove a connection. |
| `read_conventions` | Only when you provide a conventions file: the SQL writing conventions of your team, as you wrote them. See *Your team's conventions*, below. |

### Measuring the impact of a change

Before changing a table, the question is what uses it. To answer it, `find_references` and
`search_modules` are read together.

`find_references` relies on the dependency graph that SQL Server maintains. But that graph does not see
dynamic SQL. When a procedure builds a query in a character string and then runs it, the name of the
table is inside the string, and SQL Server does not record it as a dependency. `search_modules` searches
the text, and finds that name. On a production database that was measured, 1.1 % of the procedures,
views and functions held dynamic SQL.

### Your team's conventions (optional)

The MCP server can also give the agent the SQL writing conventions of your team: how you name objects,
which types you use, how you write a script. It imposes no convention, and it ships none.

To use it, write your conventions in a Markdown file named `conventions.md`, and put it next to
`schema-mcp.exe`. The MCP server then offers one more tool, `read_conventions`, which returns the file
as it is. The description of that tool asks the agent to call it before writing SQL, and to follow the
conventions where the existing code does something else. If the file lives somewhere else, the
environment variable `DDL_CONVENTIONS_PATH` gives its location.

Without the file, the tool does not exist, and it costs nothing. To stop using it, remove or rename the
file. The file is read up to 50,000 characters; beyond that, the agent is told it was cut.

You can also give the same file to your agent through its own instructions — the instructions of a
project in Claude Desktop, a `CLAUDE.md` or `AGENTS.md` file, a skill — rather than through the MCP
server. Choose one of the two: a file loaded twice costs twice, and two copies drift apart as soon as
one of them is edited.

The folder `examples/` holds an example of such a file, with a note on how to use it. It is the
conventions of one team, published as an example, and nothing more.

### Known limits

- Dependencies are given at the level of the object, not of the column. SQL Server does not resolve
  column dependencies reliably, so the MCP server does not promise them.
- A module created with the `with encryption` option has no readable text, so `search_modules` does not
  find it.
- The modification date of a table also changes when one of its indexes changes. For views, procedures
  and functions, it does show the last change to the code.
- Database triggers, the ones that fire on DDL statements, are not covered: they belong to no schema.
  Triggers on tables and views are.

## What it costs

### To the AI model

Every answer of a tool takes room in the conversation, and that room is counted in tokens. So the MCP
server limits all its answers. A list never goes beyond 200 rows by default. This ceiling is set by the
environment variable `DDL_MAX_RESULTS`, which the agent cannot change.

When a request is too broad, the tool does not return a truncated list. It returns how the results are
spread: how many objects per schema, per type, or per name prefix. The agent then knows how to narrow its
request, or what question to ask you.

Two tools help load only what is useful. `list_objects` gives the length of the text of a module before
loading it: some procedures exceed 200,000 characters. And `search_modules` lets the agent read a
passage of a large module without loading it whole.

For the same reason, `describe_object` is the tool to know the structure of a table: on a large table
of a production database, it returned about a sixth of what `script_object` returned, in one second
rather than forty.

Finally, the definitions of the nine tools take about 3,000 tokens. A client that loads its tools in
advance pays them in every conversation, even one that has nothing to do with a database. A client
that loads its tools on demand pays them only when a conversation looks for them.

### To the SQL Server instance

The MCP server only reads the catalog, never a table of data. Queries on the catalog are usually fast.
The most expensive one is a search through the text of every module, without a filter. On a production
database of 36,000 objects and 32,000 modules, it took 11 seconds. And the search stops as soon as it has
found more results than it can return.

That is the cost of using SSMS without care: expanding, without a filter, the node of stored procedures
of such a database also loads thousands of objects. The filters of the tools are there to avoid that
kind of query.

## Installation

The MCP server runs on Windows x64, with .NET 10.

<!-- Distribution to be decided: a self-contained executable in the releases, or a NuGet package run by dnx. -->

It is built with the following command:

```powershell
dotnet publish src/SharpPyxis.SqlServer.SchemaMcp -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\SchemaMcp
```

The result is a single executable, `schema-mcp.exe`, of about 93 MB. It contains the .NET runtime:
nothing else needs to be installed on the machine.

### The connections

Connections are declared in a console, with the executable itself. You type the name of the instance,
the database, the login and the password. The MCP server never asks for them during a conversation, and
the agent never sees them.

```text
schema-mcp configure add                 record a new connection, testing it first
schema-mcp configure list                show what is recorded
schema-mcp configure test [id]           test one recorded connection, or all of them
schema-mcp configure set-password <id>   replace the password of one connection
schema-mcp configure remove <id>         delete one connection
```

Connections are stored in a file encrypted with DPAPI, the encryption mechanism of Windows, for the
current Windows account. This encryption protects the file if it is stolen, or if it is exposed by
accident: copied elsewhere, included in a backup, shown during a screen share. A program running under
the same Windows account, however, can decrypt it, exactly as the MCP server does. This level of
protection matches what the password allows: reading object definitions, and nothing else.

The file cannot be decrypted from another Windows account, nor on another machine. If you move to a new
machine, the connections have to be declared again.

### The client

For Claude Desktop, the declaration goes in the file `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "sqlserver-schema": {
      "command": "C:\\Tools\\SchemaMcp\\schema-mcp.exe",
      "env": { "DDL_CONFIG_PATH": "C:\\Tools\\SchemaMcp\\connections.dat" }
    }
  }
}
```

`DDL_CONFIG_PATH` tells where the file of connections is. This line is optional: without it, the file
is in the `%APPDATA%` folder of your account.

Any MCP client that can start a server over stdio will do, not only Claude Desktop. The MCP server opens
no network port. Its only connection is the one it makes to the SQL Server instance.

If you have declared several connections, none is chosen at startup. The agent then asks you which
database to work on, and it only switches to another one when you ask it to. Every result names the
database it comes from: a wrong target shows at once.

Three environment variables set up the MCP server:

| Variable | Role |
| --- | --- |
| `DDL_CONFIG_PATH` | Where the file of connections is. |
| `DDL_MAX_RESULTS` | The maximum number of rows in an answer, 200 by default. The agent cannot change it. |
| `DDL_CONVENTIONS_PATH` | Where the conventions file of your team is, when it is not next to the executable. Optional. |
| `CONNECTION_STRING` | A single connection, declared without the file. It serves where DPAPI does not exist, outside Windows. The password is then in plain text in the configuration of the client. |

## Trying it on a demo database

You can try the MCP server without going near a database that matters. The script
`db/Create-DemoDatabase.sql` creates about twenty objects, spread over four schemas, with no data at
all. It runs in an empty database, created for the purpose.

Several of these objects are traps, and a comment announces each one: a name holding the special
characters of `like`, dynamic SQL, a reference to a table that does not exist, a view broken by the
removal of a column.

Run it in SSMS, or with `sqlcmd -I`. Without the `-I` option, `sqlcmd` creates the procedures with
`QUOTED_IDENTIFIER OFF`, and `script_object` will show it.

## Tests

```powershell
dotnet test ./SharpPyxis.SqlServer.SchemaMcp.slnx -c Release
```

The integration tests need LocalDB. They create the demo database, run the tools of the MCP server
against it, and then drop it.

## License

The code of this repository is under the MIT license, in `LICENSE`.

The executable published in the releases also contains the .NET runtime and third-party libraries,
each under its own license: `THIRD-PARTY-NOTICES.md` lists them. One of them,
`Microsoft.Data.SqlClient.SNI`, is not open source. It is distributed under the Microsoft Software
License Terms, which apply to your use of it.
