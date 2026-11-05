using System.Text;
using Renci.SshNet;

namespace SshOpen;

/// <summary>
/// Lectura y escritura de ficheros como root cuando SFTP devuelve "permiso denegado": se ejecuta
/// <c>sudo cat</c> / <c>sudo tee</c> por un canal de comandos SSH, con la misma clave del usuario.
/// </summary>
internal static class Sudo
{
    public static void Download(ConnectionRequest r, string localPath)
    {
        using var ssh = Sftp.ConnectSsh(r);
        Authorize(ssh, r.Password);
        // base64 para que el contenido pase intacto por la salida de texto del comando.
        string output = Run(ssh, "sudo -n cat -- " + Sftp.ShellQuote(r.RemotePath) + " | base64 -w0", null);
        byte[] bytes = Convert.FromBase64String(output.Trim());
        Sftp.WriteLocal(localPath, file => file.Write(bytes));
    }

    public static void Upload(ConnectionRequest r, string localPath)
    {
        using var ssh = Sftp.ConnectSsh(r);
        Authorize(ssh, r.Password);
        using var file = File.OpenRead(localPath);
        // tee sobrescribe en el sitio: conserva propietario y permisos del fichero.
        Run(ssh, "sudo -n tee -- " + Sftp.ShellQuote(r.RemotePath) + " > /dev/null", file);
    }

    /// <summary>
    /// Deja validadas las credenciales de sudo para que los comandos siguientes usen
    /// <c>sudo -n</c>: así la clave nunca viaja mezclada con el contenido del fichero.
    /// </summary>
    private static void Authorize(SshClient ssh, string password)
    {
        using (var probe = ssh.CreateCommand("sudo -n true"))
        {
            probe.Execute();
            if (probe.ExitStatus == 0)
                return; // NOPASSWD o credenciales ya en caché
        }
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(password + "\n"));
        Run(ssh, "sudo -S -p '' -v", stdin, "sudo ha rechazado la clave o el usuario no puede usar sudo");
    }

    private static string Run(SshClient ssh, string commandText, Stream? stdin, string? failureHint = null)
    {
        using var cmd = ssh.CreateCommand(commandText);
        cmd.CommandTimeout = TimeSpan.FromSeconds(60);
        var input = stdin != null ? cmd.CreateInputStream() : null;
        var pending = cmd.BeginExecute();
        if (input != null)
        {
            stdin!.CopyTo(input);
            input.Dispose(); // EOF para que el comando termine
        }
        string output = cmd.EndExecute(pending);
        if (cmd.ExitStatus != 0)
        {
            string error = cmd.Error.Trim();
            throw new SudoException((failureHint ?? "sudo ha fallado") + (error.Length > 0 ? ":\n" + error : "."));
        }
        return output;
    }
}

internal sealed class SudoException(string message) : Exception(message);
