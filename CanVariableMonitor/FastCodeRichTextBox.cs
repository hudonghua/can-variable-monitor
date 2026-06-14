using System.Runtime.InteropServices;

namespace CanVariableMonitor;

internal sealed class FastCodeRichTextBox : RichTextBox
{
    private const int WmMouseWheel = 0x020A;
    private const int EmLineScroll = 0x00B6;
    private int _wheelRemainder;

    public event EventHandler? ImmediateWheel;
    public event EventHandler? WheelScrolled;
    public event MouseEventHandler? ControlMouseWheel;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel)
        {
            ImmediateWheel?.Invoke(this, EventArgs.Empty);
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                int ctrlDelta = unchecked((short)((long)m.WParam >> 16));
                Point location = PointToClient(MousePosition);
                ControlMouseWheel?.Invoke(this, new MouseEventArgs(MouseButtons.None, 0, location.X, location.Y, ctrlDelta));
                m.Result = IntPtr.Zero;
                return;
            }

            int delta = unchecked((short)((long)m.WParam >> 16));
            _wheelRemainder += delta;
            int notches = _wheelRemainder / 120;
            if (notches != 0)
            {
                _wheelRemainder -= notches * 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines < 6)
                {
                    lines = 6;
                }
                SendMessage(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(-notches * lines));
                WheelScrolled?.Invoke(this, EventArgs.Empty);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
