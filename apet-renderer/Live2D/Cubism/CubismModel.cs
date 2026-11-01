using System.Runtime.InteropServices;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>
/// 一个已实例化的 Cubism 模型:持有 moc3 与 model 的非托管内存,
/// 封装参数 / 部件 / Drawable 三类对象供宿主精细控制。
/// 渲染管线在每帧调用 <see cref="Update"/> 后从 Drawables 读取变形后的顶点。
/// </summary>
public sealed unsafe class CubismModel : IDisposable
{
    IntPtr _mocBuffer, _modelBuffer;   // 原始分配地址(释放用)
    IntPtr _moc, _model;               // 对齐后的工作地址

    public CubismParameter[] Parameters { get; private set; }
    public CubismPart[] Parts { get; private set; }
    public CubismDrawable[] Drawables { get; private set; }

    readonly Dictionary<string, int> _paramIndex = new();
    readonly Dictionary<string, int> _partIndex = new();
    readonly Dictionary<string, int> _drawableIndex = new(StringComparer.Ordinal);

    /// <summary>画布信息(像素)。原点为模型坐标系原点在画布上的位置。</summary>
    public float CanvasWidth { get; private set; }
    public float CanvasHeight { get; private set; }
    public float CanvasOriginX { get; private set; }
    public float CanvasOriginY { get; private set; }
    public float PixelsPerUnit { get; private set; }

    /// <summary>moc3 文件格式版本(3 = Cubism 4.0,4 = 4.2,5 = 5.0)。</summary>
    public int MocVersion { get; private set; }

    /// <summary>是否已从 moc 复活并实例化成功。</summary>
    public bool IsValid => _model != IntPtr.Zero;

    /// <summary>从 moc3 字节复活模型。失败抛异常。</summary>
    public static CubismModel Load(byte[] mocBytes)
    {
        CubismCoreNative.Load();

        var model = new CubismModel();
        model.Initialize(mocBytes);
        return model;
    }

    void Initialize(byte[] mocBytes)
    {
        uint mocSize = (uint)mocBytes.Length;

        // 分配并对齐(moc 64 字节,model 16 字节)
        _mocBuffer = Marshal.AllocHGlobal((nint)(mocSize + CubismCoreNative.AlignofMoc));
        _moc = Align(_mocBuffer, CubismCoreNative.AlignofMoc);
        Marshal.Copy(mocBytes, 0, _moc, mocBytes.Length);

        MocVersion = CubismCoreNative.GetMocVersion(_moc, mocSize);
        if (!CubismCoreNative.HasMocConsistency(_moc, mocSize))
            throw new ApplicationException("moc3 数据一致性校验失败(文件损坏或版本不匹配)");
        if (MocVersion > CubismCoreNative.GetLatestMocVersion())
            throw new ApplicationException(
                $"moc3 版本 {MocVersion} 高于当前 Core 支持的最新版本 {CubismCoreNative.GetLatestMocVersion()},请更新 live2dcubismcore.dll");

        _moc = CubismCoreNative.ReviveMocInPlace(_moc, mocSize);
        if (_moc == IntPtr.Zero)
            throw new ApplicationException("moc3 复活失败:文件损坏,或 Core 版本过旧无法读取该 moc3 版本");

        uint modelSize = CubismCoreNative.GetSizeofModel(_moc);
        _modelBuffer = Marshal.AllocHGlobal((nint)(modelSize + CubismCoreNative.AlignofModel));
        _model = Align(_modelBuffer, CubismCoreNative.AlignofModel);
        _model = CubismCoreNative.InitializeModelInPlace(_moc, _model, modelSize);
        if (_model == IntPtr.Zero)
            throw new ApplicationException("模型实例化失败");

        ReadCanvas();
        ReadParameters();
        ReadParts();
        ReadDrawables();
        Update();   // 首次求值,填充动态数据
    }

    static IntPtr Align(IntPtr ptr, int alignment)
        => (IntPtr)(((long)ptr + alignment - 1) & ~(alignment - 1));

    void ReadCanvas()
    {
        CubismCoreNative.ReadCanvasInfo(_model, out var size, out var origin, out float ppu);
        CanvasWidth = size.W; CanvasHeight = size.H;
        CanvasOriginX = origin.X; CanvasOriginY = origin.Y;
        PixelsPerUnit = ppu;
    }

    void ReadParameters()
    {
        int count = CubismCoreNative.GetParameterCount(_model);
        var ids = CubismCoreNative.ReadStringTable(CubismCoreNative.GetParameterIds(_model), count);

        float* values = (float*)CubismCoreNative.GetParameterValues(_model);
        float* mins = (float*)CubismCoreNative.GetParameterMinimumValues(_model);
        float* maxs = (float*)CubismCoreNative.GetParameterMaximumValues(_model);
        float* defs = (float*)CubismCoreNative.GetParameterDefaultValues(_model);
        int* types = null;
        var typesPtr = CubismCoreNative.GetParameterTypes(_model);
        if (typesPtr != IntPtr.Zero) types = (int*)typesPtr;

        Parameters = new CubismParameter[count];
        for (int i = 0; i < count; i++)
        {
            int type = types != null ? types[i] : -1;
            Parameters[i] = new CubismParameter(ids[i], i, mins + i, maxs + i, defs + i, values + i, type);
            _paramIndex[ids[i]] = i;
        }
    }

    void ReadParts()
    {
        int count = CubismCoreNative.GetPartCount(_model);
        var ids = CubismCoreNative.ReadStringTable(CubismCoreNative.GetPartIds(_model), count);
        float* opacities = (float*)CubismCoreNative.GetPartOpacities(_model);
        int* parents = null;
        var parentPtr = CubismCoreNative.GetPartParentPartIndices(_model);
        if (parentPtr != IntPtr.Zero) parents = (int*)parentPtr;

        Parts = new CubismPart[count];
        for (int i = 0; i < count; i++)
        {
            Parts[i] = new CubismPart(ids[i], i, opacities + i, parents != null ? parents[i] : -1);
            _partIndex[ids[i]] = i;
        }
    }

    void ReadDrawables()
    {
        int count = CubismCoreNative.GetDrawableCount(_model);
        var ids = CubismCoreNative.ReadStringTable(CubismCoreNative.GetDrawableIds(_model), count);

        int* texIndices = (int*)CubismCoreNative.GetDrawableTextureIndices(_model);
        int* maskCounts = (int*)CubismCoreNative.GetDrawableMaskCounts(_model);
        int** masks = (int**)CubismCoreNative.GetDrawableMasks(_model);
        int* vertexCounts = (int*)CubismCoreNative.GetDrawableVertexCounts(_model);
        csmVector2** uvs = (csmVector2**)CubismCoreNative.GetDrawableVertexUvs(_model);
        int* indexCounts = (int*)CubismCoreNative.GetDrawableIndexCounts(_model);
        ushort** indices = (ushort**)CubismCoreNative.GetDrawableIndices(_model);
        byte* constFlags = (byte*)CubismCoreNative.GetDrawableConstantFlags(_model);
        csmVector4* mulColors = null, scrColors = null;
        var mulPtr = CubismCoreNative.GetDrawableMultiplyColors(_model);
        if (mulPtr != IntPtr.Zero) mulColors = (csmVector4*)mulPtr;
        var scrPtr = CubismCoreNative.GetDrawableScreenColors(_model);
        if (scrPtr != IntPtr.Zero) scrColors = (csmVector4*)scrPtr;

        Drawables = new CubismDrawable[count];
        for (int i = 0; i < count; i++)
        {
            var d = new CubismDrawable(ids[i], i);
            d.TextureIndex = texIndices[i];
            d.DoubleSided = (constFlags[i] & CubismCoreNative.IsDoubleSided) != 0;
            d.InvertedMask = (constFlags[i] & CubismCoreNative.IsInvertedMask) != 0;
            d.ConstFlags = constFlags[i];
            d.BlendMode = (constFlags[i] & CubismCoreNative.BlendAdditive) != 0 ? Live2DBlendMode.Additive
                : (constFlags[i] & CubismCoreNative.BlendMultiplicative) != 0 ? Live2DBlendMode.Multiplicative
                : Live2DBlendMode.Normal;
            if (mulColors != null)
            {
                d.MultiplyR = mulColors[i].X; d.MultiplyG = mulColors[i].Y; d.MultiplyB = mulColors[i].Z;
            }
            else { d.MultiplyR = d.MultiplyG = d.MultiplyB = 1f; }

            int vc = vertexCounts[i];
            d.VertexCount = vc;
            d.VertexUvs = new float[vc * 2];
            fixed (float* uvDst = d.VertexUvs)
            {
                // Core 的 UV v=0 在图集底部;纹理按"首行 = 图像顶部"上传(v=0 在顶部),故翻转 v
                csmVector2* uvSrc = uvs[i];
                for (int v = 0; v < vc; v++) { uvDst[v * 2] = uvSrc[v].X; uvDst[v * 2 + 1] = 1f - uvSrc[v].Y; }
            }

            int ic = indexCounts[i];
            d.Indices = new ushort[ic];
            fixed (ushort* idxDst = d.Indices)
                Buffer.MemoryCopy(indices[i], idxDst, (long)ic * sizeof(ushort), (long)ic * sizeof(ushort));

            int mc = maskCounts[i];
            d.Masks = new int[mc];
            for (int m = 0; m < mc; m++) d.Masks[m] = masks[i][m];

            d.VertexPositions = new float[vc * 2];
            Drawables[i] = d;
            _drawableIndex[ids[i]] = i;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct csmVector2 { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct csmVector4 { public float X, Y, Z, W; }

    /// <summary>
    /// 求值一帧:调用 Core 更新模型,并把动态数据(顶点、透明度、渲染顺序、可见性、染色)
    /// 拷贝到托管数组供渲染与命中检测使用。调用前应先写入参数/部件值。
    /// 顶点坐标统一换算到像素空间(乘 PixelsPerUnit),量级与 Spine 等常见骨架一致,
    /// 便于宿主用同一套视图/交互代码。
    /// </summary>
    public void Update()
    {
        if (_model == IntPtr.Zero) return;
        CubismCoreNative.UpdateModel(_model);
        float ppu = PixelsPerUnit > 0 ? PixelsPerUnit : 1f;

        csmVector2** positions = (csmVector2**)CubismCoreNative.GetDrawableVertexPositions(_model);
        float* opacities = (float*)CubismCoreNative.GetDrawableOpacities(_model);
        int* renderOrders = (int*)CubismCoreNative.GetDrawableRenderOrders(_model);
        byte* dynFlags = (byte*)CubismCoreNative.GetDrawableDynamicFlags(_model);
        csmVector4* mulColors = null, scrColors = null;
        var mulPtr = CubismCoreNative.GetDrawableMultiplyColors(_model);
        if (mulPtr != IntPtr.Zero) mulColors = (csmVector4*)mulPtr;
        var scrPtr = CubismCoreNative.GetDrawableScreenColors(_model);
        if (scrPtr != IntPtr.Zero) scrColors = (csmVector4*)scrPtr;

        for (int i = 0; i < Drawables.Length; i++)
        {
            var d = Drawables[i];
            int vc = d.VertexCount;
            fixed (float* posDst = d.VertexPositions)
            {
                csmVector2* posSrc = positions[i];
                for (int v = 0; v < vc; v++)
                {
                    posDst[v * 2] = posSrc[v].X * ppu;
                    posDst[v * 2 + 1] = posSrc[v].Y * ppu;
                }
            }
            d.Opacity = opacities[i];
            d.RenderOrder = renderOrders[i];
            d.Visible = (dynFlags[i] & CubismCoreNative.IsVisible) != 0 && opacities[i] > 0.001f;
            if (mulColors != null)
            {
                d.MultiplyR = mulColors[i].X; d.MultiplyG = mulColors[i].Y; d.MultiplyB = mulColors[i].Z;
            }
            if (scrColors != null)
            {
                // 现有字段复用:Screen 色暂存于 Multiply* 之上的独立字段
                d.ScreenR = scrColors[i].X; d.ScreenG = scrColors[i].Y; d.ScreenB = scrColors[i].Z;
            }
        }
    }

    // ---- 索引查询 ----

    public int FindParameterIndex(string id) => _paramIndex.TryGetValue(id, out int i) ? i : -1;
    public int FindPartIndex(string id) => _partIndex.TryGetValue(id, out int i) ? i : -1;
    public int FindDrawableIndex(string id) => _drawableIndex.TryGetValue(id, out int i) ? i : -1;
    public CubismParameter GetParameter(string id) => FindParameterIndex(id) >= 0 ? Parameters[FindParameterIndex(id)] : null;
    public CubismPart GetPart(string id) => FindPartIndex(id) >= 0 ? Parts[FindPartIndex(id)] : null;

    public void Dispose()
    {
        if (_mocBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_mocBuffer);
        if (_modelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_modelBuffer);
        _mocBuffer = _modelBuffer = _moc = _model = IntPtr.Zero;
        Parameters = null; Parts = null; Drawables = null;
        _paramIndex.Clear(); _partIndex.Clear(); _drawableIndex.Clear();
    }
}
