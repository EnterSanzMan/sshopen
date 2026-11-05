# SshOpen

Plugin de Notepad++ (x64) para abrir y editar ficheros de un servidor Linux por SSH indicando
IP, usuario y clave.

Requisitos: Notepad++ 8.x de 64 bits y Windows 10 o posterior. El servidor solo necesita SSH
con el subsistema SFTP (el habitual de OpenSSH).

## Uso

**Plugins → SshOpen → Abrir fichero remoto (SSH)...** (Ctrl+Alt+O; *Subir fichero actual* es Ctrl+Alt+U)

| Campo          | Ejemplo        |
|----------------|----------------|
| Perfil         | opcional: nombre con el que guardar o cargar la conexión |
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
   Si el fichero **cambió en el servidor** desde que lo abriste (otra persona lo editó), avisa con la
   fecha del cambio y pregunta antes de sobrescribirlo.
4. Si la subida falla, los cambios siguen en la copia local y puedes reintentar.
   **Subir fichero actual al servidor** fuerza la subida a mano.
5. Conexiones, descargas y subidas muestran una ventana de progreso con **Cancelar**; Notepad++
   no se bloquea mientras tanto.
6. Al **arrancar Notepad++** con ficheros remotos de la sesión anterior, ofrece conectar (una vez
   por cuenta) para comprobar si han cambiado en el servidor y recargar los que lo hayan hecho.

### Perfiles

Escribe un nombre en *Perfil* y pulsa **Guardar** para recordar servidor, puerto, usuario y
carpeta inicial (la clave nunca se guarda). Al elegir un perfil de la lista se rellenan los campos
y solo queda teclear la clave. **Borrar** elimina el perfil seleccionado.

### Ficheros de root (sudo)

Si tu usuario no puede leer o escribir el fichero (por ejemplo `/etc/nginx/nginx.conf`), el plugin
ofrece hacerlo con `sudo` usando la misma clave: lee con `sudo cat` y guarda con `sudo tee`, que
sobrescribe en el sitio y conserva propietario y permisos. Requiere que el usuario esté en
`sudoers` y que no tenga `requiretty`. Una vez aceptado, no vuelve a preguntar para esa cuenta
mientras Notepad++ siga abierto. La barra de estado indica `(sudo)`.

### Seguridad

- La clave **solo vive en memoria** mientras Notepad++ está abierto. Si reabres Notepad++ con un
  fichero remoto en la sesión, la pide al guardar.
- La primera conexión a un servidor enseña su huella SHA256 para que la aceptes (como `known_hosts`).
  Si cambia, avisa.
- Configuración en `%APPDATA%\Notepad++\plugins\config\SshOpen.*` (sin claves): última conexión,
  perfiles, huellas aceptadas y el mapa de copias locales.

### Limitaciones

- La subida sobrescribe el fichero remoto en el sitio (conserva propietario y permisos); si se
  corta la conexión a mitad, el fichero remoto puede quedar truncado hasta que reintentes.
- Dos ficheros remotos que solo se diferencien en mayúsculas (`/tmp/A` y `/tmp/a`) comparten la
  misma copia local, porque Windows no los distingue.
- Los diálogos no siguen el modo oscuro de Notepad++.
- La detección de cambios en el servidor compara la fecha de modificación (resolución de un
  segundo): dos escrituras en el mismo segundo no se distinguen.
- Los comandos de sudo no se pueden cortar a medias: Cancelar espera a que terminen (máximo 60 s).

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
- La subida automática escucha `NPPN_FILESAVED`; la comprobación al arrancar, `NPPN_READY` (`src/Plugin.cs`).
- Las operaciones de red corren en un hilo del *thread pool* mientras el hilo de la interfaz
  ejecuta el bucle modal del diálogo de progreso (`src/ProgressDialog.cs`); un `Stream` envoltorio
  cuenta los bytes y corta la transferencia al cancelar.
- Con cada fichero se guarda la fecha de modificación remota de la última sincronización; antes de
  subir se vuelve a consultar y, si no coincide, se avisa (`RemoteChangedException`).
- El acceso con sudo abre un canal de comandos SSH aparte (`src/Sudo.cs`): valida las credenciales
  con `sudo -S -v` y después ejecuta `sudo -n cat | base64` y `sudo -n tee` con el contenido por
  la entrada estándar, de modo que la clave nunca se mezcla con el fichero ni aparece en `ps`.

## Licencia

[MIT](LICENSE).
