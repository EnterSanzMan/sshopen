using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Conexión SFTP que el panel mantiene abierta. Si se cae (servidor reiniciado, VPN, suspensión
/// del equipo) reconecta una vez con las mismas credenciales; si no puede, se cierra y avisa
/// con <paramref name="onLost"/> para que el panel se vacíe.
/// </summary>
internal sealed class PanelConnection(Action onLost)
{
    private SftpClient? _client;

    public bool IsOpen => _client is not null;

    /// <summary>Última petición usada (se conserva al cerrar, para rellenar el diálogo).</summary>
    public ConnectionRequest? Request { get; private set; }

    public void Attach(SftpClient client, ConnectionRequest request)
    {
        Close();
        _client = client;
        Request = request;
    }

    public void Close()
    {
        _client?.Dispose();
        _client = null;
    }

    /// <summary>Ejecuta una operación con la conexión, reconectando una vez si se había caído.</summary>
    public T Run<T>(Func<SftpClient, ConnectionRequest, T> action)
    {
        if (_client is null || Request is null)
            throw new OperationCanceledException(); // se desconectó mientras tanto
        var request = Request;
        if (_client.IsConnected)
        {
            try
            {
                return action(_client, request);
            }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
            }
        }

        Close();
        SftpClient client;
        try
        {
            client = Connector.Connect(request) ?? throw new OperationCanceledException();
        }
        catch (Exception ex)
        {
            Credentials.ForgetIfRejected(request, ex);
            onLost();
            throw;
        }
        _client = client;
        return action(client, request); // un error aquí ya no es de conexión: no se desconecta
    }

    private static bool IsConnectionLost(Exception ex) =>
        ex is SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException;
}
