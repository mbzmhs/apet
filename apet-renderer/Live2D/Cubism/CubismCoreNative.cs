using System.Runtime.InteropServices;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>
/// Live2D Cubism Core 原生库(live2dcubismcore.dll)的动态绑定。
/// 按官方 Live2DCubismCore.h(Cubism 5)的 C API 声明,在运行时通过
/// NativeLibrary 解析函数指针——Core 4.x / 5.x 均可加载,缺失的可选函数自动降级。
///
/// 注意:Core 是 Live2D 官方私有二进制,不随本库分发。
/// 宿主需自行从 Live2D 官方 SDK(Cubism SDK for Native/Unity)获取
/// live2dcubismcore.dll(x64)放到程序目录或 PATH 中。
/// </summary>
public static unsafe class CubismCoreNative
{
    static IntPtr _module;
    static bool _loadAttempted;

    // ---- 函数指针 ----
    static delegate* unmanaged<uint> _csmGetVersion;
    static delegate* unmanaged<int> _csmGetLatestMocVersion;
    static delegate* unmanaged<IntPtr, uint, int> _csmGetMocVersion;
    static delegate* unmanaged<IntPtr, uint, int> _csmHasMocConsistency;
    static delegate* unmanaged<IntPtr, uint, IntPtr> _csmReviveMocInPlace;
    static delegate* unmanaged<IntPtr, uint> _csmGetSizeofModel;
    static delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr> _csmInitializeModelInPlace;
    static delegate* unmanaged<IntPtr, void> _csmUpdateModel;
    static delegate* unmanaged<IntPtr, void> _csmResetDrawableDynamicFlags;
    static delegate* unmanaged<IntPtr, CanvasInfo*, CanvasInfo*, float*, void> _csmReadCanvasInfo;

    static delegate* unmanaged<IntPtr, int> _csmGetParameterCount;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterIds;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterTypes;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterMinimumValues;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterMaximumValues;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterDefaultValues;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterValues;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterKeyCounts;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetParameterKeyValues;

    static delegate* unmanaged<IntPtr, int> _csmGetPartCount;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetPartIds;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetPartOpacities;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetPartParentPartIndices;

    static delegate* unmanaged<IntPtr, int> _csmGetDrawableCount;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableIds;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableConstantFlags;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableDynamicFlags;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableTextureIndices;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableDrawOrders;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableRenderOrders;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableOpacities;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableMaskCounts;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableMasks;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableVertexCounts;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableVertexPositions;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableVertexUvs;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableIndexCounts;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableIndices;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableMultiplyColors;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableScreenColors;
    static delegate* unmanaged<IntPtr, IntPtr> _csmGetDrawableParentPartIndices;

    [StructLayout(LayoutKind.Sequential)]
    struct CanvasInfo { public float X, Y; }

    /// <summary>Core 是否已加载。</summary>
    public static bool IsLoaded => _module != IntPtr.Zero;

    /// <summary>Core 版本号(如 0x05000000 = 5.0.0)。</summary>
    public static uint Version => _csmGetVersion != null ? _csmGetVersion() : 0;

    /// <summary>
    /// 加载 Core。dllPath 为空时依次尝试:程序目录、PATH、runtimes/win-x64/native、
    /// Core/dll/windows/x86_64(官方 SDK 目录结构)。重复调用无副作用。
    /// </summary>
    public static void Load(string dllPath = null)
    {
        if (_module != IntPtr.Zero) return;
        if (_loadAttempted) throw new InvalidOperationException(LoadErrorMessage);
        _loadAttempted = true;

        string name = "live2dcubismcore";
        IntPtr module = IntPtr.Zero;

        if (!string.IsNullOrEmpty(dllPath) && NativeLibrary.TryLoad(dllPath, out module))
            _module = module;
        else if (NativeLibrary.TryLoad(name, out module))
            _module = module;
        else
        {
            // 再试几个常见相对位置
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, name + ".dll"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", name + ".dll"),
                Path.Combine(AppContext.BaseDirectory, "Core", "dll", "windows", "x86_64", "Live2DCubismCore.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), name + ".dll"),
            };
            foreach (var c in candidates)
                if (File.Exists(c) && NativeLibrary.TryLoad(c, out module)) { _module = module; break; }
        }

        if (_module == IntPtr.Zero)
            throw new DllNotFoundException(LoadErrorMessage);

        _csmGetVersion = (delegate* unmanaged<uint>)Export("csmGetVersion");
        _csmGetLatestMocVersion = (delegate* unmanaged<int>)Export("csmGetLatestMocVersion");
        _csmGetMocVersion = (delegate* unmanaged<IntPtr, uint, int>)Export("csmGetMocVersion");
        _csmHasMocConsistency = (delegate* unmanaged<IntPtr, uint, int>)Export("csmHasMocConsistency");
        _csmReviveMocInPlace = (delegate* unmanaged<IntPtr, uint, IntPtr>)Export("csmReviveMocInPlace");
        _csmGetSizeofModel = (delegate* unmanaged<IntPtr, uint>)Export("csmGetSizeofModel");
        _csmInitializeModelInPlace = (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr>)Export("csmInitializeModelInPlace");
        _csmUpdateModel = (delegate* unmanaged<IntPtr, void>)Export("csmUpdateModel");
        _csmResetDrawableDynamicFlags = (delegate* unmanaged<IntPtr, void>)Export("csmResetDrawableDynamicFlags");
        _csmReadCanvasInfo = (delegate* unmanaged<IntPtr, CanvasInfo*, CanvasInfo*, float*, void>)Export("csmReadCanvasInfo");

        _csmGetParameterCount = (delegate* unmanaged<IntPtr, int>)Export("csmGetParameterCount");
        _csmGetParameterIds = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetParameterIds");
        _csmGetParameterTypes = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetParameterTypes");
        _csmGetParameterMinimumValues = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetParameterMinimumValues");
        _csmGetParameterMaximumValues = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetParameterMaximumValues");
        _csmGetParameterDefaultValues = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetParameterDefaultValues");
        _csmGetParameterValues = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetParameterValues");
        _csmGetParameterKeyCounts = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetParameterKeyCounts");
        _csmGetParameterKeyValues = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetParameterKeyValues");

        _csmGetPartCount = (delegate* unmanaged<IntPtr, int>)Export("csmGetPartCount");
        _csmGetPartIds = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetPartIds");
        _csmGetPartOpacities = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetPartOpacities");
        _csmGetPartParentPartIndices = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetPartParentPartIndices");

        _csmGetDrawableCount = (delegate* unmanaged<IntPtr, int>)Export("csmGetDrawableCount");
        _csmGetDrawableIds = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableIds");
        _csmGetDrawableConstantFlags = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableConstantFlags");
        _csmGetDrawableDynamicFlags = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableDynamicFlags");
        _csmGetDrawableTextureIndices = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableTextureIndices");
        _csmGetDrawableDrawOrders = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableDrawOrders");
        _csmGetDrawableRenderOrders = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableRenderOrders");
        _csmGetDrawableOpacities = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableOpacities");
        _csmGetDrawableMaskCounts = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableMaskCounts");
        _csmGetDrawableMasks = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableMasks");
        _csmGetDrawableVertexCounts = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableVertexCounts");
        _csmGetDrawableVertexPositions = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableVertexPositions");
        _csmGetDrawableVertexUvs = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableVertexUvs");
        _csmGetDrawableIndexCounts = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableIndexCounts");
        _csmGetDrawableIndices = (delegate* unmanaged<IntPtr, IntPtr>)Export("csmGetDrawableIndices");
        _csmGetDrawableMultiplyColors = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetDrawableMultiplyColors");
        _csmGetDrawableScreenColors = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetDrawableScreenColors");
        _csmGetDrawableParentPartIndices = (delegate* unmanaged<IntPtr, IntPtr>)TryExport("csmGetDrawableParentPartIndices");
    }

    const string LoadErrorMessage =
        "未找到 Live2D Cubism Core 原生库 (live2dcubismcore.dll, x64)。\n" +
        "Core 是 Live2D 官方私有二进制,不随本库分发:请从 Live2D 官方网站下载 " +
        "Cubism SDK for Native / Unity,把其中的 live2dcubismcore.dll 放到程序目录下。\n" +
        "详见 Live2DLib README 的「获取 Cubism Core」一节。";

    static IntPtr Export(string name)
    {
        if (!NativeLibrary.TryGetExport(_module, name, out var addr))
            throw new EntryPointNotFoundException($"Live2DCubismCore 缺少导出函数 {name}");
        return addr;
    }

    static IntPtr TryExport(string name)
        => NativeLibrary.TryGetExport(_module, name, out var addr) ? addr : IntPtr.Zero;

    // ---- 包装方法 ----

    public static int GetLatestMocVersion() => _csmGetLatestMocVersion();
    public static int GetMocVersion(IntPtr moc, uint size) => _csmGetMocVersion(moc, size);
    public static bool HasMocConsistency(IntPtr moc, uint size) => _csmHasMocConsistency(moc, size) != 0;
    public static IntPtr ReviveMocInPlace(IntPtr address, uint size) => _csmReviveMocInPlace(address, size);
    public static uint GetSizeofModel(IntPtr moc) => _csmGetSizeofModel(moc);
    public static IntPtr InitializeModelInPlace(IntPtr moc, IntPtr address, uint size)
        => _csmInitializeModelInPlace(moc, address, size);
    public static void UpdateModel(IntPtr model) => _csmUpdateModel(model);
    public static void ResetDrawableDynamicFlags(IntPtr model) => _csmResetDrawableDynamicFlags(model);

    public static void ReadCanvasInfo(IntPtr model, out (float W, float H) sizeInPixels,
        out (float X, float Y) originInPixels, out float pixelsPerUnit)
    {
        CanvasInfo size, origin;
        float ppu;
        _csmReadCanvasInfo(model, &size, &origin, &ppu);
        sizeInPixels = (size.X, size.Y);
        originInPixels = (origin.X, origin.Y);
        pixelsPerUnit = ppu;
    }

    public static int GetParameterCount(IntPtr model) => _csmGetParameterCount(model);
    public static IntPtr GetParameterIds(IntPtr model) => _csmGetParameterIds(model);
    public static IntPtr GetParameterTypes(IntPtr model)
        => _csmGetParameterTypes != null ? _csmGetParameterTypes(model) : IntPtr.Zero;
    public static IntPtr GetParameterMinimumValues(IntPtr model) => _csmGetParameterMinimumValues(model);
    public static IntPtr GetParameterMaximumValues(IntPtr model) => _csmGetParameterMaximumValues(model);
    public static IntPtr GetParameterDefaultValues(IntPtr model) => _csmGetParameterDefaultValues(model);
    public static IntPtr GetParameterValues(IntPtr model) => _csmGetParameterValues(model);
    public static IntPtr GetParameterKeyCounts(IntPtr model)
        => _csmGetParameterKeyCounts != null ? _csmGetParameterKeyCounts(model) : IntPtr.Zero;
    public static IntPtr GetParameterKeyValues(IntPtr model)
        => _csmGetParameterKeyValues != null ? _csmGetParameterKeyValues(model) : IntPtr.Zero;

    public static int GetPartCount(IntPtr model) => _csmGetPartCount(model);
    public static IntPtr GetPartIds(IntPtr model) => _csmGetPartIds(model);
    public static IntPtr GetPartOpacities(IntPtr model) => _csmGetPartOpacities(model);
    public static IntPtr GetPartParentPartIndices(IntPtr model)
        => _csmGetPartParentPartIndices != null ? _csmGetPartParentPartIndices(model) : IntPtr.Zero;

    public static int GetDrawableCount(IntPtr model) => _csmGetDrawableCount(model);
    public static IntPtr GetDrawableIds(IntPtr model) => _csmGetDrawableIds(model);
    public static IntPtr GetDrawableConstantFlags(IntPtr model) => _csmGetDrawableConstantFlags(model);
    public static IntPtr GetDrawableDynamicFlags(IntPtr model) => _csmGetDrawableDynamicFlags(model);
    public static IntPtr GetDrawableTextureIndices(IntPtr model) => _csmGetDrawableTextureIndices(model);
    public static IntPtr GetDrawableDrawOrders(IntPtr model) => _csmGetDrawableDrawOrders(model);
    public static IntPtr GetDrawableRenderOrders(IntPtr model) => _csmGetDrawableRenderOrders(model);
    public static IntPtr GetDrawableOpacities(IntPtr model) => _csmGetDrawableOpacities(model);
    public static IntPtr GetDrawableMaskCounts(IntPtr model) => _csmGetDrawableMaskCounts(model);
    public static IntPtr GetDrawableMasks(IntPtr model) => _csmGetDrawableMasks(model);
    public static IntPtr GetDrawableVertexCounts(IntPtr model) => _csmGetDrawableVertexCounts(model);
    public static IntPtr GetDrawableVertexPositions(IntPtr model) => _csmGetDrawableVertexPositions(model);
    public static IntPtr GetDrawableVertexUvs(IntPtr model) => _csmGetDrawableVertexUvs(model);
    public static IntPtr GetDrawableIndexCounts(IntPtr model) => _csmGetDrawableIndexCounts(model);
    public static IntPtr GetDrawableIndices(IntPtr model) => _csmGetDrawableIndices(model);
    public static IntPtr GetDrawableMultiplyColors(IntPtr model)
        => _csmGetDrawableMultiplyColors != null ? _csmGetDrawableMultiplyColors(model) : IntPtr.Zero;
    public static IntPtr GetDrawableScreenColors(IntPtr model)
        => _csmGetDrawableScreenColors != null ? _csmGetDrawableScreenColors(model) : IntPtr.Zero;
    public static IntPtr GetDrawableParentPartIndices(IntPtr model)
        => _csmGetDrawableParentPartIndices != null ? _csmGetDrawableParentPartIndices(model) : IntPtr.Zero;

    /// <summary>读取 Core 返回的 char** 字符串表。</summary>
    public static string[] ReadStringTable(IntPtr table, int count)
    {
        var result = new string[count];
        byte** ptrs = (byte**)table;
        for (int i = 0; i < count; i++)
            result[i] = Marshal.PtrToStringUTF8((IntPtr)ptrs[i]);
        return result;
    }

    // ---- 常量(与 Live2DCubismCore.h 一致) ----

    /// <summary>moc3 需要的内存对齐(字节)。</summary>
    public const int AlignofMoc = 64;
    /// <summary>model 实例需要的内存对齐(字节)。</summary>
    public const int AlignofModel = 16;

    // 常量 flag
    public const byte BlendAdditive = 1 << 0;
    public const byte BlendMultiplicative = 1 << 1;
    public const byte IsDoubleSided = 1 << 2;
    public const byte IsInvertedMask = 1 << 3;

    // 动态 flag
    public const byte IsVisible = 1 << 0;
    public const byte VertexPositionsDidChange = 1 << 5;

    // moc3 格式版本
    public const int MocVersion30 = 1;
    public const int MocVersion33 = 2;
    public const int MocVersion40 = 3;
    public const int MocVersion42 = 4;
    public const int MocVersion50 = 5;
}
