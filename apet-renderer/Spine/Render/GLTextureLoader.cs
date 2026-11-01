using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace APet.Renderer.Spine;

/// <summary>
/// Spine Atlas 纹理加载器:把 PNG 页图上传为 OpenGL 纹理,
/// 并把纹理句柄记录到 AtlasPage.rendererObject 上。
/// </summary>
public class GLTextureLoader : APet.Renderer.Spine.Runtime.TextureLoader
{
    readonly List<int> _textures = new();

    public void Load(APet.Renderer.Spine.Runtime.AtlasPage page, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到图集页纹理文件: " + path);

        using var bmp = new Bitmap(path);
        int width = bmp.Width, height = bmp.Height;
        page.width = width;
        page.height = height;

        var data = new byte[width * height * 4];
        var rect = new Rectangle(0, 0, width, height);
        var bits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 顺序上传(首行=图集顶部):spine-csharp 的 Atlas 不自动做 FlipV,
            // UV 的 v 以图集顶部为 0,与 GL 默认上传方向一致
            for (int y = 0; y < height; y++)
            {
                IntPtr src = bits.Scan0 + y * bits.Stride;
                Marshal.Copy(src, data, y * width * 4, width * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data);

        _textures.Add(tex);
        page.rendererObject = tex;
    }

    public void Unload(object texture)
    {
        if (texture is int tex)
        {
            GL.DeleteTexture(tex);
            _textures.Remove(tex);
        }
    }

    public void DisposeAll()
    {
        foreach (var tex in _textures) GL.DeleteTexture(tex);
        _textures.Clear();
    }
}
