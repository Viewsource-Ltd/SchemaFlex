using SchemaFlex.Core;
using SchemaFlex.Core.Database;
using SchemaFlex.Core.Models;
using SchemaFlex.Core.Ssh;
using System.CommandLine;
using System.Data.Common;
using System.Reflection;

const string title = "SchemaFlex — ERD Generator";

const string usage = """
Usage:
  SchemaFlex --connection <connection> [options]
""";

const string optionsHelp = """
Options:
  --connection <connection> (REQUIRED)  Database connection string.
  --output <output>                     Output HTML file. Default: erd.html
  --schema <schema>                     Schema to inspect. Default varies by provider.
  --include-tables <patterns>           Only include tables matching these comma-separated
                                         patterns (* and % both match any run of characters).
  --exclude-tables <patterns>           Exclude tables matching these comma-separated patterns
                                         (* and % both match any run of characters). Applied
                                         after --include-tables.
  --version                             Show version information.
  -?, -h, --help                        Show help.

SSH tunnel options (for a database only reachable through a jump host; not applicable to
SQLite, and the connection string's host/port must be reachable from the SSH host itself):
  --ssh-host <host>                     SSH jump host.
  --ssh-port <port>                     SSH port. Default: 22.
  --ssh-user <user>                     SSH username.
  --ssh-key <path>                      Path to a private key file. Use this or --ssh-password.
  --ssh-key-passphrase <passphrase>     Passphrase for --ssh-key, if it has one.
  --ssh-password <password>             SSH password. Use this or --ssh-key.
""";

const string examples = """
Examples:
  SchemaFlex --connection "Data Source=localhost;Initial Catalog=MyDb;Integrated Security=true;TrustServerCertificate=true"
  SchemaFlex --connection "Host=localhost;Username=postgres;Password=secret;Database=mydb"
  SchemaFlex --connection "Host=10.0.4.12;Username=postgres;Password=secret;Database=mydb" --ssh-host bastion.example.com --ssh-user ec2-user --ssh-key ~/.ssh/id_ed25519
""";

var connectionOption = new Option<string>(
                name: "--connection",
                description: "The database connection string (PostgreSQL, SQL Server, MySQL/MariaDB, or SQLite - the provider is auto-detected).")
{
    IsRequired = true
};

var outputOption = new Option<string>(
    name: "--output",
    description: "Path to output the generated HTML file.",
    getDefaultValue: () => "erd.html");

var schemaOption = new Option<string?>(
    name: "--schema",
    description: "The database schema to inspect. Defaults to 'public' (Postgres), 'dbo' (SQL Server), the database name (MySQL/MariaDB), or 'main' (SQLite).");

var includeTablesOption = new Option<string?>(
    name: "--include-tables",
    description: "Only include tables matching these comma-separated patterns (* and % both match any run of characters).");

var excludeTablesOption = new Option<string?>(
    name: "--exclude-tables",
    description: "Exclude tables matching these comma-separated patterns (* and % both match any run of characters). Applied after --include-tables.");

var sshHostOption = new Option<string?>(name: "--ssh-host", description: "SSH jump host to tunnel the database connection through.");
var sshPortOption = new Option<int>(name: "--ssh-port", description: "SSH port.", getDefaultValue: () => 22);
var sshUserOption = new Option<string?>(name: "--ssh-user", description: "SSH username.");
var sshKeyOption = new Option<string?>(name: "--ssh-key", description: "Path to a private key file for SSH authentication.");
var sshKeyPassphraseOption = new Option<string?>(name: "--ssh-key-passphrase", description: "Passphrase for --ssh-key, if it has one.");
var sshPasswordOption = new Option<string?>(name: "--ssh-password", description: "SSH password (alternative to --ssh-key).");

var rootCommand = new RootCommand("A lightweight standalone CLI tool to generate a database ERD visualizer HTML file.");
rootCommand.AddOption(connectionOption);
rootCommand.AddOption(outputOption);
rootCommand.AddOption(schemaOption);
rootCommand.AddOption(includeTablesOption);
rootCommand.AddOption(excludeTablesOption);
rootCommand.AddOption(sshHostOption);
rootCommand.AddOption(sshPortOption);
rootCommand.AddOption(sshUserOption);
rootCommand.AddOption(sshKeyOption);
rootCommand.AddOption(sshKeyPassphraseOption);
rootCommand.AddOption(sshPasswordOption);

if (args.Any(a => a is "-h" or "--help" or "-?"))
{
    PrintHelp();
    return 0;
}

if (args.Any(a => a == "--version"))
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
    return 0;
}

var parseResult = rootCommand.Parse(args);

// Checked ahead of "Errors" below: when an unknown option is combined with a
// missing --connection, System.CommandLine reports the missing-option error
// first, which buries what's actually most likely a typo the user should see.
if (parseResult.UnmatchedTokens.Count > 0)
{
    PrintHelp($"Unrecognized argument: '{parseResult.UnmatchedTokens[0]}'");
    return 1;
}

if (parseResult.Errors.Count > 0)
{
    PrintHelp(parseResult.Errors[0].Message);
    return 1;
}

if (string.IsNullOrWhiteSpace(parseResult.GetValueForOption(connectionOption)))
{
    PrintHelp("Missing required option: --connection");
    return 1;
}

var connectionString = parseResult.GetValueForOption(connectionOption)!;
var outputPath = parseResult.GetValueForOption(outputOption)!;
var schemaValue = parseResult.GetValueForOption(schemaOption);
var includeTables = SplitPatterns(parseResult.GetValueForOption(includeTablesOption));
var excludeTables = SplitPatterns(parseResult.GetValueForOption(excludeTablesOption));

var sshHost = parseResult.GetValueForOption(sshHostOption);
var sshPort = parseResult.GetValueForOption(sshPortOption);
var sshUser = parseResult.GetValueForOption(sshUserOption);
var sshKey = parseResult.GetValueForOption(sshKeyOption);
var sshKeyPassphrase = parseResult.GetValueForOption(sshKeyPassphraseOption);
var sshPassword = parseResult.GetValueForOption(sshPasswordOption);

SshTunnelOptions? sshOptions = null;
if (!string.IsNullOrWhiteSpace(sshHost))
{
    if (string.IsNullOrWhiteSpace(sshUser))
    {
        PrintHelp("--ssh-user is required when --ssh-host is set.");
        return 1;
    }

    var hasKey = !string.IsNullOrWhiteSpace(sshKey);
    var hasPassword = !string.IsNullOrWhiteSpace(sshPassword);
    if (hasKey == hasPassword)
    {
        PrintHelp("Specify exactly one of --ssh-key or --ssh-password when --ssh-host is set.");
        return 1;
    }

    sshOptions = new SshTunnelOptions(sshHost, sshPort, sshUser, sshKey, sshKeyPassphrase, sshPassword);
}
else if (!string.IsNullOrWhiteSpace(sshUser) || !string.IsNullOrWhiteSpace(sshKey) ||
         !string.IsNullOrWhiteSpace(sshKeyPassphrase) || !string.IsNullOrWhiteSpace(sshPassword) ||
         parseResult.FindResultFor(sshPortOption)?.IsImplicit == false)
{
    PrintHelp("--ssh-* options require --ssh-host to be set.");
    return 1;
}

var cts = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.CancelKeyPress += cancelHandler;

try
{
    await GenerateErdAsync(connectionString, outputPath, schemaValue, includeTables, excludeTables, sshOptions, cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (DbException ex)
{
    WriteError($"Database error: {ex.Message}");
    return 1;
}
catch (IOException ex)
{
    WriteError($"Could not write output file: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    WriteError($"Unexpected error: {ex.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

void WriteError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Error: {message}");
    Console.ResetColor();
}

void PrintHelp(string? errorMessage = null)
{
    var writer = errorMessage is null ? Console.Out : Console.Error;

    writer.WriteLine(title);
    writer.WriteLine();

    if (errorMessage is not null)
    {
        writer.WriteLine(errorMessage);
        writer.WriteLine();
    }

    writer.WriteLine(usage);
    writer.WriteLine();
    writer.WriteLine(optionsHelp);
    writer.WriteLine();
    writer.WriteLine(examples);
}

List<string>? SplitPatterns(string? raw) =>
    string.IsNullOrWhiteSpace(raw)
        ? null
        : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

async Task GenerateErdAsync(string connString, string outputFilePath, string? schema, List<string>? includeTables, List<string>? excludeTables, SshTunnelOptions? sshOptions, CancellationToken token)
{
    if (string.IsNullOrWhiteSpace(connString))
    {
        throw new ArgumentException("A connection string must be provided.", nameof(connString));
    }

    if (string.IsNullOrWhiteSpace(outputFilePath))
    {
        throw new ArgumentException("An output path must be provided.", nameof(outputFilePath));
    }

    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputFilePath));
    if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
    {
        throw new DirectoryNotFoundException($"Output directory does not exist: {outputDirectory}");
    }

    var provider = DatabaseProviderDetector.Detect(connString);

    SshTunnel? tunnel = null;
    try
    {
        if (sshOptions is not null)
        {
            if (provider == DatabaseProvider.Sqlite)
            {
                throw new ArgumentException("SSH tunneling is not applicable to SQLite connections (file-based).", nameof(sshOptions));
            }

            var (remoteHost, remotePort) = ConnectionEndpoint.Resolve(provider, connString);
            Console.WriteLine($"Opening SSH tunnel via {sshOptions.Host} to {remoteHost}:{remotePort}...");
            tunnel = SshTunnel.Open(sshOptions, remoteHost, remotePort);
            connString = ConnectionEndpoint.WithHostAndPort(provider, connString, "127.0.0.1", tunnel.LocalPort);
        }

        var resolvedSchema = string.IsNullOrWhiteSpace(schema) ? SchemaDefaults.Resolve(provider, connString) : schema;

        Console.WriteLine($"Connecting to {provider} database...");

        var reader = SchemaReaderFactory.Create(provider);
        var schemaData = await reader.ReadSchemaAsync(connString, resolvedSchema, token);

        if (includeTables is not null || excludeTables is not null)
        {
            var filteredTables = TableFilter.Apply(schemaData.Tables, includeTables, excludeTables);
            Console.WriteLine($"Filtered {schemaData.Tables.Count} table(s) down to {filteredTables.Count}.");
            schemaData = schemaData with { Tables = filteredTables };
        }

        Console.WriteLine(schemaData.Tables.Count == 0
            ? $"No base tables found in schema '{resolvedSchema}'. Generating an empty viewer."
            : $"Found {schemaData.Tables.Count} table(s). Writing ERD viewer...");

        var finalHtml = ErdHtmlGenerator.Generate(schemaData);

        await File.WriteAllTextAsync(outputFilePath, finalHtml, token);
        Console.WriteLine($"Successfully generated ERD viewer at: {Path.GetFullPath(outputFilePath)}");
    }
    finally
    {
        tunnel?.Dispose();
    }
}

