# SchemaFlex

SchemaFlex is a **command-line (CLI) tool** — that connects to a relational database, 
reads its schema, and generates a single self-contained HTML file containing an interactive
Entity-Relationship Diagram (ERD) viewer for that database.

Run it from a terminal, point it at a database connection string, and it produces an
`.html` file you can open directly in a browser (no server required) to explore your
tables, columns, keys, and relationships.

## Features

- **Interactive ERD viewer** — pan, zoom, search, drag tables, and click through to full
  column/index/constraint/trigger detail, all in one offline HTML file.
- **Export PNG** — render the whole diagram to a PNG image straight from the viewer.
- **View DDL** — a `CREATE TABLE`/`ALTER TABLE` script reconstructed from the schema,
  in the source database's own dialect, with copy-to-clipboard and download.
- **SSH tunneling** — connect to a database that's only reachable through a jump host,
  via `--ssh-host`/`--ssh-key`/`--ssh-password`.

## Supported databases

The database provider is auto-detected from the connection string format — no need to
specify it explicitly:

- PostgreSQL (via Npgsql)
- SQL Server (via Microsoft.Data.SqlClient)
- MySQL / MariaDB (via MySqlConnector)
- SQLite (via Microsoft.Data.Sqlite)

## Requirements

- .NET 10 SDK (see `global.json`)

## Build

```bash
git clone <repo-url>
cd SchemaFlex
dotnet build
```

## Usage

Run via `dotnet run` from the `SchemaFlex` project directory, or build and invoke the
published executable directly.

```
schemaflex --connection <connection-string> [--output <path>] [--schema <schema-name>]
```

### Options

| Option         | Required | Default              | Description                                                                                                          |
|----------------|----------|-----------------------|------------------------------------------------------------------------------------------------------------------------|
| `--connection` | Yes      | —                     | The database connection string (PostgreSQL, SQL Server, MySQL/MariaDB, or SQLite — the provider is auto-detected).    |
| `--output`     | No       | `erd.html`            | Path to write the generated HTML file to.                                                                              |
| `--schema`     | No       | provider default      | The database schema to inspect. Defaults to `public` (Postgres), `dbo` (SQL Server), the database name (MySQL/MariaDB), or `main` (SQLite). |
| `--include-tables` | No   | (all tables)          | Only include tables matching these comma-separated patterns. `*` and `%` both match any run of characters. |
| `--exclude-tables` | No   | (none)                | Exclude tables matching these comma-separated patterns (same wildcard syntax). Applied after `--include-tables`. |

### SSH tunnel options

For a database that's only reachable through a jump host (not applicable to SQLite, and
the connection string's host/port must be reachable from the SSH host itself):

| Option                  | Default | Description                                                |
|--------------------------|---------|--------------------------------------------------------------|
| `--ssh-host`             | —       | SSH jump host.                                                |
| `--ssh-port`             | `22`    | SSH port.                                                     |
| `--ssh-user`             | —       | SSH username. Required when `--ssh-host` is set.              |
| `--ssh-key`              | —       | Path to a private key file. Use this or `--ssh-password`.     |
| `--ssh-key-passphrase`   | —       | Passphrase for `--ssh-key`, if it has one.                     |
| `--ssh-password`         | —       | SSH password. Use this or `--ssh-key`.                         |

```bash
schemaflex --connection "Host=10.0.4.12;Username=postgres;Password=secret;Database=mydb" \
  --ssh-host bastion.example.com --ssh-user ec2-user --ssh-key ~/.ssh/id_ed25519
```

### Examples

Generate an ERD for a PostgreSQL database, writing to the default `erd.html`:

```bash
dotnet run --project SchemaFlex -- --connection "Host=localhost;Username=postgres;Password=secret;Database=mydb"
```

Generate an ERD for a SQL Server database, writing to a custom path:

```bash
dotnet run --project SchemaFlex -- --connection "Data Source=localhost;Initial Catalog=MyDb;Integrated Security=true;TrustServerCertificate=true" --output diagrams/mydb-erd.html
```

Generate an ERD for a local SQLite file:

```bash
dotnet run --project SchemaFlex -- --connection "Data Source=app.db" --output app-schema.html
```

Generate an ERD for a MySQL database with an explicit schema:

```bash
dotnet run --project SchemaFlex -- --connection "Server=localhost;Uid=root;Pwd=secret" --schema mydb --output mydb.html
```

Generate an ERD limited to a set of tables, excluding audit/temp tables:

```bash
dotnet run --project SchemaFlex -- --connection "Data Source=app.db" --include-tables "user*,order*" --exclude-tables "%_audit"
```

Example console output:

```
Connecting to PostgreSQL database...
Found 12 table(s). Writing ERD viewer...
Successfully generated ERD viewer at: C:\path\to\erd.html
```

Open the resulting HTML file in any browser to view the interactive diagram.

## Viewer features

The generated `erd.html` is a full interactive viewer, not a static image:

- **Pan, zoom, search** — click a table to see its full column/index/constraint/trigger
  detail; drag tables to rearrange, or click Auto-arrange to lay them out again.
- **Export PNG** — the "Export PNG" toolbar button rasterizes the whole diagram (not just
  the visible viewport) to a downloadable image.
- **View DDL** — the "View DDL" toolbar button reconstructs `CREATE TABLE`/`ALTER TABLE`
  statements from the captured schema, in the source database's own dialect (identifier
  quoting, identity/auto-increment syntax, etc. all follow the provider), with a Copy
  button and a "Download .sql" link.

## What it reads

For each table in the target schema, SchemaFlex reads:

- Columns (name, type, nullability, defaults)
- Primary keys
- Foreign keys / relationships
- Indexes
- Check constraints
- Triggers
- Enum types (where supported by the provider)

This data is serialized to JSON and embedded directly into the generated HTML file
alongside a bundled viewer template, so the output is fully self-contained and portable —
no external files or network access needed to view it.

## Architecture

- **SchemaFlex** — the CLI entry point (`Program.cs`), built with `System.CommandLine`.
  Parses arguments, resolves the provider, and orchestrates schema reading and HTML
  generation.
- **SchemaFlex.Core** — the core library:
  - Database provider detection (`DatabaseProviderDetector`) and one `ISchemaReader`
    implementation per provider (`Database/Postgres`, `Database/SqlServer`,
    `Database/MySql`, `Database/Sqlite`).
  - `Ssh/` — `SshTunnel` (wraps SSH.NET to forward a local port to the real database
    endpoint) and `ConnectionEndpoint` (reads/rewrites a connection string's host and
    port per provider).
  - Schema data models (`Models/`).
  - `ErdHtmlGenerator` injects the schema JSON into the viewer template
    (`templates/erd.html`).
- **SchemaFlex.Tests** — unit tests (provider detection, defaults, HTML generation) and
  integration tests against each supported database.

## Exit codes

- `0` — success
- `1` — database error, I/O error, or other failure
- `130` — cancelled (Ctrl+C)



## 🚀 Release Pipeline

SchemaFlex uses a two‑stage GitHub Actions pipeline (`.github/workflows/main.yml`)
designed to keep `main` stable and ensure releases are intentional, versioned, and
fully validated.

### 1. Continuous Integration (CI) on `main`

Whenever code is pushed to the `main` branch, the pipeline automatically runs:

- Tests (Linux runner)
- A sanity build of the win-x64 EXE (ensures it still compiles)

This guarantees that `main` always contains valid, working code. No release is created
during CI runs.

### 2. Release Pipeline (CD) triggered by version tags

A release is only created when a version tag is pushed.

Example:

```bash
git tag v1.2.3
git push origin v1.2.3
```

Tagging triggers the full release workflow:

- Run tests
- Build self‑contained, single-file EXEs for `win-x64`, `linux-x64` and `osx-arm64`,
  each smoke-tested on its native OS (`osx-x64`/Intel is excluded for now — GitHub's
  Intel Mac runner pool was taking 30+ minutes to queue a job)
- Create a [GitHub release](https://github.com/Viewsource-Ltd/SchemaFlex/releases) for
  the tag, with each platform build attached as a zip and release notes generated
  automatically from the merged PRs/commits since the last tag
- Redeploy [the website](https://github.com/Viewsource-Ltd/SchemaFlex) so its
  "last updated" stamp reflects the new release

GitHub Releases is the only distribution channel — the website's download button and
any future WinGet/Scoop/Chocolatey manifest can link directly to the stable
`releases/latest/download/schemaflex-<rid>.zip` or `releases/download/vX.Y.Z/...` URLs,
so nothing needs updating by hand when a new version ships.

### 3. Versioning

Version numbers are not edited manually. They come directly from the Git tag:
pushing `vX.Y.Z` produces a release named `vX.Y.Z` with assets
`schemaflex-win-x64.zip`, `schemaflex-linux-x64.zip` and `schemaflex-osx-arm64.zip`.

### 4. How to create a release

To publish a new version:

1. Ensure your changes are merged into `main`
2. Create a version tag:
   ```bash
   git tag vX.Y.Z
   ```
3. Push the tag:
   ```bash
   git push origin vX.Y.Z
   ```

The pipeline will:

- Validate the code
- Build and smoke-test the EXEs for every supported platform
- Create the GitHub release with those EXEs attached and auto‑generated release notes
- Redeploy the website

No other steps are required.

### 5. Manual runs

You can manually trigger the workflow from GitHub Actions for testing, but publishing only
happens when the ref is a version tag. Manual runs are safe and will not create releases
unless you explicitly tag.

## 🌐 Website

`SchemaFlex.Web/` is a plain static site (no build step) deployed to GitHub Pages by
`.github/workflows/deploy-pages.yml`. It redeploys automatically whenever:

- Anything under `SchemaFlex.Web/` is pushed to `main`, or
- A version tag is released (see above)

Both `index.html` and `privacy.html` contain `{{LAST_UPDATED}}` / `{{YEAR}}`
placeholders that the workflow stamps with the current UTC date at deploy time — they
are never committed with real values.

The source files stay split up (`css/site.css`, `js/nav.js`, `js/download.js`) for
easier editing, but what actually gets published is optimized by
`SchemaFlex.Web/build/build-site.mjs` (`npm run build` from `SchemaFlex.Web/`): the CSS
and JS are inlined into each HTML page and minified along with the HTML itself, and the
favicon is run through SVGO — so each page ships as a single, minified request instead
of four.

## Privacy

SchemaFlex is a local CLI tool. It makes no network requests other than the database
connection you give it (and, if you use `--ssh-host`, the SSH tunnel you configured to
reach it), sends no telemetry or analytics, and has no server component. The connection
string, schema and everything read from your database stay on your machine — the only
thing SchemaFlex writes anywhere is the HTML file you asked for.
