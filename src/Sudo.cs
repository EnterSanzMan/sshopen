using System.Text;
using Renci.SshNet;

namespace SshOpen;

/// <summary>
/// Lectura y escritura de ficheros como root cuando SFTP devuelve "permiso denegado": se ejecuta
/// <c>sudo cat</c> / <c>sudo tee</c> por un canal de comandos SSH, con la misma clave del usuario.
/// Los comandos no admiten cancelación a medias: Cancelar espera a que terminen (máximo 60 s).
/// </summary>
internal static class Sudo
{
    /// <summary>Descarga el fichero y devuelve su fecha de modificación remota (segundos Unix).</summary>
    public static long Download(ConnectionRequest r, string localPath, TransferProgress progress)
    {
        using var ssh = Sftp.ConnectSsh(r, progress.Token);
        Authorize(ssh, r.Password);
        long stamp = Stat(ssh, r.RemotePath);
        // base64 para que el contenido pase intacto por la salida de texto del comando.
        string output = Run(ssh, "sudo -n cat -- " + Sftp.ShellQuote(r.RemotePath) + " | base64 -w0", null);
        byte[] bytes = Convert.FromBase64String(output.Trim());
        progress.Start(bytes.Length);
        Sftp.WriteLocal(localPath, file => new ProgressStream(file, progress).Write(bytes, 0, bytes.Length));
        return stamp;
    }

    /// <summary>Sube la copia local con la misma comprobación de cambios que <see cref="Sftp.Upload"/>.</summary>
    public static long Upload(ConnectionRequest r, string localPath, TransferProgress progress, long expectedStamp, bool force)
    {
        using var ssh = Sftp.ConnectSsh(r, progress.Token);
        Authorize(ssh, r.Password);
        if (!force)
            Sftp.CheckUnchanged(Stat(ssh, r.RemotePath), expectedStamp);
        using var file = File.OpenRead(localPath);
        progress.Start(file.Length);
        // tee sobrescribe en el sitio: conserva propietario y permisos del fichero.
        Run(ssh, "sudo -n tee -- " + Sftp.ShellQuote(r.RemotePath) + " > /dev/null", new ProgressStream(file, progress));
        return Stat(ssh, r.RemotePath);
    }

    /// <summary>Fecha de modificación (segundos Unix) vía <c>stat</c>; 0 si no existe.</summary>
    private static long Stat(SshClient ssh, string path)
    {
        using var cmd = ssh.CreateCommand("sudo -n stat -c %Y -- " + Sftp.ShellQuote(path));
        string output = cmd.Execute();
        return cmd.ExitStatus == 0 && long.TryParse(output.Trim(), out long stamp) ? stamp : 0;
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
        if (password.Length == 0) // entrada con clave privada y sin contraseña
            throw new SudoException("sudo pide la contraseña del usuario: escríbela también en el campo Contraseña.", badPassword: true);
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(password + "\n"));
        try
        {
            Run(ssh, "sudo -S -p '' -v", stdin, "sudo ha rechazado la contraseña o el usuario no puede usar sudo");
        }
        catch (SudoException e)
        {
            // Con clave privada SSH no ha comprobado la contraseña: puede ser incorrecta.
            throw new SudoException(e.Message, badPassword: true);
        }
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

internal sealed class SudoException(string message, bool badPassword = false) : Exception(message)
{
    /// <summary>sudo rechazó la contraseña (o no había): hay que volver a pedirla.</summary>
    public bool BadPassword => badPassword;
}
