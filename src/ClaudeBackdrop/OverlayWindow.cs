namespace ClaudeBackdrop;

// Click-through window that sits directly above Claude and is owned by it, so Windows
// keeps it above Claude in the z-order. The GPU renderer fills it through
// DirectComposition, the CPU renderer through UpdateLayeredWindow.
sealed class OverlayWindow : NativeWindow, IDisposable
{
    public event Action Resumed;

    public OverlayWindow(bool composition)
    {
        int ex = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (composition) ex |= Native.WS_EX_NOREDIRECTIONBITMAP;
        CreateHandle(new CreateParams
        {
            Caption = "Claude Backdrop",
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = ex,
            Width = 1,
            Height = 1,
        });
        if (composition) Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
    }

    public void SetOwner(IntPtr owner) => Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, owner);

    protected override void WndProc(ref Message m)
    {
        // Windows re-shows owned popups the moment their owner is restored, while the
        // restore animation still runs. The engine shows this window itself once a
        // fresh frame is ready, so that automatic show is skipped.
        if (m.Msg == Native.WM_SHOWWINDOW && m.WParam != IntPtr.Zero && m.LParam == Native.SW_PARENTOPENING) return;
        if (m.Msg == Native.WM_POWERBROADCAST && m.WParam == Native.PBT_APMRESUMEAUTOMATIC) Resumed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
