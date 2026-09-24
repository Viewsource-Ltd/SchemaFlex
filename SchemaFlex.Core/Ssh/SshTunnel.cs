using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace SchemaFlex.Core.Ssh;

/// <summary>
/// Opens a local TCP port that forwards to a remote host/port through an SSH jump host, for
/// reaching a database that isn't directly reachable from where SchemaFlex is running.
/// Disposing the tunnel stops the forward and closes the SSH connection.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forwardedPort;

    public int LocalPort { get; }

    private SshTunnel(SshClient client, ForwardedPortLocal forwardedPort, int localPort)
    {
        _client = client;
        _forwardedPort = forwardedPort;
        LocalPort = localPort;
    }

    public static SshTunnel Open(SshTunnelOptions options, string remoteHost, int remotePort)
    {
        var connectionInfo = new ConnectionInfo(
            options.Host,
            options.Port,
            options.Username,
            BuildAuthenticationMethods(options));

        var client = new SshClient(connectionInfo);
        try
        {
            client.Connect();

            var localPort = GetFreeLocalPort();
            var forwardedPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
            client.AddForwardedPort(forwardedPort);
            forwardedPort.Exception += (_, e) => throw new IOException($"SSH tunnel error: {e.Exception.Message}", e.Exception);
            forwardedPort.Start();

            return new SshTunnel(client, forwardedPort, localPort);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static AuthenticationMethod[] BuildAuthenticationMethods(SshTunnelOptions options)
    {
        if (!string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            return [new PrivateKeyAuthenticationMethod(options.Username, keyFile)];
        }

        return [new PasswordAuthenticationMethod(options.Username, options.Password ?? "")];
    }

    // SSH.NET's ForwardedPortLocal needs an already-decided local port rather than picking one
    // itself, so one is reserved by briefly binding a listener to port 0 (the OS assigns a free
    // ephemeral port) and releasing it immediately before SSH.NET binds it for real.
    private static int GetFreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_forwardedPort.IsStarted) _forwardedPort.Stop();
        _forwardedPort.Dispose();
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
    }
}
