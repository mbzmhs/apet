namespace APet.Renderer.Live2D.Cubism;

/// <summary>Live2D 参数:宿主可精细读写(直接作用于 Core 的参数缓冲区,下一帧生效)。</summary>
public sealed unsafe class CubismParameter
{
    readonly float* _value;

    public string Id { get; }
    public int Index { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public float Default { get; }
    /// <summary>0 = 普通参数,1 = 混合形状参数(仅 Core 5.x 可用,旧 Core 为 -1)。</summary>
    public int Type { get; }

    internal CubismParameter(string id, int index, float* min, float* max, float* def, float* value, int type)
    {
        Id = id; Index = index;
        Minimum = *min; Maximum = *max; Default = *def;
        _value = value; Type = type;
    }

    /// <summary>当前值(直接读写模型缓冲区,csmUpdateModel 前写入生效)。</summary>
    public float Value
    {
        get => _value != null ? *_value : 0f;
        set { if (_value != null) *_value = value; }
    }
}

/// <summary>Live2D 部件(Part):透明度可直接控制(服装显隐、整体结构等)。</summary>
public sealed unsafe class CubismPart
{
    readonly float* _opacity;

    public string Id { get; }
    public int Index { get; }
    /// <summary>父部件下标(-1 = 无)。</summary>
    public int ParentIndex { get; }

    internal CubismPart(string id, int index, float* opacity, int parentIndex)
    {
        Id = id; Index = index; _opacity = opacity; ParentIndex = parentIndex;
    }

    /// <summary>部件不透明度 0..1(直接读写模型缓冲区)。</summary>
    public float Opacity
    {
        get => _opacity != null ? *_opacity : 0f;
        set { if (_opacity != null) *_opacity = Math.Clamp(value, 0f, 1f); }
    }
}

/// <summary>Live2D 可绘制体(Drawable):一个网格 + 纹理区域,渲染器的最小绘制单元。</summary>
public sealed unsafe class CubismDrawable
{
    public string Id { get; }
    public int Index { get; }

    // ---- 静态数据(加载时读取一次) ----
    public int TextureIndex { get; internal set; }
    /// <summary>三角形索引(ushort,长度 = 三角数 * 3)。</summary>
    public ushort[] Indices { get; internal set; }
    /// <summary>纹理 UV(x,y 交替,v=0 在图集顶部)。</summary>
    public float[] VertexUvs { get; internal set; }
    /// <summary>渲染遮罩列表(其他 Drawable 的下标)。</summary>
    public int[] Masks { get; internal set; }
    /// <summary>是否双面渲染(不剔除背面)。</summary>
    public bool DoubleSided { get; internal set; }
    /// <summary>遮罩反转:命中遮罩处不透明、未命中处透明。</summary>
    public bool InvertedMask { get; internal set; }
    /// <summary>常量 flag 原始字节(诊断用,核对位定义)。</summary>
    public byte ConstFlags { get; internal set; }
    /// <summary>染色乘色(1,1,1 = 原色)。</summary>
    public float MultiplyR, MultiplyG, MultiplyB;
    /// <summary>染色滤色(Screen,0,0,0 = 无效果)。</summary>
    public float ScreenR, ScreenG, ScreenB;
    /// <summary>混合模式(来自常量 flag:Additive / Multiplicative / Normal)。</summary>
    public Live2DBlendMode BlendMode { get; internal set; } = Live2DBlendMode.Normal;

    // ---- 动态数据(每帧 Update 时刷新) ----
    /// <summary>顶点坐标(x,y 交替,模型坐标系,y 向上,随动画变形)。</summary>
    public float[] VertexPositions { get; internal set; }
    public int VertexCount { get; internal set; }
    public float Opacity { get; internal set; }
    /// <summary>渲染顺序(越大越靠前)。</summary>
    public int RenderOrder { get; internal set; }
    /// <summary>当前帧是否可见。</summary>
    public bool Visible { get; internal set; } = true;

    internal CubismDrawable(string id, int index) { Id = id; Index = index; }
}
