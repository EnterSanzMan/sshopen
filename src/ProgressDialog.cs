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
internal sealed unsafe class ProgressDialog : ModalDialog
{
    private const int IdText = 301, IdBar = 302, IdDetail = 303;
    private const nuint TimerId = 1;
    private const int ShowDelayMs = 400;

    private readonly CancellationTokenSource _cts = new();
    private readonly TransferProgress _progress;
    private Task _task = Task.CompletedTask;
    private bool _cancelling, _determinate;
    private long _started;

    private ProgressDialog() => _progress = new TransferProgress { Token = _cts.Token };

    public static T Run<T>(HWND owner, string text, Func<TransferProgress, T> work)
    {
        var dialog = new ProgressDialog();
        try
        {
            return dialog.Execute(owner, text, work);
        }
        finally
        {
            dialog._cts.Dispose();
        }
    }

    protected override HWND DefaultFocus => Item(IDCANCEL);

    private T Execute<T>(HWND owner, string text, Func<TransferProgress, T> work)
    {
        CreateFrame(owner, "SshOpenProgressDialog", Plugin.Name, 420, 118);
        Task<T> task;
        try
        {
            Child(0, "STATIC", text, 0, 14, 14, 392, 20, IdText);
            HWND bar = Child(0, "msctls_progress32", "", PBS_MARQUEE, 14, 40, 392, 18, IdBar);
            PInvoke.SendMessage(bar, PBM_SETRANGE32, 0, 1000);
            PInvoke.SendMessage(bar, PBM_SETMARQUEE, 1, 0);
            Child(0, "STATIC", "", 0, 14, 84, 290, 20, IdDetail);
            Child(0, "BUTTON", Tr.Cancel, WS_TABSTOP | BS_DEFPUSHBUTTON, 316, 78, 90, 28, IDCANCEL);
            SetFocus(Item(IDCANCEL));

            task = Task.Run(() => work(_progress), CancellationToken.None);
            _task = task;
            SetTimer(Handle, TimerId, 100, null);
            _started = Environment.TickCount64;
        }
        catch
        {
            Close();
            throw;
        }

        // Solo se muestra si tarda: una operación rápida (listar una carpeta) no hace parpadear nada.
        RunModal(show: false);

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
    }

    protected override nint? OnDialogMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                Tick();
                return 0;
            case WM_COMMAND:
                if (LoWord(wParam) == IDCANCEL)
                    OnCancel();
                return 0;
        }
        return null;
    }

    protected override void OnCancel()
    {
        if (_cancelling)
            return;
        _cancelling = true;
        _cts.Cancel();
        SetText(IdDetail, Tr.Cancelling);
        Enable(IDCANCEL, false);
    }

    private void Tick()
    {
        if (_task.IsCompleted)
        {
            KillTimer(Handle, TimerId);
            Done = true;
            return;
        }
        if (!IsWindowVisible(Handle) && Environment.TickCount64 - _started >= ShowDelayMs)
        {
            ShowFrame();
            SetFocus(Item(IDCANCEL));
        }
        if (_cancelling)
            return;

        var (done, total) = _progress.Snapshot;
        if (total <= 0)
            return;

        HWND bar = Item(IdBar);
        if (!_determinate)
        {
            // Pasar de marquesina a barra real exige quitar el estilo PBS_MARQUEE.
            PInvoke.SendMessage(bar, PBM_SETMARQUEE, 0, 0);
            SetWindowLongPtr(bar, WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                GetWindowLongPtr(bar, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & ~(nint)PBS_MARQUEE);
            _determinate = true;
        }
        PInvoke.SendMessage(bar, PBM_SETPOS, (nuint)Math.Min(1000, done * 1000 / total), 0);
        SetText(IdDetail, Tr.TransferDetail(done, total));
    }
}
