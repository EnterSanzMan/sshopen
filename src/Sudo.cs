using System.Text;
using Renci.SshNet;

namespace SshOpen;

/// <summary>
/// Lectura y escritura de ficheros como root cuando SFTP devuelve "permiso denegado": se ejecuta
/// <c>sudo cat</c> / <c>sudo tee</c> por un canal de comandos SSH, con la misma clave del usuario.
/// Los comandos no admiten cancelación a medias: Cancelar espera a que terminen (máximo 60 s).
/// </summary>
/// <remarks>
/// Cada comando SSH es un proceso nuevo sin terminal, y sudo recuerda la contraseña por terminal
/// (o, sin ella, por proceso padre): validarla con <c>sudo -v</c> en un comando no sirve para el
/// siguiente. Por eso, si sudo pide contraseña, cada orden la lleva: <c>sudo -k -S</c> lee una
/// línea de la entrada estándar (byte a byte, sin consumir más) y el resto llega al comando.
/// <c>-k</c> obliga a pedirla siempre, así la clave nunca puede acabar dentro del fichero.
/// </remarks>
internal static class Sudo
{
    /// <summary>Descarga el fichero y devuelve su fecha de modificación remota (segundos Unix).</summary>
    public static long Download(ConnectionRequest r, string localPath, TransferProgress progress)
    {
        using var ssh = Sftp.ConnectSsh(r, progress.Token);
        var sudo = Authorize(ssh, r.Password);
        long stamp = sudo.Stat(r.RemotePath);
        // base64 para que el contenido pase intacto por la salida de texto del comando.
        string output = sudo.Run("cat -- " + Sftp.ShellQuote(r.RemotePath) + " | base64 -w0", null);
        byte[] bytes = Convert.FromBase64String(output.Trim());
        progress.Start(bytes.Length);
        Sftp.WriteLocal(localPath, file => new ProgressStream(file, progress).Write(bytes, 0, bytes.Length));
        return stamp;
    }

    /// <summary>Sube la copia local con la misma comprobación de cambios que <see cref="Sftp.Upload"/>.</summary>
    public static long Upload(ConnectionRequest r, string localPath, TransferProgress progress, long expectedStamp, bool force)
    {
        using var ssh = Sftp.ConnectSsh(r, progress.Token);
        var sudo = Authorize(ssh, r.Password);
        if (!force)
            Sftp.CheckUnchanged(sudo.Stat(r.RemotePath), expectedStamp);
        using var file = File.OpenRead(localPath);
        progress.Start(file.Length);
        // tee sobrescribe en el sitio: conserva propietario y permisos del fichero.
        sudo.Run("tee -- " + Sftp.ShellQuote(r.RemotePath) + " > /dev/null", new ProgressStream(file, progress));
        return sudo.Stat(r.RemotePath);
    }

    /// <summary>
    /// Comprueba cómo hay que llamar a sudo: sin contraseña (NOPASSWD) o con ella en cada orden,
    /// y en ese caso que sudo la acepta.
    /// </summary>
    private static Session Authorize(SshClient ssh, string password)
    {
        using (var probe = ssh.CreateCommand("sudo -n true"))
        {
            probe.Execute();
            if (probe.ExitStatus == 0)
                return new Session(ssh, null); // NOPASSWD
        }
        if (password.Length == 0) // entrada con clave privada y sin contraseña
            throw new SudoException(Tr.SudoNeedsPassword, badPassword: true);
        var session = new Session(ssh, password);
        try
        {
            session.Run("true", null, Tr.SudoRejected);
        }
        catch (SudoException e)
        {
            // Con clave privada SSH no ha comprobado la contraseña: puede ser incorrecta.
            throw new SudoException(e.Message, badPassword: true);
        }
        return session;
    }

    /// <param name="password">Contraseña de sudo; null si el usuario tiene NOPASSWD.</param>
    private sealed class Session(SshClient ssh, string? password)
    {
        /// <summary>Fecha de modificación (segundos Unix) vía <c>stat</c>; 0 si no existe.</summary>
        public long Stat(string path)
        {
            // Si sudo falla, Run lanza la excepción: así un error de sudo no se confunde con
            // "el fichero ya no existe".
            const string script = "if [ -e \"$1\" ]; then stat -c %Y -- \"$1\"; else echo 0; fi";
            string output = Run("sh -c " + Sftp.ShellQuote(script) + " sh " + Sftp.ShellQuote(path), null);
            return long.TryParse(output.Trim(), out long stamp) ? stamp : 0;
        }

        /// <summary>Ejecuta <paramref name="command"/> como root y devuelve su salida.</summary>
        public string Run(string command, Stream? stdin, string? failureHint = null)
        {
            string prefix = password == null ? "sudo -n " : "sudo -k -S -p '' ";
            using var cmd = ssh.CreateCommand(prefix + command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            // SSH.NET solo deja crear el flujo de entrada con el comando ya en marcha
            // (antes de BeginExecute lanza "The input stream can be used only during execution").
            var pending = cmd.BeginExecute();
            if (password != null || stdin != null)
            {
                using var input = cmd.CreateInputStream();
                if (password != null)
                {
                    byte[] line = Encoding.UTF8.GetBytes(password + "\n");
                    input.Write(line, 0, line.Length);
                }
                stdin?.CopyTo(input);
            } // al cerrarlo se envía EOF para que el comando termine
            string output = cmd.EndExecute(pending);
            if (cmd.ExitStatus != 0)
            {
                string error = cmd.Error.Trim();
                throw new SudoException((failureHint ?? Tr.SudoFailed) + (error.Length > 0 ? ":\n" + error : "."));
            }
            return output;
        }
    }
}

internal sealed class SudoException(string message, bool badPassword = false) : Exception(message)
{
    /// <summary>sudo rechazó la contraseña (o no había): hay que volver a pedirla.</summary>
    public bool BadPassword => badPassword;
}
