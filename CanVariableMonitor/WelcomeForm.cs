using System.Drawing.Drawing2D;

namespace CanVariableMonitor;

internal sealed class WelcomeForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public WelcomeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 360);
        BackColor = Color.FromArgb(9, 14, 20);
        DoubleBuffered = true;
        ShowInTaskbar = false;

        _timer.Interval = 1400;
        _timer.Tick += delegate
        {
            _timer.Stop();
            Close();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = ClientRectangle;

        using LinearGradientBrush bg = new(rect, Color.FromArgb(7, 13, 21), Color.FromArgb(26, 36, 48), 35f);
        g.FillRectangle(bg, rect);

        using GraphicsPath glowPath = new();
        glowPath.AddEllipse(new Rectangle(rect.Width / 2 - 260, rect.Height / 2 - 120, 520, 240));
        using PathGradientBrush glow = new(glowPath)
        {
            CenterColor = Color.FromArgb(115, 70, 180, 255),
            SurroundColors = new[] { Color.FromArgb(0, 70, 180, 255) }
        };
        g.FillPath(glow, glowPath);

        string title = "keil for you";
        using Font titleFont = new("Segoe UI Black", 54f, FontStyle.Bold);
        using GraphicsPath textPath = new();
        SizeF textSize = g.MeasureString(title, titleFont);
        float x = (rect.Width - textSize.Width) / 2f;
        float y = (rect.Height - textSize.Height) / 2f - 12f;
        textPath.AddString(title, titleFont.FontFamily, (int)FontStyle.Bold, g.DpiY * 54f / 72f, new PointF(x, y), StringFormat.GenericDefault);

        using Pen shadowPen = new(Color.FromArgb(140, 0, 0, 0), 10f) { LineJoin = LineJoin.Round };
        g.DrawPath(shadowPen, textPath);
        using LinearGradientBrush textBrush = new(new RectangleF(x, y, textSize.Width, textSize.Height), Color.FromArgb(255, 245, 180), Color.FromArgb(70, 210, 255), 8f);
        g.FillPath(textBrush, textPath);
        using Pen edgePen = new(Color.FromArgb(210, 255, 255, 255), 1.4f) { LineJoin = LineJoin.Round };
        g.DrawPath(edgePen, textPath);

        string sub = "长沙康旭电子科技有限公司";
        using Font subFont = new("Microsoft YaHei UI", 10f, FontStyle.Regular);
        SizeF subSize = g.MeasureString(sub, subFont);
        using SolidBrush subBrush = new(Color.FromArgb(170, 220, 230, 240));
        g.DrawString(sub, subFont, subBrush, (rect.Width - subSize.Width) / 2f, y + textSize.Height + 18f);
    }
}
