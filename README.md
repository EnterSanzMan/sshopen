# SshOpen

Plugin de Notepad++ (x64) para abrir y editar ficheros de un servidor Linux por SSH indicando
IP, usuario y clave.

Requisitos: Notepad++ 8.x de 64 bits y Windows 10 o posterior. El servidor solo necesita SSH
con el subsistema SFTP (el habitual de OpenSSH).

## Uso

**Plugins → SshOpen → Abrir fichero remoto (SSH)...**

| Campo          | Ejemplo        |
|----------------|----------------|
| Servidor (IP)  | `192.168.1.10` |
| Puerto         | `22`           |
| Usuario        | `root`         |
| Clave          | `••••••`       |
| Carpeta inicial | opcional: `/var/log`; vacío = carpeta del usuario |

1. Al conectar se abre un explorador con las carpetas y ficheros (nombre, tipo, tamaño, fecha)
   para navegar hasta el fichero. Doble clic o Intro entra en una carpeta o abre el fichero
   seleccionado; Retroceso sube de carpeta; también puedes escribir una ruta y pulsar *Ir*.
   Si la carpeta inicial es un fichero, se empieza en su carpeta con él seleccionado.
   La última carpeta visitada se recuerda para la próxima vez.
2. El fichero se descarga a `%TEMP%\SshOpen\usuario@host_puerto\ruta\remota` y se abre en Notepad++.
3. Cada vez que lo guardas (Ctrl+S) se sube al servidor. La barra de estado muestra `SFTP: subido ...`.
4. Si la subida falla, los cambios siguen en la copia local y puedes reintentar.
   **Subir fichero actual al servidor** fuerza la subida a mano.

### Seguridad

- La clave **solo vive en memoria** mientras Notepad++ está abierto. Si reabres Notepad++ con un
  fichero remoto en la sesión, la pide al guardar.
- La primera conexión a un servidor enseña su huella SHA256 para que la aceptes (como `known_hosts`).
  Si cambia, avisa.
- Configuración en `%APPDATA%\Notepad++\plugins\config\SshOpen.*` (sin claves).

### Limitaciones

- La subida sobrescribe el fichero remoto en el sitio (conserva propietario y permisos); si se
  corta la conexión a mitad, el fichero remoto puede quedar truncado hasta que reintentes.
- Dos ficheros remotos que solo se diferencien en mayúsculas (`/tmp/A` y `/tmp/a`) comparten la
  misma copia local, porque Windows no los distingue.
- Los diálogos no siguen el modo oscuro de Notepad++.

## Compilar e instalar

Necesitas el SDK de .NET 10 y Visual Studio o Build Tools con *Desarrollo para el escritorio con C++*
(NativeAOT usa `link.exe`). La DLL final es nativa: el equipo destino no necesita .NET.

```powershell
.\build.ps1      # genera dist\SshOpen\SshOpen.dll
.\install.ps1    # con Notepad++ cerrado; pide permisos de administrador
```

## Cómo funciona

- Notepad++ carga `plugins\SshOpen\SshOpen.dll` y llama a sus exportaciones C
  (`setInfo`, `getName`, `getFuncsArray`, `beNotified`, `messageProc`, `isUnicode`),
  generadas con `[UnmanagedCallersOnly]` y NativeAOT (`src/Exports.cs`).
- Los diálogos son Win32 puro (`src/DialogHost.cs`, `src/ConnectDialog.cs`, `src/BrowseDialog.cs`):
  WinForms no es compatible con NativeAOT.
- La transferencia usa SFTP con [SSH.NET](https://github.com/sshnet/SSH.NET) (`src/Sftp.cs`),
  con autenticación por clave y *keyboard-interactive*.
- La subida automática escucha `NPPN_FILESAVED` (`src/Plugin.cs`).

## Licencia

[MIT](LICENSE).
