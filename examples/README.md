# Examples

## conventions.md

`conventions.md` is an **example** of a conventions file: the SQL writing conventions of one team,
published to show what such a file can look like. It was written on 2026-09-12 and is not kept up to
date.

The MCP server imposes no convention, and it ships none. Without a conventions file, its
`read_conventions` tool does not exist. Copy this file, change it, or write your own from scratch.

A conventions file tells the AI agent how your team writes SQL. The code the agent reads in the
database shows one style, sometimes several; the conventions file says which rules to follow when it
writes something new.

There are two ways to give it to the agent. Choose one of them: a file loaded twice costs twice, and
two copies drift apart as soon as one of them is edited.

- **Through the MCP server.** Name the file `conventions.md` and put it next to `schema-mcp.exe`, or
  point the environment variable `DDL_CONVENTIONS_PATH` to it. The MCP server then offers the tool
  `read_conventions`, which the agent calls before writing SQL. To stop using it, remove or rename the
  file.
- **Through your agent.** The file is plain Markdown. It can go wherever your agent reads its
  instructions: the instructions of a project in Claude Desktop, a `CLAUDE.md` or `AGENTS.md` file in
  a repository, a skill.

The file itself holds nothing but the conventions: whatever it contains, the agent pays for in tokens
each time it reads it.
