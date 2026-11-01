using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace APet.Renderer.Vrm;

/// <summary>
/// glTF/GLB 容器的底层访问:解包 GLB 的 JSON/BIN 两个 chunk,提供 accessor/bufferView 的
/// 原始读取与图片解码。VRM 扩展(VRM/VRMC_vrm/VRMC_springBone)以原始 JsonElement 暴露,
/// 由 VrmLoader 按规范自行解析——不依赖任何第三方 glTF 库。
/// </summary>
public sealed class GltfSource : IDisposable
{
    public JsonDocument Json { get; }
    private readonly byte[] _bin;

    private GltfSource(JsonDocument json, byte[] bin)
    {
        Json = json;
        _bin = bin;
    }

    public void Dispose() => Json.Dispose();

    public JsonElement Root => Json.RootElement;

    /// <summary>解析 .vrm / .glb 文件(GLB 容器;纯 JSON 的 .gltf 不支持——VRM 一律是 glb)。</summary>
    public static GltfSource Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || data[0] != (byte)'g' || data[1] != (byte)'l' || data[2] != (byte)'T' || data[3] != (byte)'F')
            throw new InvalidDataException("不是有效的 GLB 文件(缺少 glTF magic): " + path);
        uint total = BitConverter.ToUInt32(data, 8);
        int pos = 12;
        JsonDocument json = null;
        byte[] bin = Array.Empty<byte>();
        while (pos + 8 <= data.Length)
        {
            uint len = BitConverter.ToUInt32(data, pos);
            uint type = BitConverter.ToUInt32(data, pos + 4);
            var body = data.AsSpan(pos + 8, checked((int)Math.Min(len, (uint)(data.Length - pos - 8))));
            if (type == 0x4E4F534A) // 'JSON'
                json = JsonDocument.Parse(body.ToArray());
            else if (type == 0x004E4942) // 'BIN\0'
                bin = body.ToArray();
            pos += 8 + (int)len;
            if (pos >= total) break;
        }
        if (json is null)
            throw new InvalidDataException("GLB 中缺少 JSON chunk: " + path);
        return new GltfSource(json, bin);
    }

    // ───────── bufferView / accessor 读取 ─────────

    public (int offset, int length, int stride) BufferView(int index)
    {
        var bv = Root.GetProperty("bufferViews")[index];
        int offset = bv.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        int length = bv.GetProperty("byteLength").GetInt32();
        int stride = bv.TryGetProperty("byteStride", out var s) ? s.GetInt32() : 0;
        return (offset, length, stride);
    }

    private static readonly Dictionary<string, (int components, int bytes)> ComponentInfo = new()
    {
        ["SCALAR"] = (1, 4), ["VEC2"] = (2, 4), ["VEC3"] = (3, 4), ["VEC4"] = (4, 4), ["MAT4"] = (16, 4),
    };

    private static readonly Dictionary<int, (int size, Type type)> ComponentType = new()
    {
        [5120] = (1, typeof(sbyte)), [5121] = (1, typeof(byte)), [5122] = (2, typeof(short)),
        [5123] = (2, typeof(ushort)), [5125] = (4, typeof(uint)), [5126] = (4, typeof(float)),
    };

    /// <summary>把 accessor 内容读为 float 数组(整数组件类型自动转 float)。</summary>
    public float[] ReadFloats(int accessorIndex)
    {
        var acc = Root.GetProperty("accessors")[accessorIndex];
        var (components, _) = ComponentInfo[acc.GetProperty("type").GetString()!];
        int compType = acc.GetProperty("componentType").GetInt32();
        var (compSize, _) = ComponentType[compType];
        int count = acc.GetProperty("count").GetInt32();
        var (viewOff, _, stride) = BufferView(acc.GetProperty("bufferView").GetInt32());
        int byteOffset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        int elemSize = components * compSize;
        int step = stride > 0 ? stride : elemSize;

        var result = new float[count * components];
        for (var i = 0; i < count; i++)
        {
            var src = viewOff + byteOffset + i * step;
            for (var c = 0; c < components; c++)
            {
                int at = src + c * compSize;
                result[i * components + c] = compType switch
                {
                    5120 => (sbyte)_bin[at],
                    5121 => _bin[at],
                    5122 => BitConverter.ToInt16(_bin, at),
                    5123 => BitConverter.ToUInt16(_bin, at),
                    5125 => BitConverter.ToUInt32(_bin, at),
                    _ => BitConverter.ToSingle(_bin, at),
                };
            }
        }
        return result;
    }

    /// <summary>把 accessor 内容读为 ushort 索引(componentType 必须 5123/5125/5121)。</summary>
    public ushort[] ReadIndices(int accessorIndex)
    {
        var acc = Root.GetProperty("accessors")[accessorIndex];
        int compType = acc.GetProperty("componentType").GetInt32();
        int count = acc.GetProperty("count").GetInt32();
        var (viewOff, _, stride) = BufferView(acc.GetProperty("bufferView").GetInt32());
        int byteOffset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            int at = viewOff + byteOffset + i * (stride > 0 ? stride : ComponentType[compType].size);
            result[i] = compType switch
            {
                5121 => _bin[at],
                5123 => BitConverter.ToUInt16(_bin, at),
                5125 => (ushort)BitConverter.ToUInt32(_bin, at),
                _ => throw new InvalidDataException("不支持的索引组件类型: " + compType),
            };
        }
        return result;
    }

    /// <summary>MAT4 accessor(float)读为一组行主序展开的矩阵(OpenTK 按列主序构造)。</summary>
    public float[] ReadMatrices(int accessorIndex) => ReadFloats(accessorIndex);

    // ───────── 图片解码(png/jpeg → BGRA 像素,供 GL 上传) ─────────

    public (byte[] bgra, int width, int height) DecodeImage(int imageIndex)
    {
        var img = Root.GetProperty("images")[imageIndex];
        if (!img.TryGetProperty("bufferView", out var bvIdx))
            throw new InvalidDataException("仅支持内嵌图片的 VRM(data uri 外部引用不支持)");
        var (off, len, _) = BufferView(bvIdx.GetInt32());
        using var ms = new MemoryStream(_bin, off, len, writable: false);
        using var bmp = new Bitmap(ms);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bmp.Width * bmp.Height * 4];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return (pixels, bmp.Width, bmp.Height); // 32bppArgb 内存序即 BGRA(小端)
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
