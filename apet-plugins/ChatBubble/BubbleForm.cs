using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace APet.Plugins.ChatBubble;

/// <summary>
/// 漫画式对话气泡窗体:无边框、置顶,圆角矩形 + 指向宠物的尾巴。
/// 默认在宠物正上方(尾巴朝下);屏幕放不下时移到宠物左侧(尾巴朝右)或右侧(尾巴朝左)。
/// 形状之外区域透明(Region),整体支持 Opacity 淡入/淡出,不抢焦点。
/// 视觉全部由 <see cref="BubbleStyle"/> 驱动(字体/颜色/边框/圆角/尾巴/最大宽/不透明度):
/// 样式变更经 <see cref="ApplyStyle"/> 热应用——按新字体与宽度重新测量排版,再沿用最近偏移重新锚定,
/// 圆角与尾巴长度都按实际气泡尺寸收缩,任何设置组合下形状都闭合合法、落点都不漂移。
/// 文本超出气泡体最大高时不再截断:保持最大尺寸、内容视口滚动——流式时自动钉底跟随最新内容,
/// 鼠标滚轮可上滚回读旧文(滚回底部后恢复跟随);悬停显示手型光标提示可交互(点击/滚动)。
/// </summary>
public sealed class BubbleForm : Form
{
    /// <summary>尾巴朝向:Down=气泡在宠物上方;Right=气泡在宠物左侧(尾朝右);Left=气泡在宠物右侧(尾朝左)。</summary>
    private enum TailDir { Down, Right, Left }

    // ───────── 布局常量(屏幕像素;可由用户设置的尺寸量在 BubbleStyle 里) ─────────
    private const int PadX = 14;          // 气泡体内左右留白
    private const int PadY = 11;          // 气泡体内上下留白
    private const int TailHalf = 9;       // 尾巴半宽(只开放长度设置)
    private const int MinWidth = 60;      // 气泡体最小宽
    private const int MaxBodyHeight = 170;// 气泡体最大高(不含尾巴,超高则视口滚动)
    private const int Gap = 6;            // 气泡与宠物之间的默认间隙

    private BubbleStyle _style = BubbleStyle.Default;
    private Font _font = CreateFont(BubbleStyle.Default);
    private string _text = "";
    private Size _bodySize;               // 当前文本对应的气泡体尺寸(Reposition 复用,免重测)
    private TailDir _tailDir = TailDir.Down;
    private int _tailAnchor = -1;         // 尾巴尖端相对窗口坐标(Down→x,Right/Left→y),已夹到体内
    private int _topOffset, _leftOffset, _rightOffset; // 最近一次布局用的偏移(样式热更新时复用)

    // ───────── 滚动状态(仅文本超高时生效) ─────────
    private float _totalTextH;            // 全文按当前内宽排版的总高(0 = 未超高,不滚动)
    private int _scrollTop;               // 距文本顶部的偏移(px);= MaxOffset() 即钉底
    private bool _autoPin = true;         // 钉底跟随最新内容;用户上滚后暂停,滚回底部恢复

    private readonly System.Windows.Forms.Timer _fadeTimer;
    private double _fadeStep;             // +1 淡入 / -1 淡出(Form.Opacity 为 double)
    private bool _fadingOut;

    public BubbleForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = _style.FillColor;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // 气泡可交互(点击开聊天窗口、滚轮滚动):悬停显示手型光标提示(Region 外不命中,只在气泡形状内出现)
        Cursor = Cursors.Hand;

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 24 };
        _fadeTimer.Tick += OnFadeTick;
    }

    /// <summary>气泡不激活、不抢占输入焦点(纯展示)。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>气泡体最大宽(样式设置)。</summary>
    private int MaxWidth => _style.MaxWidth;

    /// <summary>尾巴长度(样式设置;0 = 无尾巴)。</summary>
    private int TailLen => _style.TailLength;

    /// <summary>淡入淡出的目标不透明度(样式设置;不透明度 100% 即 1.0)。</summary>
    private double TargetOpacity => _style.Opacity;

    /// <summary>按样式创建字体;字体族不存在时回退系统无衬线,保证任何名称都能渲染出文字。</summary>
    private static Font CreateFont(BubbleStyle style)
    {
        try
        {
            return new Font(style.FontFamily, (float)style.FontSizePt, style.FontStyle, GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            return new Font(FontFamily.GenericSansSerif, (float)style.FontSizePt, style.FontStyle, GraphicsUnit.Point);
        }
    }

    // ───────── 对外接口 ─────────

    /// <summary>
    /// 应用新样式:重建字体与配色,已显示时按新字体/最大宽重新测量排版并沿用最近偏移重新锚定
    /// (滚动位置夹回有效范围),保证字号/宽度变化后气泡不错位、不溢出屏幕。样式相同为空操作。
    /// </summary>
    public void ApplyStyle(BubbleStyle style, Rectangle petBounds)
    {
        if (style.Equals(_style))
            return;

        _style = style;
        var oldFont = _font;
        _font = CreateFont(style);
        oldFont.Dispose();
        BackColor = style.FillColor;

        if (!Visible || _text.Length == 0)
        {
            Invalidate(); // 未显示:下次 Present 会按新样式测量;仅刷新配色
            return;
        }

        using var g = CreateGraphics();
        var (bodyW, bodyH, scroll) = MeasureBody(g);
        _bodySize = new Size(bodyW, bodyH);
        AnchorToPet(petBounds, bodyW, bodyH, _topOffset, _leftOffset, _rightOffset);
        FinishScrollState(g, scroll);
        Invalidate();
    }

    /// <summary>设置/更新文本并锚定到宠物身边,显示并淡入。text 为空则隐藏。</summary>
    public void Present(string text, Rectangle petBounds, int topOffset = 0, int leftOffset = 0, int rightOffset = 0)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || petBounds.IsEmpty)
        {
            if (Visible) StartHide();
            return;
        }

        // 新一轮文本(非在旧文本上追加):滚动状态复位,从开头钉底跟随
        bool append = _text.Length > 0 && trimmed.StartsWith(_text, StringComparison.Ordinal);
        if (!append)
        {
            _scrollTop = 0;
            _autoPin = true;
        }
        _text = trimmed;

        using var g = CreateGraphics();
        var (bodyW, bodyH, scroll) = MeasureBody(g);
        _bodySize = new Size(bodyW, bodyH); // Reposition 复用,免重测
        AnchorToPet(petBounds, bodyW, bodyH, topOffset, leftOffset, rightOffset);
        FinishScrollState(g, scroll);

        if (!Visible)
        {
            Opacity = 0f;
            Show();
        }
        FadeIn();
        Invalidate();
    }

    /// <summary>用当前已显示的文本重新锚定(不改文本与淡入淡出状态),供宠物拖动或偏移设置变更时跟随。</summary>
    public void Reposition(Rectangle petBounds, int topOffset, int leftOffset, int rightOffset)
    {
        if (!Visible || string.IsNullOrEmpty(_text) || _bodySize.IsEmpty || petBounds.IsEmpty)
            return;
        AnchorToPet(petBounds, _bodySize.Width, _bodySize.Height, topOffset, leftOffset, rightOffset);
    }

    /// <summary>开始淡出,完成后自动隐藏(新文本到来时调用 <see cref="FadeIn"/> 可打断)。</summary>
    public void StartHide()
    {
        if (!Visible) return;
        _fadingOut = true;
        _fadeStep = -1f;
        _fadeTimer.Start();
    }

    /// <summary>淡入(若正在淡出则打断)。已达目标不透明度时为空操作。</summary>
    public void FadeIn()
    {
        if (Opacity >= TargetOpacity && !_fadingOut) return;
        _fadingOut = false;
        _fadeStep = +1f;
        _fadeTimer.Start();
    }

    // ───────── 滚动 ─────────

    /// <summary>文本区内宽(扣除尾巴占位与留白),即换行/绘制约束宽。</summary>
    private int InnerWidth()
    {
        int leftInset = _tailDir == TailDir.Left ? TailLen : 0;
        int rightInset = _tailDir == TailDir.Right ? TailLen : 0;
        return Width - leftInset - rightInset - PadX * 2;
    }

    /// <summary>文本区可视高(扣除尾巴占位与留白)。</summary>
    private int ViewHeight()
    {
        int bottomInset = _tailDir == TailDir.Down ? TailLen : 0;
        return Height - bottomInset - PadY * 2;
    }

    /// <summary>最大可滚动偏移(全文总高超出可视高的部分);不超高为 0。</summary>
    private int MaxOffset() => Math.Max(0, (int)MathF.Ceiling(_totalTextH) - ViewHeight());

    /// <summary>滚轮:上滚回读旧文、下滚追新;滚回底部恢复钉底跟随。仅超高时可滚动。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int maxOff = MaxOffset();
        if (maxOff <= 0) return;
        _autoPin = false;
        _scrollTop = Math.Clamp(_scrollTop - e.Delta, 0, maxOff);
        if (_scrollTop >= maxOff)
            _autoPin = true; // 滚回底部:恢复跟随最新内容
        Invalidate();
    }

    // ───────── 布局与测量 ─────────

    /// <summary>
    /// 按当前文本/字体/最大宽测量气泡体尺寸,并写回 _totalTextH(供滚动计算)。
    /// 超高时固定到最大宽与最大高(滚动中宽度稳定,避免每帧重排抖动);未超高时按内容自适应。
    /// </summary>
    private (int BodyW, int BodyH, bool Scroll) MeasureBody(Graphics g)
    {
        float maxTextW = MaxWidth - PadX * 2f;
        var full = g.MeasureString(_text, _font, new SizeF(maxTextW, float.MaxValue));
        bool scroll = full.Height > MaxBodyHeight - PadY * 2f;

        // 最小高需要容纳一行文字;极大字号/高 DPI 下该下限可能超过上限,先取小避免 Clamp 参数倒置抛异常
        int minBodyH = Math.Min(2 * PadY + (int)Math.Ceiling(_font.GetHeight(g)), MaxBodyHeight);
        int bodyW = scroll ? MaxWidth : (int)Math.Clamp(Math.Ceiling(full.Width) + PadX * 2, MinWidth, MaxWidth);
        int bodyH = scroll ? MaxBodyHeight : Math.Clamp((int)Math.Ceiling(full.Height) + PadY * 2, minBodyH, MaxBodyHeight);
        _totalTextH = scroll ? (float)Math.Ceiling(full.Height) : 0f;
        return (bodyW, bodyH, scroll);
    }

    /// <summary>
    /// 锚定后修正滚动状态:侧置时尾巴占掉一侧内宽、换行变窄总高变大,按最终内宽重测;
    /// 再按钉底/用户阅读位置夹取滚动偏移(不超高则复位)。
    /// </summary>
    private void FinishScrollState(Graphics g, bool scroll)
    {
        if (!scroll)
        {
            _scrollTop = 0;
            return;
        }
        if (_tailDir != TailDir.Down)
            _totalTextH = (float)Math.Ceiling(g.MeasureString(_text, _font, new SizeF(InnerWidth(), float.MaxValue)).Height);
        int maxOff = MaxOffset();
        _scrollTop = _autoPin ? maxOff : Math.Clamp(_scrollTop, 0, maxOff);
    }

    /// <summary>
    /// 计算窗口尺寸与位置:默认宠物正上方;上方放不下(超出当前屏幕工作区)时移到左/右侧。
    /// 偏移语义:正值一律朝宠物方向移动(上=下移、左=右移、右=左移),用于修正模型预留空白。
    /// </summary>
    private void AnchorToPet(Rectangle pet, int bodyW, int bodyH, int topOffset, int leftOffset, int rightOffset)
    {
        _topOffset = topOffset;
        _leftOffset = leftOffset;
        _rightOffset = rightOffset;

        var screen = Screen.FromPoint(new Point(pet.Left + pet.Width / 2, pet.Top + pet.Height / 2));
        var wa = screen.WorkingArea;
        int cx = pet.Left + pet.Width / 2;
        int cy = pet.Top + pet.Height / 2;

        // 候选一:正上方(默认)。顶端对齐宠物顶边 - 气泡高 - 间隙,再按上偏移下移
        int topW = bodyW, topH = bodyH + TailLen;
        int topMaxX = Math.Max(wa.Left, wa.Right - topW); // 气泡比屏幕还宽时退化到左边界,避免 Clamp(min>max) 抛异常
        var topRect = new Rectangle(
            Math.Clamp(cx - topW / 2, wa.Left, topMaxX),
            pet.Top - topH - Gap + topOffset, topW, topH);

        // 候选二/三:左侧/右侧。顶端对齐宠物顶边 + 上偏移;内侧紧贴宠物边 ± 间隙,再按左/右偏移朝宠物方向移
        int sideW = bodyW + TailLen, sideH = bodyH;
        int sideY = pet.Top + topOffset;
        var leftRect = new Rectangle(pet.Left - sideW - Gap + leftOffset, sideY, sideW, sideH);
        var rightRect = new Rectangle(pet.Right + Gap - rightOffset, sideY, sideW, sideH);

        TailDir dir;
        Rectangle rect;
        if (Fits(topRect, wa))
        {
            dir = TailDir.Down;
            rect = topRect;
        }
        else if (Fits(leftRect, wa) && Fits(rightRect, wa))
        {
            // 两侧都放得下:选空余更多的一侧
            bool useLeft = pet.Left - wa.Left >= wa.Right - pet.Right;
            dir = useLeft ? TailDir.Right : TailDir.Left;
            rect = useLeft ? leftRect : rightRect;
        }
        else if (Fits(leftRect, wa))
        {
            dir = TailDir.Right;
            rect = leftRect;
        }
        else if (Fits(rightRect, wa))
        {
            dir = TailDir.Left;
            rect = rightRect;
        }
        else
        {
            // 都不够(极端窗口):仍按上方放置并夹回屏幕内,尽力可见
            dir = TailDir.Down;
            rect = new Rectangle(Math.Clamp(topRect.X, wa.Left, topMaxX), Math.Max(topRect.Y, wa.Top), topW, topH);
        }

        _tailDir = dir;
        if (Width != rect.Width || Height != rect.Height)
            Size = new Size(rect.Width, rect.Height);

        // 尾巴尖端指向宠物中心,夹到气泡体内(避开圆角)
        int r = EffectiveCorner(rect.Width, rect.Height, dir);
        int lo = r + TailHalf;
        int hi = (dir == TailDir.Down ? rect.Width : rect.Height) - r - TailHalf;
        _tailAnchor = hi > lo
            ? Math.Clamp(dir == TailDir.Down ? cx - rect.X : cy - rect.Y, lo, hi)
            : (dir == TailDir.Down ? rect.Width : rect.Height) / 2;

        Location = new Point(rect.X, rect.Y);
        ApplyShape();
    }

    private static bool Fits(Rectangle r, Rectangle wa) =>
        r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right && r.Bottom <= wa.Bottom;

    /// <summary>
    /// 当前样式圆角在给定窗体尺寸下的有效值:夹到气泡体宽/体高的一半以内,
    /// 避免用户设置的大圆角在小气泡上画出越界圆弧(0 = 直角)。
    /// </summary>
    private int EffectiveCorner(int w, int h, TailDir dir)
    {
        int bodyW = dir == TailDir.Down ? w : w - TailLen;
        int bodyH = dir == TailDir.Down ? h - TailLen : h;
        int limit = Math.Min(bodyW, Math.Max(bodyH, 0)) / 2;
        return Math.Clamp(Math.Min(_style.CornerRadius, limit), 0, _style.CornerRadius);
    }

    /// <summary>把窗口 Region 设为气泡形状(含尾巴),形状外透明且不可点击。</summary>
    private void ApplyShape()
    {
        using var path = BuildPath(Width, Height, _tailDir, _tailAnchor);
        Region = new Region(path);
        Invalidate();
    }

    /// <summary>构建气泡轮廓路径:圆角矩形 + 尾巴,单一闭合路径便于填充与描边;圆角为 0 时退化为直角。</summary>
    private GraphicsPath BuildPath(int w, int h, TailDir dir, int anchor)
    {
        var p = new GraphicsPath();
        float r = EffectiveCorner(w, h, dir);
        // 圆角过小时不画弧(AddArc 需要正的椭圆尺寸),由相邻直线自然衔接成直角
        void Arc(float x, float y, float start, float sweep)
        {
            if (r >= 0.5f)
                p.AddArc(x, y, 2 * r, 2 * r, start, sweep);
        }

        switch (dir)
        {
            case TailDir.Down: // 体在上,尾巴从底边朝下
            {
                float bb = h - TailLen; // 体底边 y
                Arc(0, 0, 180, 90);                                           // 左上角
                p.AddLine(r, 0, w - r, 0);                                    // 顶边
                Arc(w - 2 * r, 0, 270, 90);                                   // 右上角
                p.AddLine(w, r, w, bb - r);                                   // 右边
                Arc(w - 2 * r, bb - 2 * r, 0, 90);                            // 右下角
                p.AddLine(w - r, bb, anchor + TailHalf, bb);                  // 底边→尾巴右基
                p.AddLine(anchor + TailHalf, bb, anchor, h);                  // 尾巴
                p.AddLine(anchor, h, anchor - TailHalf, bb);
                p.AddLine(anchor - TailHalf, bb, r, bb);                      // 底边→左下角
                Arc(0, bb - 2 * r, 90, 90);                                   // 左下角
                p.AddLine(0, bb - r, 0, r);                                   // 左边
                break;
            }
            case TailDir.Right: // 体在左,尾巴从右边朝右(气泡位于宠物左侧)
            {
                float bl = w - TailLen; // 体右边 x
                Arc(0, 0, 180, 90);                                           // 左上角
                p.AddLine(r, 0, bl - r, 0);                                   // 顶边
                Arc(bl - 2 * r, 0, 270, 90);                                  // 体右上角
                p.AddLine(bl, r, bl, anchor - TailHalf);                      // 体右边→尾巴上基
                p.AddLine(bl, anchor - TailHalf, w, anchor);                  // 尾巴
                p.AddLine(w, anchor, bl, anchor + TailHalf);
                p.AddLine(bl, anchor + TailHalf, bl, h - r);                  // 体右边→右下角
                Arc(bl - 2 * r, h - 2 * r, 0, 90);                            // 体右下角
                p.AddLine(bl - r, h, r, h);                                   // 底边
                Arc(0, h - 2 * r, 90, 90);                                    // 左下角
                p.AddLine(0, h - r, 0, r);                                    // 左边
                break;
            }
            default: // TailDir.Left:体在右,尾巴从左边朝左(气泡位于宠物右侧)
            {
                float bl = TailLen; // 体左边 x
                Arc(bl, 0, 180, 90);                                          // 体左上角
                p.AddLine(bl + r, 0, w - r, 0);                               // 顶边
                Arc(w - 2 * r, 0, 270, 90);                                   // 右上角
                p.AddLine(w, r, w, h - r);                                    // 右边
                Arc(w - 2 * r, h - 2 * r, 0, 90);                             // 右下角
                p.AddLine(w - r, h, bl + r, h);                               // 底边
                Arc(bl, h - 2 * r, 90, 90);                                   // 体左下角
                p.AddLine(bl, h - r, bl, anchor + TailHalf);                  // 体左边→尾巴下基
                p.AddLine(bl, anchor + TailHalf, 0, anchor);                  // 尾巴
                p.AddLine(0, anchor, bl, anchor - TailHalf);
                p.AddLine(bl, anchor - TailHalf, bl, r);                      // 体左边→左上角
                break;
            }
        }
        p.CloseFigure();
        return p;
    }

    // ───────── 绘制 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var path = BuildPath(Width, Height, _tailDir, _tailAnchor);
        using (var brush = new SolidBrush(_style.FillColor))
            g.FillPath(brush, path);
        if (_style.BorderWidth >= 0.25f) // 0(或亚像素细)即不描边
        {
            using var pen = new Pen(_style.BorderColor, _style.BorderWidth);
            g.DrawPath(pen, path);
        }

        if (_text.Length > 0)
        {
            int leftInset = _tailDir == TailDir.Left ? TailLen : 0;
            int rightInset = _tailDir == TailDir.Right ? TailLen : 0;
            int bottomInset = _tailDir == TailDir.Down ? TailLen : 0;
            float x = leftInset + PadX;
            float w = Width - leftInset - rightInset - PadX * 2f;
            using var sf = new StringFormat(StringFormatFlags.LineLimit)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
            };
            using var brush = new SolidBrush(_style.TextColor);

            int maxOff = MaxOffset();
            if (maxOff > 0)
            {
                // 滚动模式:整段文本上移 _scrollTop,视口只露出其中一段(钉底时露出末尾)
                g.DrawString(_text, _font, brush, new RectangleF(x, PadY - _scrollTop, w, _totalTextH), sf);
            }
            else
            {
                g.DrawString(_text, _font, brush, new RectangleF(x, PadY, w, Height - bottomInset - PadY * 2f), sf);
            }
        }
    }

    // ───────── 淡入淡出 ─────────

    private void OnFadeTick(object? sender, EventArgs e)
    {
        double next = Opacity + _fadeStep * 0.08;
        if (_fadeStep > 0 && next >= TargetOpacity)
        {
            Opacity = TargetOpacity;
            _fadeTimer.Stop();
            return;
        }
        if (_fadeStep < 0 && next <= 0f)
        {
            Opacity = 0f;
            _fadeTimer.Stop();
            Hide();
            return;
        }
        Opacity = next;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
