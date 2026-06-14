using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace CanVariableMonitor;

internal sealed record FlowChartNode(string Id, string Text, RectangleF Bounds, ushort TraceId, string FunctionName = "", string Kind = "normal", string Group = "", int HierarchyLevel = -1);

internal sealed record FlowChartEdge(string FromId, string ToId, string Label = "");

internal sealed class FlowChartView : ScrollableControl
{
    private readonly System.Windows.Forms.Timer _animationTimer = new();
    private IReadOnlyList<FlowChartNode> _nodes = Array.Empty<FlowChartNode>();
    private IReadOnlyList<FlowChartEdge> _edges = Array.Empty<FlowChartEdge>();
    private Dictionary<string, FlowChartNode> _nodeById = new(StringComparer.OrdinalIgnoreCase);
    private ushort _highlightTraceId = ushort.MaxValue;
    private int _animatedEdgeIndex;
    private float _animationProgress;
    private float _animationDashOffset;
    private bool _animationEnabled;
    private Color _surface = Color.FromArgb(15, 23, 42);
    private Color _surfaceAlt = Color.FromArgb(20, 30, 48);
    private Color _ink = Color.FromArgb(229, 231, 235);
    private Color _muted = Color.FromArgb(148, 163, 184);
    private Color _accent = Color.FromArgb(14, 165, 233);
    private Color _line = Color.FromArgb(58, 50, 34);
    private bool _lightPalette;
    private bool _highContrastNodes;
    private float _treeFontSize = 15f;
    private string _focusedFunctionName = "";

    public event EventHandler<FlowChartNode>? NodeClick;
    public event EventHandler<FlowChartNode>? NodeDoubleClick;

    public bool LastClickHitTreeExpander { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float TreeFontSize
    {
        get => _treeFontSize;
        set
        {
            float next = Math.Clamp(value, 10f, 22f);
            if (Math.Abs(_treeFontSize - next) < 0.01f)
            {
                return;
            }

            _treeFontSize = next;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HighContrastNodes
    {
        get => _highContrastNodes;
        set
        {
            if (_highContrastNodes == value)
            {
                return;
            }

            _highContrastNodes = value;
            Invalidate();
        }
    }

    public FlowChartView()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = _surface;
        Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _animationTimer.Interval = 70;
    }

    public void SetPalette(Color surface, Color surfaceAlt, Color ink, Color muted, Color accent, Color line)
    {
        _surface = surface;
        _surfaceAlt = surfaceAlt;
        _ink = ink;
        _muted = muted;
        _accent = accent;
        _line = line;
        _lightPalette = IsLightColor(surface);
        BackColor = _surface;
        Invalidate();
    }

    public void SetGraph(IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges)
    {
        _nodes = nodes;
        _edges = edges;
        _nodeById = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        _animatedEdgeIndex = 0;
        _animationProgress = 0f;
        _animationDashOffset = 0f;
        RectangleF bounds = RectangleF.Empty;
        foreach (FlowChartNode node in nodes)
        {
            bounds = bounds == RectangleF.Empty ? node.Bounds : RectangleF.Union(bounds, node.Bounds);
        }
        AutoScrollMinSize = new Size((int)Math.Ceiling(bounds.Right + 40), (int)Math.Ceiling(bounds.Bottom + 40));
        UpdateAnimationTimer();
        Invalidate();
    }

    public bool ContainsFunction(string functionName)
    {
        return !string.IsNullOrWhiteSpace(functionName) &&
            _nodes.Any(node => FunctionNameEquals(node.FunctionName, functionName));
    }

    public bool ScrollFunctionIntoView(string functionName, int padding = 72)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        FlowChartNode? node = _nodes.FirstOrDefault(item =>
            FunctionNameEquals(item.FunctionName, functionName));
        if (node == null)
        {
            return false;
        }

        RectangleF bounds = node.Bounds;
        int visibleLeft = -AutoScrollPosition.X;
        int visibleTop = -AutoScrollPosition.Y;
        int visibleRight = visibleLeft + ClientSize.Width;
        int visibleBottom = visibleTop + ClientSize.Height;
        int nextX = visibleLeft;
        int nextY = visibleTop;

        if (bounds.Left < visibleLeft + padding)
        {
            nextX = Math.Max(0, (int)Math.Floor(bounds.Left) - padding);
        }
        else if (bounds.Right > visibleRight - padding)
        {
            nextX = Math.Max(0, (int)Math.Ceiling(bounds.Right) - ClientSize.Width + padding);
        }

        if (bounds.Top < visibleTop + padding)
        {
            nextY = Math.Max(0, (int)Math.Floor(bounds.Top) - padding);
        }
        else if (bounds.Bottom > visibleBottom - padding)
        {
            nextY = Math.Max(0, (int)Math.Ceiling(bounds.Bottom) - ClientSize.Height + padding);
        }

        if (nextX == visibleLeft && nextY == visibleTop)
        {
            Invalidate();
            return true;
        }

        AutoScrollPosition = new Point(nextX, nextY);
        Invalidate();
        return true;
    }

    public bool CenterFunctionInView(string functionName, int padding = 72)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        FlowChartNode? node = _nodes.FirstOrDefault(item =>
            FunctionNameEquals(item.FunctionName, functionName));
        if (node == null)
        {
            return false;
        }

        RectangleF bounds = node.Bounds;
        int visibleLeft = -AutoScrollPosition.X;
        int nextX = visibleLeft;
        int nextY = Math.Max(0, (int)Math.Round(bounds.Top + bounds.Height / 2f - ClientSize.Height / 2f));

        int maxX = Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width);
        int maxY = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        int visibleRight = visibleLeft + ClientSize.Width;
        if (bounds.Left < visibleLeft + padding)
        {
            nextX = Math.Max(0, (int)Math.Floor(bounds.Left) - padding);
        }
        else if (bounds.Right > visibleRight - padding)
        {
            nextX = Math.Max(0, (int)Math.Ceiling(bounds.Right) - ClientSize.Width + padding);
        }

        nextX = Math.Clamp(nextX, 0, maxX);
        nextY = Math.Clamp(nextY, 0, maxY);
        AutoScrollPosition = new Point(nextX, nextY);
        Invalidate();
        return true;
    }

    public void SetFocusedFunction(string functionName)
    {
        string next = functionName.Trim();
        if (_focusedFunctionName.Equals(next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _focusedFunctionName = next;
        Invalidate();
    }

    public void SetAnimationEnabled(bool enabled)
    {
        _animationEnabled = false;
        UpdateAnimationTimer();
        Invalidate();
    }

    public void SetHighlight(ushort traceId)
    {
        if (_highlightTraceId == traceId)
        {
            return;
        }

        _highlightTraceId = traceId;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimationTimer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        using var edgePen = new Pen(Color.FromArgb(_lightPalette ? 125 : 105, _line), _lightPalette ? 1.15f : 1.35f)
        {
            CustomEndCap = new AdjustableArrowCap(4, 5)
        };
        using var highlightPen = new Pen(_accent, _lightPalette ? 2.1f : 2.4f)
        {
            CustomEndCap = new AdjustableArrowCap(4, 5)
        };
        foreach (FlowChartEdge edge in _edges)
        {
            if (!_nodeById.TryGetValue(edge.FromId, out FlowChartNode? from) ||
                !_nodeById.TryGetValue(edge.ToId, out FlowChartNode? to) ||
                from == null ||
                to == null)
            {
                continue;
            }
            if (IsTreeNode(from) || IsTreeNode(to))
            {
                continue;
            }
            if (IsHierarchyEdge(edge))
            {
                continue;
            }
            bool hot = from.TraceId == _highlightTraceId || to.TraceId == _highlightTraceId;
            DrawEdge(g, from.Bounds, to.Bounds, hot ? highlightPen : edgePen, edge.Label, hot);
        }

        foreach (FlowChartEdge edge in _edges)
        {
            if (!_nodeById.TryGetValue(edge.FromId, out FlowChartNode? from) ||
                !_nodeById.TryGetValue(edge.ToId, out FlowChartNode? to) ||
                from == null ||
                to == null ||
                !IsHierarchyEdge(edge))
            {
                continue;
            }
            if (IsTreeNode(from) || IsTreeNode(to))
            {
                continue;
            }

            bool hot = from.TraceId == _highlightTraceId || to.TraceId == _highlightTraceId;
            Color hierarchyColor = hot
                ? (_lightPalette ? Color.FromArgb(14, 116, 64) : Color.FromArgb(74, 222, 128))
                : HierarchyAccentForLevel(to.HierarchyLevel);
            using var hierarchyPen = new Pen(hierarchyColor, hot ? (_lightPalette ? 2.0f : 2.2f) : 1.15f)
            {
                CustomEndCap = new AdjustableArrowCap(4, 5),
                DashStyle = DashStyle.Solid,
                LineJoin = LineJoin.Round
            };
            DrawEdge(g, from.Bounds, to.Bounds, hierarchyPen, edge.Label, hot);
        }

        DrawColumnHeaders(g);

        foreach (FlowChartNode node in _nodes)
        {
            DrawNode(g, node);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        FlowChartNode? node = HitTest(e.Location);
        LastClickHitTreeExpander = node != null && IsTreeExpanderHit(node, e.Location);
        if (node != null)
        {
            NodeClick?.Invoke(this, node);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        FlowChartNode? node = HitTest(e.Location);
        LastClickHitTreeExpander = node != null && IsTreeExpanderHit(node, e.Location);
        if (node != null)
        {
            NodeDoubleClick?.Invoke(this, node);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = HitTest(e.Location) == null ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int notches = e.Delta / 120;
        if (notches == 0)
        {
            notches = e.Delta > 0 ? 1 : -1;
        }
        int step = -notches * 96;
        Point current = AutoScrollPosition;
        AutoScrollPosition = new Point(-current.X, Math.Max(0, -current.Y + step));
        Invalidate();
    }

    private FlowChartNode? HitTest(Point location)
    {
        PointF logical = new PointF(location.X - AutoScrollPosition.X, location.Y - AutoScrollPosition.Y);
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            FlowChartNode node = _nodes[i];
            if (node.Bounds.Contains(logical))
            {
                return node;
            }
        }

        return null;
    }

    private bool IsTreeExpanderHit(FlowChartNode node, Point location)
    {
        if (!IsTreeNode(node) || !IsExpandableTreeNode(node))
        {
            return false;
        }

        PointF logical = new PointF(location.X - AutoScrollPosition.X, location.Y - AutoScrollPosition.Y);
        TreeVisualInfo visual = ParseTreeVisualInfo(node.Text);
        float boxX = GetTreeExpanderX(node.Bounds, visual);
        return logical.X >= boxX - 4f && logical.X <= boxX + 16f &&
            logical.Y >= node.Bounds.Top && logical.Y <= node.Bounds.Bottom;
    }

    private void AdvanceAnimation()
    {
        if (!_animationEnabled || !Visible || _edges.Count == 0 || _nodes.Count == 0)
        {
            UpdateAnimationTimer();
            return;
        }
        int animatedCount = CountAnimatedEdges();
        if (animatedCount == 0)
        {
            animatedCount = _edges.Count;
        }
        _animationProgress += 0.065f;
        _animationDashOffset = (_animationDashOffset + 1.4f) % 84f;
        if (_animationProgress >= 1f)
        {
            _animationProgress = 0f;
            _animatedEdgeIndex = (_animatedEdgeIndex + 1) % Math.Max(1, animatedCount);
        }
        Invalidate();
    }

    private void UpdateAnimationTimer()
    {
        if (_animationTimer.Enabled)
        {
            _animationTimer.Stop();
        }
    }

    private void DrawNode(Graphics g, FlowChartNode node)
    {
        if (IsTreeNode(node))
        {
            DrawTreeNode(g, node);
            return;
        }

        bool hot = !string.IsNullOrWhiteSpace(_focusedFunctionName) &&
            FunctionNameEquals(node.FunctionName, _focusedFunctionName);
        RectangleF rect = node.Bounds;
        using GraphicsPath path = RoundedRect(rect, _highContrastNodes ? 9 : 8);
        (Color fillColor, Color borderColor, Color badgeColor, string badge) = PaletteForNode(node);
        if (_highContrastNodes)
        {
            fillColor = BoostHighContrastFill(fillColor);
            borderColor = _lightPalette ? ControlPaint.Dark(borderColor, 0.08f) : ControlPaint.Light(borderColor, 0.18f);
            badgeColor = _lightPalette ? ControlPaint.Dark(badgeColor, 0.06f) : ControlPaint.Light(badgeColor, 0.14f);
        }

        Color actualFill = hot ? Blend(fillColor, _accent, _lightPalette ? 0.18f : 0.30f) : fillColor;
        using var fill = new SolidBrush(actualFill);
        using var border = new Pen(hot ? _accent : borderColor, hot ? 2.8f : (_highContrastNodes ? 1.9f : (_lightPalette ? 1.35f : 1.2f)));
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var badgeBrush = new SolidBrush(hot ? _accent : badgeColor);
        g.FillRectangle(badgeBrush, rect.Left, rect.Top, _lightPalette ? 4 : 5, rect.Height);
        using var badgeTextBrush = new SolidBrush(_highContrastNodes ? ContrastTextColor(actualFill) : (_lightPalette ? borderColor : _muted));
        using var badgeFont = new Font(Font.FontFamily, _highContrastNodes ? 9.2f : 8f, FontStyle.Bold);
        g.DrawString(badge, badgeFont, badgeTextBrush, new RectangleF(rect.Left + 10, rect.Top + 4, rect.Width - 16, _highContrastNodes ? 15 : 12));
        using Font? highContrastFont = _highContrastNodes
            ? new Font(Font.FontFamily, Math.Max(Font.SizeInPoints, 11.8f), FontStyle.Bold)
            : null;
        Font bodyFont = highContrastFont ?? Font;
        using var textBrush = new SolidBrush(_highContrastNodes ? ContrastTextColor(actualFill) : _ink);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        RectangleF textRect = new RectangleF(rect.Left + 10, rect.Top + (_highContrastNodes ? 18 : 15), rect.Width - 20, rect.Height - (_highContrastNodes ? 22 : 18));
        g.DrawString(node.Text, bodyFont, textBrush, textRect, format);
    }

    private void DrawTreeNode(Graphics g, FlowChartNode node)
    {
        bool hot = !string.IsNullOrWhiteSpace(_focusedFunctionName) &&
            FunctionNameEquals(node.FunctionName, _focusedFunctionName);
        RectangleF rect = node.Bounds;
        TreeVisualInfo visual = ParseTreeVisualInfo(node.Text);
        bool current = IsTreeKind(node, "Current") ||
            (!string.IsNullOrWhiteSpace(_focusedFunctionName) &&
             FunctionNameEquals(node.FunctionName, _focusedFunctionName));
        bool hint = IsTreeKind(node, "Hint");
        bool expandable = IsExpandableTreeNode(node);
        bool weak = IsTreeKind(node, "Weak") ||
            visual.Depth >= 3 ||
            (!expandable && visual.Depth > 0);
        Color levelColor = TreeTextColor(node, visual.Depth, hot, false, expandable);
        bool collapsed = node.Kind.Contains("Collapsed", StringComparison.OrdinalIgnoreCase);
        Color currentColor = _lightPalette ? Color.FromArgb(18, 137, 72) : Color.FromArgb(74, 222, 128);
        Color textColor = TreeTextColor(node, visual.Depth, hot, current, expandable);

        (string titleText, string summaryText) = SplitTreeNodeText(visual.DisplayText);
        bool root = node.Kind.Equals("treeRoot", StringComparison.OrdinalIgnoreCase);
        float titleFontSize = TreeTitleFontSize(visual.Depth, expandable, root, current);
        FontStyle titleStyle = TreeTitleStyle(visual.Depth, expandable, root, current);
        using var treeFont = new Font(
            "Consolas",
            titleFontSize,
            titleStyle);
        using var summaryFont = new Font(
            "Microsoft YaHei UI",
            TreeSummaryFontSize(titleFontSize, visual.Depth),
            FontStyle.Italic);
        using var textBrush = new SolidBrush(textColor);
        using var titleFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = string.IsNullOrWhiteSpace(summaryText) ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var summaryFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        DrawKeilTreeGuides(g, rect, visual, current);

        float expanderX = GetTreeExpanderX(rect, visual);
        float iconX = expanderX;
        if (expandable)
        {
            float boxSize = Dpi(10f);
            DrawPlusMinusBox(g, expanderX, rect.Top + rect.Height / 2f - boxSize / 2f, collapsed, current ? currentColor : levelColor, boxSize);
            iconX += Dpi(18f);
        }

        bool hasSummary = !string.IsNullOrWhiteSpace(summaryText);
        float iconCenterY = hasSummary ? rect.Top + Math.Min(rect.Height / 2f, 17f) : rect.Top + rect.Height / 2f;
        if (node.Kind.Equals("treeRoot", StringComparison.OrdinalIgnoreCase))
        {
            DrawDocumentIcon(g, iconX + 1f, iconCenterY - 7f, textColor);
        }
        else
        {
            Color diamondColor = current
                ? currentColor
                : FunctionDiamondColor(hint, weak);
            float radius = current ? Dpi(6.4f) : (expandable ? Dpi(6.0f) : Dpi(4.8f));
            DrawFunctionDiamond(g, iconX + Dpi(7f), iconCenterY, diamondColor, radius);
        }

        float textLeft = iconX + Dpi(20f);
        float textWidth = Math.Max(20f, rect.Right - textLeft);
        if (!hasSummary)
        {
            RectangleF textRect = new RectangleF(textLeft, rect.Top, textWidth, rect.Height);
            g.DrawString(titleText, treeFont, textBrush, textRect, titleFormat);
            return;
        }

        RectangleF titleRect = new RectangleF(textLeft, rect.Top + 3f, textWidth, Math.Max(18f, rect.Height * 0.48f));
        RectangleF summaryRect = new RectangleF(textLeft, titleRect.Bottom - 1f, textWidth, Math.Max(14f, rect.Bottom - titleRect.Bottom - 2f));
        g.DrawString(titleText, treeFont, textBrush, titleRect, titleFormat);
        Color summaryColor = current
            ? (_lightPalette ? Color.FromArgb(36, 107, 72) : Color.FromArgb(150, 238, 178))
            : (weak ? (_lightPalette ? Color.FromArgb(138, 140, 136) : Color.FromArgb(96, 104, 114)) :
                (_lightPalette ? Color.FromArgb(104, 108, 104) : Color.FromArgb(132, 140, 150)));
        using var summaryBrush = new SolidBrush(summaryColor);
        g.DrawString(summaryText, summaryFont, summaryBrush, summaryRect, summaryFormat);
    }

    private readonly record struct TreeVisualInfo(string DisplayText, int Depth, bool HasConnector, bool IsLast, bool[] Continuations);

    private static (string Title, string Summary) SplitTreeNodeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ("", "");
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int split = normalized.IndexOf('\n');
        if (split < 0)
        {
            return (normalized.Trim(), "");
        }

        string title = normalized[..split].Trim();
        string summary = normalized[(split + 1)..].Trim();
        return (title, summary);
    }

    private float TreeTitleFontSize(int depth, bool expandable, bool root, bool current)
    {
        if (root || current)
        {
            return _treeFontSize;
        }

        float offset = Math.Clamp(depth, 0, 4) switch
        {
            0 => expandable ? 0.1f : -0.4f,
            1 => expandable ? -0.7f : -1.5f,
            2 => expandable ? -1.2f : -2.0f,
            3 => expandable ? -1.6f : -2.3f,
            _ => -2.6f
        };

        return Math.Max(9.6f, _treeFontSize + offset);
    }

    private static float TreeSummaryFontSize(float titleFontSize, int depth)
    {
        float offset = depth <= 0 ? 2.5f : 2.9f;
        return Math.Max(8.6f, titleFontSize - offset);
    }

    private static FontStyle TreeTitleStyle(int depth, bool expandable, bool root, bool current)
    {
        if (root || current)
        {
            return FontStyle.Bold;
        }

        if (expandable)
        {
            return FontStyle.Bold;
        }

        return depth > 0 ? FontStyle.Italic : FontStyle.Regular;
    }

    private static TreeVisualInfo ParseTreeVisualInfo(string text)
    {
        const string Vertical = "\u2502  ";
        const string Tee = "\u251C\u2500 ";
        const string Elbow = "\u2514\u2500 ";

        int index = 0;
        int depth = 0;
        bool hasConnector = false;
        bool isLast = false;
        List<bool> continuations = new();
        while (index + 2 < text.Length)
        {
            string chunk = text.Substring(index, 3);
            if (chunk == Vertical)
            {
                continuations.Add(true);
                depth++;
                index += 3;
                continue;
            }
            if (chunk == "   ")
            {
                if (index == 0 && index + 5 < text.Length)
                {
                    string nextChunk = text.Substring(index + 3, 3);
                    if (nextChunk == Tee || nextChunk == Elbow || nextChunk == Vertical)
                    {
                        index += 3;
                        continue;
                    }
                }
                continuations.Add(false);
                depth++;
                index += 3;
                continue;
            }
            if (chunk == Tee || chunk == Elbow)
            {
                hasConnector = true;
                isLast = chunk[0] == '\u2514';
                depth++;
                index += 3;
            }
            break;
        }

        string displayText = index < text.Length ? text.Substring(index).TrimStart() : text.TrimStart();
        return new TreeVisualInfo(displayText, Math.Max(0, depth), hasConnector, isLast, continuations.ToArray());
    }

    private float Dpi(float value)
    {
        return value * Math.Max(96, DeviceDpi) / 96f;
    }

    private float TreeIndent()
    {
        return Dpi(24f);
    }

    private float GetTreeExpanderX(RectangleF rect, TreeVisualInfo visual)
    {
        return rect.Left + Dpi(4f) + Math.Max(0, visual.Depth) * TreeIndent();
    }

    private void DrawKeilTreeGuides(Graphics g, RectangleF rect, TreeVisualInfo visual, bool current)
    {
        Color guideColor = current
            ? (_lightPalette ? Color.FromArgb(220, 90, 114, 104) : Color.FromArgb(230, 168, 190, 180))
            : (_lightPalette ? Color.FromArgb(205, 88, 96, 92) : Color.FromArgb(215, 132, 146, 152));
        using var guidePen = new Pen(guideColor, Dpi(2f))
        {
            DashStyle = DashStyle.Solid,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        float midY = rect.Top + rect.Height / 2f;
        for (int i = 0; i < visual.Continuations.Length; i++)
        {
            if (!visual.Continuations[i])
            {
                continue;
            }

            float x = rect.Left + Dpi(10f) + i * TreeIndent();
            g.DrawLine(guidePen, x, rect.Top, x, rect.Bottom);
        }

        if (!visual.HasConnector || visual.Depth <= 0)
        {
            return;
        }

        float connectorX = rect.Left + Dpi(10f) + (visual.Depth - 1) * TreeIndent();
        g.DrawLine(guidePen, connectorX, rect.Top, connectorX, visual.IsLast ? midY : rect.Bottom);
        g.DrawLine(guidePen, connectorX, midY, connectorX + Dpi(18f), midY);
    }

    private void DrawPlusMinusBox(Graphics g, float x, float y, bool collapsed, Color accent, float boxSize)
    {
        RectangleF box = new RectangleF(x, y, boxSize, boxSize);
        using var fill = new SolidBrush(Color.FromArgb(252, 252, 252));
        using var border = new Pen(accent, Dpi(1.25f));
        using var glyph = new Pen(ControlPaint.Dark(accent, 0.25f), Dpi(1.15f));
        g.FillRectangle(fill, box);
        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        g.DrawLine(glyph, box.Left + Dpi(2f), box.Top + box.Height / 2f, box.Right - Dpi(2f), box.Top + box.Height / 2f);
        if (collapsed)
        {
            g.DrawLine(glyph, box.Left + box.Width / 2f, box.Top + Dpi(2f), box.Left + box.Width / 2f, box.Bottom - Dpi(2f));
        }
    }

    private static void DrawDocumentIcon(Graphics g, float x, float y, Color accent)
    {
        RectangleF page = new RectangleF(x, y, 11f, 14f);
        using var fill = new SolidBrush(Color.FromArgb(248, 250, 252));
        using var border = new Pen(Color.FromArgb(75, 85, 99), 1f);
        using var linePen = new Pen(Color.FromArgb(148, 163, 184), 1f);
        g.FillRectangle(fill, page);
        g.DrawRectangle(border, page.X, page.Y, page.Width, page.Height);
        g.DrawLine(linePen, page.Left + 2f, page.Top + 4f, page.Right - 2f, page.Top + 4f);
        g.DrawLine(linePen, page.Left + 2f, page.Top + 7f, page.Right - 2f, page.Top + 7f);
        g.DrawLine(linePen, page.Left + 2f, page.Top + 10f, page.Right - 3f, page.Top + 10f);
    }

    private static void DrawFunctionDiamond(Graphics g, float centerX, float centerY, Color color, float radius)
    {
        PointF[] diamond =
        {
            new PointF(centerX, centerY - radius),
            new PointF(centerX + radius, centerY),
            new PointF(centerX, centerY + radius),
            new PointF(centerX - radius, centerY)
        };
        using var fill = new SolidBrush(color);
        using var border = new Pen(Color.FromArgb(88, 88, 88), 1f);
        g.FillPolygon(fill, diamond);
        g.DrawPolygon(border, diamond);
    }

    private Color FunctionDiamondColor(bool hint, bool weak)
    {
        if (_lightPalette)
        {
            return hint
                ? Color.FromArgb(230, 82, 150)
                : (weak ? Color.FromArgb(184, 92, 136) : Color.FromArgb(255, 70, 158));
        }

        return hint
            ? Color.FromArgb(236, 86, 164)
            : (weak ? Color.FromArgb(166, 78, 124) : Color.FromArgb(255, 77, 166));
    }

    private Color TreeTextColor(FlowChartNode node, int depth, bool hot, bool current, bool expandable)
    {
        if (current)
        {
            return _lightPalette ? Color.FromArgb(8, 138, 74) : Color.FromArgb(72, 232, 135);
        }
        if (hot)
        {
            return _lightPalette ? Color.FromArgb(8, 138, 74) : Color.FromArgb(72, 232, 135);
        }
        if (node.Kind.Equals("treeRoot", StringComparison.OrdinalIgnoreCase))
        {
            return _lightPalette ? Color.FromArgb(28, 31, 30) : Color.FromArgb(216, 222, 224);
        }
        if (expandable)
        {
            return Math.Clamp(depth, 0, 4) switch
            {
                0 => _lightPalette ? Color.FromArgb(20, 24, 22) : Color.FromArgb(230, 238, 238),
                1 => _lightPalette ? Color.FromArgb(46, 52, 48) : Color.FromArgb(202, 214, 216),
                2 => _lightPalette ? Color.FromArgb(70, 76, 72) : Color.FromArgb(176, 188, 192),
                _ => _lightPalette ? Color.FromArgb(100, 104, 100) : Color.FromArgb(142, 154, 160)
            };
        }
        if (IsTreeKind(node, "Hint"))
        {
            return _lightPalette ? Color.FromArgb(86, 92, 88) : Color.FromArgb(144, 154, 160);
        }
        if (IsTreeKind(node, "Weak"))
        {
            return _lightPalette ? Color.FromArgb(172, 174, 168) : Color.FromArgb(82, 90, 98);
        }

        return Math.Clamp(depth, 0, 4) switch
        {
            0 => _lightPalette ? Color.FromArgb(34, 37, 36) : Color.FromArgb(210, 216, 218),
            1 => _lightPalette ? Color.FromArgb(116, 122, 116) : Color.FromArgb(126, 136, 142),
            2 => _lightPalette ? Color.FromArgb(146, 150, 144) : Color.FromArgb(102, 112, 120),
            3 => _lightPalette ? Color.FromArgb(170, 172, 166) : Color.FromArgb(84, 94, 104),
            _ => _lightPalette ? Color.FromArgb(184, 186, 180) : Color.FromArgb(72, 82, 92)
        };
    }

    private static bool IsTreeNode(FlowChartNode node)
    {
        return node.Kind.StartsWith("tree", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpandableTreeNode(FlowChartNode node)
    {
        return node.Kind.EndsWith("Expanded", StringComparison.OrdinalIgnoreCase) ||
            node.Kind.EndsWith("Collapsed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTreeKind(FlowChartNode node, string kindPart)
    {
        if (!IsTreeNode(node) || string.IsNullOrWhiteSpace(kindPart))
        {
            return false;
        }

        string kind = node.Kind;
        return kind.Equals("tree" + kindPart, StringComparison.OrdinalIgnoreCase) ||
            kind.StartsWith("tree" + kindPart, StringComparison.OrdinalIgnoreCase) &&
            (kind.EndsWith("Expanded", StringComparison.OrdinalIgnoreCase) ||
             kind.EndsWith("Collapsed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool FunctionNameEquals(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void DrawColumnHeaders(Graphics g)
    {
        var columns = _nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Group))
            .GroupBy(node => (node.Group, X: (int)Math.Round(node.Bounds.Left / 10f) * 10))
            .OrderBy(group => group.Min(node => node.Bounds.Left))
            .ToList();
        if (columns.Count == 0)
        {
            return;
        }

        using var headerFont = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
        foreach (var group in columns)
        {
            float left = group.Min(node => node.Bounds.Left);
            float right = group.Max(node => node.Bounds.Right);
            RectangleF rect = new RectangleF(left, 4, Math.Max(170, right - left), 22);
            using var fill = new SolidBrush(Color.FromArgb(_lightPalette ? 42 : 54, _accent));
            using var border = new Pen(Color.FromArgb(_lightPalette ? 95 : 130, _accent), 1f);
            using GraphicsPath path = RoundedRect(rect, 7);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            using var brush = new SolidBrush(_lightPalette ? Color.FromArgb(32, 46, 66) : _ink);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(group.Key.Group, headerFont, brush, rect, format);
        }
    }

    private (Color Fill, Color Border, Color Badge, string Label) PaletteForNode(string kind)
    {
        if (_lightPalette)
        {
            return kind switch
            {
                "main" => (Color.FromArgb(231, 238, 248), Color.FromArgb(55, 98, 160), Color.FromArgb(55, 98, 160), "MAIN"),
                "disp" => (Color.FromArgb(248, 235, 243), Color.FromArgb(176, 82, 124), Color.FromArgb(176, 82, 124), "DISP"),
                "period10" => (Color.FromArgb(247, 242, 222), Color.FromArgb(156, 117, 24), Color.FromArgb(156, 117, 24), "10ms"),
                "entry" => (Color.FromArgb(232, 239, 249), Color.FromArgb(56, 105, 170), Color.FromArgb(56, 105, 170), "入口"),
                "business" => (Color.FromArgb(232, 243, 235), Color.FromArgb(47, 124, 86), Color.FromArgb(47, 124, 86), "业务"),
                "can" => (Color.FromArgb(229, 242, 246), Color.FromArgb(36, 119, 145), Color.FromArgb(36, 119, 145), "CAN"),
                "io" => (Color.FromArgb(248, 238, 229), Color.FromArgb(176, 99, 35), Color.FromArgb(176, 99, 35), "IO"),
                "storage" => (Color.FromArgb(240, 235, 247), Color.FromArgb(119, 86, 166), Color.FromArgb(119, 86, 166), "存储"),
                "timer" => (Color.FromArgb(247, 242, 222), Color.FromArgb(156, 117, 24), Color.FromArgb(156, 117, 24), "周期"),
                "driver" => (Color.FromArgb(236, 239, 243), Color.FromArgb(103, 116, 133), Color.FromArgb(103, 116, 133), "底层"),
                _ => (_surfaceAlt, _line, _muted, "函数")
            };
        }

        return kind switch
        {
            "main" => (Color.FromArgb(31, 48, 78), Color.FromArgb(96, 165, 250), Color.FromArgb(96, 165, 250), "MAIN"),
            "disp" => (Color.FromArgb(58, 38, 64), Color.FromArgb(244, 114, 182), Color.FromArgb(244, 114, 182), "DISP"),
            "period10" => (Color.FromArgb(56, 48, 28), Color.FromArgb(250, 204, 21), Color.FromArgb(250, 204, 21), "10ms"),
            "entry" => (Color.FromArgb(34, 45, 70), Color.FromArgb(96, 165, 250), Color.FromArgb(96, 165, 250), "入口"),
            "business" => (Color.FromArgb(40, 55, 42), Color.FromArgb(74, 222, 128), Color.FromArgb(74, 222, 128), "业务"),
            "can" => (Color.FromArgb(38, 48, 64), Color.FromArgb(56, 189, 248), Color.FromArgb(56, 189, 248), "CAN"),
            "io" => (Color.FromArgb(53, 42, 32), Color.FromArgb(251, 146, 60), Color.FromArgb(251, 146, 60), "IO"),
            "storage" => (Color.FromArgb(49, 40, 58), Color.FromArgb(192, 132, 252), Color.FromArgb(192, 132, 252), "存储"),
            "timer" => (Color.FromArgb(52, 50, 33), Color.FromArgb(250, 204, 21), Color.FromArgb(250, 204, 21), "周期"),
            "driver" => (Color.FromArgb(31, 36, 47), Color.FromArgb(100, 116, 139), Color.FromArgb(100, 116, 139), "底层"),
            _ => (_surfaceAlt, _line, _muted, "函数")
        };
    }

    private (Color Fill, Color Border, Color Badge, string Label) PaletteForNode(FlowChartNode node)
    {
        (Color fill, Color border, Color badge, string label) = PaletteForNode(node.Kind);
        if (node.HierarchyLevel < 0)
        {
            return (fill, border, badge, label);
        }

        (Color hierarchyFill, Color hierarchyBorder, string hierarchyLabel) = PaletteForHierarchyLevel(node.HierarchyLevel);
        return (
            Blend(fill, hierarchyFill, _lightPalette ? 0.70f : 0.76f),
            Blend(border, hierarchyBorder, _lightPalette ? 0.78f : 0.86f),
            hierarchyBorder,
            hierarchyLabel);
    }

    private (Color Fill, Color Border, string Label) PaletteForHierarchyLevel(int hierarchyLevel)
    {
        int normalized = hierarchyLevel switch
        {
            5 => 0,
            6 => 1,
            _ => Math.Clamp(hierarchyLevel, 0, 3)
        };

        if (_lightPalette)
        {
            return normalized switch
            {
                0 => (Color.FromArgb(238, 239, 236), Color.FromArgb(86, 90, 88), "入口"),
                1 => (Color.FromArgb(232, 233, 230), Color.FromArgb(112, 116, 112), "一级"),
                2 => (Color.FromArgb(226, 227, 224), Color.FromArgb(140, 142, 136), "二级"),
                _ => (Color.FromArgb(220, 221, 218), Color.FromArgb(166, 168, 160), "三级")
            };
        }

        return normalized switch
        {
            0 => (Color.FromArgb(32, 36, 38), Color.FromArgb(130, 138, 142), "入口"),
            1 => (Color.FromArgb(28, 32, 34), Color.FromArgb(106, 114, 120), "一级"),
            2 => (Color.FromArgb(24, 28, 30), Color.FromArgb(82, 90, 98), "二级"),
            _ => (Color.FromArgb(20, 24, 26), Color.FromArgb(66, 74, 82), "三级")
        };
    }

    private Color HierarchyAccentForLevel(int hierarchyLevel)
    {
        return PaletteForHierarchyLevel(hierarchyLevel).Border;
    }

    private static bool IsLightColor(Color color)
    {
        return color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 186;
    }

    private static Color Blend(Color a, Color b, float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        float keep = 1f - ratio;
        return Color.FromArgb(
            (int)(a.R * keep + b.R * ratio),
            (int)(a.G * keep + b.G * ratio),
            (int)(a.B * keep + b.B * ratio));
    }

    private Color BoostHighContrastFill(Color fill)
    {
        return _lightPalette
            ? Blend(fill, Color.White, 0.42f)
            : Blend(fill, Color.Black, 0.22f);
    }

    private static Color ContrastTextColor(Color background)
    {
        double luminance = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
        return luminance > 150
            ? Color.FromArgb(6, 12, 24)
            : Color.FromArgb(248, 250, 252);
    }

    private void DrawEdge(Graphics g, RectangleF from, RectangleF to, Pen pen, string label, bool hot)
    {
        using var path = new GraphicsPath();
        PointF[] points = GetEdgePoints(from, to);
        path.AddLines(points);
        g.DrawPath(pen, path);

        if (label.Length > 0)
        {
            using var brush = new SolidBrush(hot ? _accent : _muted);
            using var smallFont = new Font(Font.FontFamily, 8.2f, FontStyle.Regular);
            PointF mid = points[1];
            g.DrawString(label, smallFont, brush, new PointF(mid.X + 4, mid.Y - 15));
        }
    }

    private void DrawAnimationPulse(Graphics g)
    {
        if (!_animationEnabled || _edges.Count == 0)
        {
            return;
        }
        if (!TryGetAnimatedEdge(out FlowChartEdge edge, out FlowChartNode from, out FlowChartNode to))
        {
            return;
        }
        PointF[] points = GetEdgePoints(from.Bounds, to.Bounds);
        PointF pulse = PointOnPolyline(points, _animationProgress);
        Color pulseColor = HierarchyAccentForLevel(to.HierarchyLevel);
        using var glow = new SolidBrush(Color.FromArgb(95, pulseColor));
        using var core = new SolidBrush(pulseColor);
        g.FillEllipse(glow, pulse.X - 8, pulse.Y - 8, 16, 16);
        g.FillEllipse(core, pulse.X - 4, pulse.Y - 4, 8, 8);
    }

    private int CountAnimatedEdges()
    {
        int count = 0;
        foreach (FlowChartEdge edge in _edges)
        {
            if (IsHierarchyEdge(edge))
            {
                count++;
            }
        }
        return count;
    }

    private bool TryGetAnimatedEdge(out FlowChartEdge edge, out FlowChartNode from, out FlowChartNode to)
    {
        int hierarchyCount = CountAnimatedEdges();
        int wanted = hierarchyCount > 0 ? _animatedEdgeIndex % hierarchyCount : _animatedEdgeIndex % Math.Max(1, _edges.Count);
        int ordinal = 0;
        foreach (FlowChartEdge candidate in _edges)
        {
            if (hierarchyCount > 0 && !IsHierarchyEdge(candidate))
            {
                continue;
            }
            if (ordinal == wanted &&
                _nodeById.TryGetValue(candidate.FromId, out FlowChartNode? start) &&
                _nodeById.TryGetValue(candidate.ToId, out FlowChartNode? end))
            {
                edge = candidate;
                from = start;
                to = end;
                return true;
            }
            ordinal++;
        }

        edge = default!;
        from = default!;
        to = default!;
        return false;
    }

    private static bool IsHierarchyEdge(FlowChartEdge edge)
    {
        return !string.IsNullOrWhiteSpace(edge.Label) &&
            !edge.Label.Equals("\u8c03\u7528", StringComparison.OrdinalIgnoreCase);
    }

    private static PointF[] GetEdgePoints(RectangleF from, RectangleF to)
    {
        if (to.Left >= from.Right - 8)
        {
            PointF start = new PointF(from.Right, from.Top + from.Height / 2);
            PointF end = new PointF(to.Left, to.Top + to.Height / 2);
            float midX = start.X + (end.X - start.X) / 2;
            return new[]
            {
                start,
                new PointF(midX, start.Y),
                new PointF(midX, end.Y),
                end
            };
        }

        PointF verticalStart = new PointF(from.Left + from.Width / 2, from.Bottom);
        PointF verticalEnd = new PointF(to.Left + to.Width / 2, to.Top);
        float midY = verticalStart.Y + (verticalEnd.Y - verticalStart.Y) / 2;
        return new[]
        {
            verticalStart,
            new PointF(verticalStart.X, midY),
            new PointF(verticalEnd.X, midY),
            verticalEnd
        };
    }

    private static PointF PointOnPolyline(PointF[] points, float progress)
    {
        progress = Math.Clamp(progress, 0f, 1f);
        float total = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }
        float target = total * progress;
        float walked = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            PointF a = points[i - 1];
            PointF b = points[i];
            float segment = Distance(a, b);
            if (walked + segment >= target && segment > 0)
            {
                float local = (target - walked) / segment;
                return new PointF(a.X + (b.X - a.X) * local, a.Y + (b.Y - a.Y) * local);
            }
            walked += segment;
        }
        return points[^1];
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
