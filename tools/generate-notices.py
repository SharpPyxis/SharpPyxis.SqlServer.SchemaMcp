"""Writes THIRD-PARTY-NOTICES.md from what the published executable actually contains.

Run it after publishing the self-contained executable, so that the files it reads exist:

    dotnet publish src/SharpPyxis.SqlServer.SchemaMcp -c Release -r win-x64 --self-contained ...
    py -3 -B tools/generate-notices.py

The packages come from the restore of that publish (obj/project.assets.json), their licenses and
copyrights from their nuspecs in the NuGet cache, and the runtime version from the published deps.json.
License texts are copied from files, never retyped: the MIT text from the license of the .NET runtime,
the Apache-2.0 text from tools/licenses, the terms of Microsoft.Data.SqlClient.SNI from its package.

The notices describe one published executable: rerun this in the commit that changes a dependency.
Requires Python 3.9 or later.
"""
import glob
import json
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "src", "SharpPyxis.SqlServer.SchemaMcp")
PUBLISHED = os.path.join(PROJECT, "bin", "Release", "net10.0", "win-x64")
SNI = "Microsoft.Data.SqlClient.SNI.runtime"


def read(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read().replace("\r\n", "\n")


def find(folders, relative):
    """The first package folder of the restore holding that path."""
    for folder in folders:
        matches = glob.glob(os.path.join(folder, relative))
        if matches:
            return matches[0]
    raise FileNotFoundError(f"{relative} is in no package folder of the restore: publish first.")


assets = json.load(open(os.path.join(PROJECT, "obj", "project.assets.json"), encoding="utf-8"))
folders = list(assets["packageFolders"])
target = next(t for t in assets["targets"] if t.endswith("/win-x64"))

packages = []
for key, library in assets["targets"][target].items():
    # ILLink is a build tool: it is restored, never shipped.
    if library.get("type") != "package" or key.startswith("Microsoft.NET.ILLink"):
        continue
    name, version = key.split("/")
    nuspec = read(find(folders, os.path.join(assets["libraries"][key]["path"], "*.nuspec")))
    expression = re.search(r'<license type="expression">([^<]+)</license>', nuspec)
    copyright_line = re.search(r"<copyright>([^<]*)</copyright>", nuspec)
    license_name = expression.group(1) if expression else "Microsoft Software License Terms"
    packages.append((name, version, license_name, copyright_line.group(1).strip() if copyright_line else ""))
packages.sort(key=lambda row: row[0].lower())

deps = read(os.path.join(PUBLISHED, "schema-mcp.deps.json"))
runtime = re.search(r'"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.win-x64/([^"]+)"', deps).group(1)
sni_version = next(version for name, version, _, _ in packages if name == SNI)

runtime_license = read(find(folders, os.path.join("microsoft.netcore.app.runtime.win-x64", runtime, "LICENSE.TXT"))).strip()
mit_body = runtime_license[runtime_license.index("Permission is hereby granted"):]
apache = read(os.path.join(ROOT, "tools", "licenses", "Apache-2.0.txt")).rstrip()
sni_terms = read(find(folders, os.path.join(SNI.lower(), sni_version, "LICENSE.txt"))).strip()

rows = "\n".join(f"| `{name}` | {version} | {license_name} | {holder} |" for name, version, license_name, holder in packages)

text = f"""# Third-party notices

`schema-mcp.exe`, as published in the releases of this repository, is a self-contained executable: it
contains the .NET runtime and every library the MCP server depends on. The code of this repository is
under the MIT license, in `LICENSE`. Each component listed here remains under its own license.

## Microsoft.Data.SqlClient.SNI

The executable contains `{SNI}` {sni_version}, the native library
that `Microsoft.Data.SqlClient` uses to connect to SQL Server on Windows. This component is not open
source. It is distributed under the Microsoft Software License Terms reproduced at the end of this
file, and your use of it is subject to those terms.

## The .NET runtime

The executable contains the .NET runtime {runtime}, under the MIT license reproduced below. The runtime
carries its own third-party notices, in the file `THIRD-PARTY-NOTICES.TXT` attached to each release
next to the executable.

## Libraries

{len(packages)} packages, with the version the executable contains.

| Package | Version | License | Copyright |
| --- | --- | --- | --- |
{rows}

## License texts

### MIT License — the .NET runtime

```text
{runtime_license}
```

### MIT License — the packages above under MIT

```text
Copyright (c) Microsoft Corporation.

{mit_body}
```

### Apache License 2.0 — ModelContextProtocol and ModelContextProtocol.Core

```text
{apache}
```

### Microsoft Software License Terms — Microsoft.Data.SqlClient.SNI

```text
{sni_terms}
```
"""

with open(os.path.join(ROOT, "THIRD-PARTY-NOTICES.md"), "wb") as handle:
    handle.write(text.encode("utf-8"))

print(f"{len(packages)} packages, SNI {sni_version}, runtime {runtime}, {len(text)} characters")
