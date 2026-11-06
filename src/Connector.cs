using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Conexión interactiva: diálogo de datos, progreso cancelable, aceptación de la huella del
/// servidor y reintento si las credenciales fallan. La usan el menú, el panel y el arranque.
/// </summary>
internal static class Connector
{
    /// <summary>
    /// Pide los datos (o, con <paramref name="askFirst"/> = false, prueba primero sin preguntar) y
    /// conecta. Si falla, muestra el error y vuelve a pedirlos. Devuelve null si el usuario cancela.
    /// La petición devuelta es la que funcionó (con sus credenciales, ya recordadas).
    /// </summary>
    public static (SftpClient Client, ConnectionRequest Request)? ConnectWithDialog(
        string title, ConnectionRequest request, ConnectOptions? options, bool askFirst = true)
    {
        request = Credentials.Fill(request);
        bool ask = askFirst;
        while (true)
        {
            if (ask)
            {
                var r = ConnectDialog.Show(Npp.Handle, title, Tr.ConnectButton, request, options);
                if (r is null)
                    return null;
                request = r;
            }
            ask = true;

            try
            {
                var client = Connect(request);
                if (client is null)
                    continue; // huella rechazada
                Credentials.Remember(request);
                return (client, request);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Credentials.ForgetIfRejected(request, ex);
                Prompts.Error(Tr.CannotConnect(request.Display), ex);
            }
        }
    }

    /// <summary>
    /// Conecta mostrando progreso; si la huella del servidor no está aceptada la enseña y, si el
    /// usuario la acepta, la guarda y reintenta. Devuelve null si la rechaza.
    /// </summary>
    public static SftpClient? Connect(ConnectionRequest request)
    {
        while (true)
        {
            try
            {
                return ProgressDialog.Run(Npp.Handle, Tr.Connecting(request.User, request.Host, request.Port),
                    p => Sftp.Connect(request, p.Token));
            }
            catch (UntrustedHostKeyException e)
            {
                if (!Prompts.TrustHost(request, e))
                    return null;
                Config.TrustHost(request.Host, request.Port, e.Fingerprint);
            }
        }
    }

    /// <summary>
    /// Carpeta absoluta por la que empezar a navegar. Si la ruta es un fichero, su carpeta, y
    /// <paramref name="file"/> = nombre del fichero (para dejarlo seleccionado).
    /// </summary>
    public static string StartFolder(SftpClient client, string path, out string? file)
    {
        string resolved = Sftp.ResolvePath(client, path);
        file = null;
        if (!IsFile(client, resolved))
            return resolved;
        file = resolved[(resolved.LastIndexOf('/') + 1)..];
        return Sftp.Parent(resolved);
    }

    private static bool IsFile(SftpClient client, string path)
    {
        try
        {
            return !client.GetAttributes(path).IsDirectory;
        }
        catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
        {
            return false; // el explorador ya avisará si no puede entrar
        }
    }
}
