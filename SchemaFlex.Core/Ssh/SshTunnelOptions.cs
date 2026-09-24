namespace SchemaFlex.Core.Ssh;

/// <summary>
/// How to reach the SSH jump host that forwards a connection through to the database.
/// Exactly one of <see cref="PrivateKeyPath"/> or <see cref="Password"/> is expected to be set.
/// </summary>
public record SshTunnelOptions(
    string Host,
    int Port,
    string Username,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    string? Password);
