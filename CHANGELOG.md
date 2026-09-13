# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-09-13

The first release: a read-only MCP server that gives an AI agent the structure of a SQL Server
database, and never its data.

### Added

- Nine tools: `list_objects`, `describe_object`, `script_object`, `find_references`,
  `search_modules`, `find_columns`, `use_connection`, `server_info`, and `read_conventions` when a
  conventions file is provided.
- Bounded answers: at most 200 rows by default, set by `DDL_MAX_RESULTS`. A request that matches
  more returns how the results are spread instead.
- Connections declared with `schema-mcp configure`, and stored in a file encrypted with DPAPI for
  the current Windows account.
- Three scripts in `db/`: create the restricted login, check what it can and cannot do, and review
  what the role `public` grants.
- A demo database, `db/Create-DemoDatabase.sql`, to try the server away from any real database.
- An example of a conventions file, in `examples/`.

[0.1.0]: https://github.com/SharpPyxis/SharpPyxis.SqlServer.SchemaMcp/releases/tag/v0.1.0
