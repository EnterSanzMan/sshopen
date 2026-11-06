# SshOpen

Plugin de Notepad++ (x64) para abrir y editar ficheros de un servidor Linux por SSH/SFTP con
usuario y contraseña o clave privada.

Requisitos: Notepad++ 8.4 o posterior de 64 bits y Windows 10 o posterior. El servidor solo
necesita SSH con el subsistema SFTP (el habitual de OpenSSH).

## Uso

En **Plugins → SshOpen**:

| Comando | Atajo | Qué hace |
|---|---|---|
| Abrir fichero remoto (SSH)... | Ctrl+Alt+O | Conecta y abre el explorador de carpetas |
| Subir fichero actual al servidor | Ctrl+Alt+U | Guarda y sube a mano |
| Panel remoto | | Muestra u oculta el panel acoplable con el árbol de carpetas |

Los atajos se cambian en *Configuración → Administrador de atajos*.

| Campo          | Ejemplo        |
|----------------|----------------|
| Perfil         | opcional: nombre con el que guardar o cargar la conexión |
| Servidor (IP)  | `192.168.1.10` |
| Puerto         | `22`           |
| Usuario        | `root`         |
| Contraseña     | opcional si usas clave privada |
| Clave privada  | opcional: `C:\Users\yo\.ssh\id_ed25519` o un `.ppk` |
| Frase de la clave | solo si la clave está protegida |
| Carpeta inicial | opcional: `/var/log`; vacío = carpeta del usuario |

1. Al conectar se abre un explorador con las carpetas y ficheros (nombre, tipo, tamaño, fecha)
   para navegar hasta el fichero:
   - Doble clic o Intro entra en una carpeta o abre el fichero; Retroceso sube de carpeta;
     también puedes escribir una ruta y pulsar *Ir*.
   - Pulsa la cabecera de una columna para ordenar por ella (otra vez, al revés). Las carpetas
     van siempre primero.
   - *Filtro* muestra solo lo que contiene ese texto, o lo que encaja con comodines (`*.conf`).
   - *Mostrar ocultos* enseña u oculta los ficheros que empiezan por punto (se recuerda).
   - La última carpeta visitada se recuerda para la próxima vez.
2. El fichero se descarga a `%TEMP%\SshOpen\usuario@host_puerto\ruta\remota` y se abre en Notepad++.
3. Cada vez que lo guardas (Ctrl+S) se sube al servidor. La barra de estado muestra `SFTP: subido ...`.
   Si el fichero **cambió en el servidor** desde que lo abriste (otra persona lo editó), avisa con la
   fecha del cambio y pregunta antes de sobrescribirlo.
4. Si la subida falla, los cambios siguen en la copia local y puedes reintentar.
5. Conexiones, descargas y subidas muestran una ventana de progreso con **Cancelar**; Notepad++
   no se bloquea mientras tanto.
6. Al **arrancar Notepad++** con ficheros remotos de la sesión anterior, ofrece conectar (una vez
   por cuenta) para comprobar si han cambiado en el servidor y recargar los que lo hayan hecho.

### Panel remoto

Un panel acoplable (a la izquierda por defecto; se puede arrastrar o dejar flotante) con el árbol
de carpetas del servidor, que mantiene la conexión abierta mientras trabajas:

- **Conectar...** usa el mismo diálogo que *Abrir fichero remoto* y despliega el árbol hasta la
  carpeta inicial. Las carpetas se cargan al desplegarlas.
- Doble clic o Intro sobre un fichero lo abre; **Actualizar** o F5 recarga la carpeta seleccionada.
- *Ocultos* enseña u oculta los ficheros que empiezan por punto.
- Si la conexión se cae (VPN, suspensión del equipo), reconecta sola al usarlo.
- Si estaba abierto al cerrar Notepad++, vuelve a aparecer al arrancar (sin conectar).

### Clave privada

Admite claves OpenSSH (`id_ed25519`, `id_rsa`, `id_ecdsa`), PEM y PuTTY (`.ppk`), con o sin frase.
El botón **...** abre `%USERPROFILE%\.ssh`. Si la clave no tiene frase, al guardar los ficheros no
se pide nada; si la tiene, la frase se pide una vez por sesión de Notepad++.
Si además escribes la contraseña, se usa como segundo método (o para sudo).

### Perfiles

Escribe un nombre en *Perfil* y pulsa **Guardar** para recordar servidor, puerto, usuario,
carpeta inicial y ruta de la clave privada (la contraseña y la frase nunca se guardan). Al elegir
un perfil se rellenan los campos. **Borrar** elimina el perfil seleccionado.

### Ficheros de root (sudo)

Si tu usuario no puede leer o escribir el fichero (por ejemplo `/etc/nginx/nginx.conf`), el plugin
ofrece hacerlo con `sudo` usando la misma contraseña: lee con `sudo cat` y guarda con `sudo tee`,
que sobrescribe en el sitio y conserva propietario y permisos. Requiere que el usuario esté en
`sudoers` y que no tenga `requiretty`. Si entras con clave privada y sudo pide contraseña, escríbela
también en *Contraseña*. Una vez aceptado, no vuelve a preguntar para esa cuenta mientras
Notepad++ siga abierto. La barra de estado indica `(sudo)`.

### Seguridad

- La contraseña y la frase de la clave **solo viven en memoria** mientras Notepad++ está abierto.
- La primera conexión a un servidor enseña su huella SHA256 para que la aceptes (como `known_hosts`).
  Si cambia, avisa.
- Configuración en `%APPDATA%\Notepad++\plugins\config\SshOpen.*` (sin secretos): última conexión,
  opciones, perfiles, huellas aceptadas y el mapa de copias locales.

### Limitaciones

- La subida sobrescribe el fichero remoto en el sitio (conserva propietario y permisos); si se
  corta la conexión a mitad, el fichero remoto puede quedar truncado hasta que reintentes.
- Dos ficheros remotos que solo se diferencien en mayúsculas (`/tmp/A` y `/tmp/a`) comparten la
  misma copia local, porque Windows no los distingue.
- La detección de cambios en el servidor compara la fecha de modificación (resolución de un
  segundo): dos escrituras en el mismo segundo no se distinguen.
- Los comandos de sudo no se pueden cortar a medias: Cancelar espera a que terminen (máximo 60 s).
- No usa el agente de claves (Pageant u `ssh-agent`): la clave se indica como fichero.

## Compilar e instalar

Necesitas el SDK de .NET 10 y Visual Studio o Build Tools con *Desarrollo para el escritorio con C++*
(NativeAOT usa `link.exe`). La DLL final es nativa: el equipo destino no necesita .NET.

```powershell
.\build.ps1      # genera dist\SshOpen\SshOpen.dll
.\install.ps1    # con Notepad++ cerrado; pide permisos de administrador
.\package.ps1    # opcional: zip de publicación en dist\release
```

## Publicar una versión

1. Cambia `<Version>` y `<FileVersion>` en `SshOpen.csproj` (por ejemplo `1.1.0` y `1.1.0.0`).
2. Haz commit y sube una etiqueta con la misma versión:

   ```powershell
   git tag v1.1.0
   git push origin v1.1.0
   ```

3. GitHub Actions (`.github/workflows/build.yml`) compila, comprueba que la etiqueta coincide con
   la versión de la DLL y crea la release con:
   - `SshOpen-<versión>-x64.zip`: `SshOpen.dll` en la raíz y `doc/` con este README y la licencia.
   - `SshOpen-<versión>-x64.zip.sha256`.
   - `nppPluginList-entry.json`: la entrada para la lista oficial de plugins.

En cada push y pull request se compila y el zip queda como artefacto de la ejecución.

### Lista oficial de plugins de Notepad++

Para que aparezca en *Plugins → Administrador de plugins*:

1. Haz un fork de [nppPluginList](https://github.com/notepad-plus-plus/nppPluginList).
2. Añade el contenido de `nppPluginList-entry.json` de la release a `src/pl.x64.json`, en orden
   alfabético por `folder-name`. El `id` es el SHA-256 del zip publicado y `repository` su URL de
   descarga, así que deben salir de la release, no de un zip compilado en local.
3. Abre un pull request. Su validador descarga el zip, comprueba la huella y que la versión de
   `SshOpen.dll` coincide con `version`.

## Cómo funciona

- Notepad++ carga `plugins\SshOpen\SshOpen.dll` y llama a sus exportaciones C
  (`setInfo`, `getName`, `getFuncsArray`, `beNotified`, `messageProc`, `isUnicode`),
  generadas con `[UnmanagedCallersOnly]` y NativeAOT (`src/Exports.cs`).
- Los diálogos y el panel son Win32 puro (`src/DialogHost.cs`, `src/ConnectDialog.cs`,
  `src/BrowseDialog.cs`, `src/RemotePanel.cs`): WinForms no es compatible con NativeAOT. Siguen el
  modo oscuro de Notepad++ (`NPPM_DARKMODESUBCLASSANDTHEME`, Notepad++ 8.5.4 o posterior).
- El panel se registra con `NPPM_DMMREGASDCKDLG`; Notepad++ recuerda dónde estaba acoplado.
  Su árbol está en `src/RemoteTree.cs` y su conexión persistente, con reconexión, en
  `src/PanelConnection.cs`.
- `src/Plugin.cs` orquesta abrir, subir y comprobar al arrancar. Se apoya en `src/Connector.cs`
  (diálogo, progreso, huella y reintento al conectar), `src/Credentials.cs` (secretos en memoria),
  `src/LocalCopies.cs` (copias en `%TEMP%`) y `src/Prompts.cs` (todas las preguntas y errores).
- La transferencia usa SFTP con [SSH.NET](https://github.com/sshnet/SSH.NET) (`src/Sftp.cs`),
  con autenticación por clave privada, contraseña y *keyboard-interactive*.
- La subida automática escucha `NPPN_FILESAVED`; la comprobación al arrancar, `NPPN_READY` (`src/Plugin.cs`).
- Las operaciones de red corren en un hilo del *thread pool* mientras el hilo de la interfaz
  ejecuta el bucle modal del diálogo de progreso (`src/ProgressDialog.cs`); un `Stream` envoltorio
  cuenta los bytes y corta la transferencia al cancelar.
- Con cada fichero se guarda la fecha de modificación remota de la última sincronización; antes de
  subir se vuelve a consultar y, si no coincide, se avisa (`RemoteChangedException`).
- El acceso con sudo abre un canal de comandos SSH aparte (`src/Sudo.cs`): valida las credenciales
  con `sudo -S -v` y después ejecuta `sudo -n cat | base64` y `sudo -n tee` con el contenido por
  la entrada estándar, de modo que la contraseña nunca se mezcla con el fichero ni aparece en `ps`.

## Licencia

[MIT](LICENSE).
