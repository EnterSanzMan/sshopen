using System.Runtime.InteropServices;
using static SshOpen.DialogHost;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Avance de una transferencia, escrito desde el hilo de trabajo y leído desde la interfaz.</summary>
internal sealed class TransferProgress
{
    private long _done, _total;

    public required CancellationToken Token { get; init; }

    /// <summary>Pasa de "indeterminado" a una barra real con el tamaño total en bytes.</summary>
    public void Start(long total)
    {
        Interlocked.Exchange(ref _done, 0);
        Interlocked.Exchange(ref _total, total);
    }

    public void Add(long bytes) => Interlocked.Add(ref _done, bytes);

    public (long Done, long Total) Snapshot => (Interlocked.Read(ref _done), Interlocked.Read(ref _total));
}

/// <summary>
/// Envuelve el fichero local para contar los bytes que pasan y cortar la transferencia al
/// cancelar: SSH.NET lee o escribe por aquí en su propio hilo, y la excepción sale de
/// DownloadFile/UploadFile sin depender de que acepten un CancellationToken.
/// </summary>
internal sealed class ProgressStream(Stream inner, TransferProgress progress) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        progress.Token.ThrowIfCancellationRequested();
        int n = inner.Read(buffer, offset, count);
        progress.Add(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        progress.Token.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
        progress.Add(count);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Ejecuta una operación de red en un hilo aparte mientras muestra un diálogo modal con barra
/// de progreso y botón Cancelar, de modo que Notepad++ siga repintándose. Devuelve el resultado
/// de la operación o relanza su excepción (OperationCanceledException si se canceló).
/// </summary>
internal static unsafe class ProgressDialog
{
    private const int IdText = 301, IdBar = 302, IdDetail = 303;
    private const nuint TimerId = 1;

    private const int ShowDelayMs = 400;
    private static bool _done, _cancelling, _determinate;
    private static long _started;
    private static IntPtr _hwnd;
    private static Task _task = null!;
    private static TransferProgress _progress = null!;
    private static CancellationTokenSource _cts = null!;

    public static T Run<T>(IntPtr owner, string text, Func<TransferProgress, T> work)
    {
        _done = _cancelling = _determinate = false;
        _cts = new CancellationTokenSource();
        _progress = new TransferProgress { Token = _cts.Token };

        _hwnd = CreateFrame(owner, "SshOpenProgressDialog", &WndProc, Plugin.Name, 420, 118);
        Child(_hwnd, 0, "STATIC", text, 0, 14, 14, 392, 20, IdText);
        IntPtr bar = Child(_hwnd, 0, "msctls_progress32", "", PBS_MARQUEE, 14, 40, 392, 18, IdBar);
        SendMessage(bar, PBM_SETRANGE32, 0, 1000);
        SendMessage(bar, PBM_SETMARQUEE, 1, 0);
        Child(_hwnd, 0, "STATIC", "", 0, 14, 84, 290, 20, IdDetail);
        Child(_hwnd, 0, "BUTTON", "Cancelar", WS_TABSTOP | BS_DEFPUSHBUTTON, 316, 78, 90, 28, IDCANCEL);
        SetFocus(GetDlgItem(_hwnd, IDCANCEL));

        var task = Task.Run(() => work(_progress), CancellationToken.None);
        _task = task;
        SetTimer(_hwnd, TimerId, 100, IntPtr.Zero);

        // Solo se muestra si tarda: una operación rápida (listar una carpeta) no hace parpadear nada.
        _started = Environment.TickCount64;
        RunModal(owner, _hwnd, () => _done, show: false);
        _hwnd = IntPtr.Zero;

        if (!task.IsCompleted)
            _cts.Cancel(); // el bucle salió por WM_QUIT: no dejar la operación suelta
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }
        finally
        {
            _cts.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TIMER:
                    Tick();
                    return 0;
                case WM_ACTIVATE:
                    OnActivate(hwnd, wParam, GetDlgItem(hwnd, IDCANCEL));
                    return 0;
                case WM_CLOSE:
                    Cancel();
                    return 0;
                case WM_COMMAND:
                    if ((int)(wParam & 0xFFFF) == IDCANCEL)
                        Cancel();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Prompts.Error(hwnd, "Error en el diálogo de progreso", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Tick()
    {
        if (_task.IsCompleted)
        {
            KillTimer(_hwnd, TimerId);
            _done = true;
            return;
        }
        if (IsWindowVisible(_hwnd) == 0 && Environment.TickCount64 - _started >= ShowDelayMs)
        {
            DialogHost.Show(_hwnd);
            SetFocus(GetDlgItem(_hwnd, IDCANCEL));
        }
        if (_cancelling)
            return;

        var (done, total) = _progress.Snapshot;
        if (total <= 0)
            return;

        IntPtr bar = GetDlgItem(_hwnd, IdBar);
        if (!_determinate)
        {
            // Pasar de marquesina a barra real exige quitar el estilo PBS_MARQUEE.
            SendMessage(bar, PBM_SETMARQUEE, 0, 0);
            SetWindowLongPtr(bar, GWL_STYLE, GetWindowLongPtr(bar, GWL_STYLE) & ~(nint)PBS_MARQUEE);
            _determinate = true;
        }
        SendMessage(bar, PBM_SETPOS, (nuint)Math.Min(1000, done * 1000 / total), 0);
        SetText(_hwnd, IdDetail, $"{FormatSize(done)} de {FormatSize(total)}");
    }

    private static void Cancel()
    {
        if (_cancelling)
            return;
        _cancelling = true;
        _cts.Cancel();
        SetText(_hwnd, IdDetail, "Cancelando...");
        EnableWindow(GetDlgItem(_hwnd, IDCANCEL), 0);
    }
}
