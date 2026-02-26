using System;
using System.Collections.Generic;
using Diligent;

namespace SomeEngine.Render.Graph;

public class RGResourcePool : IDisposable
{
    private readonly List<ITexture> _textures = [];
    private readonly List<IBuffer> _buffers = [];

    // Simple descriptor comparison for now. Ideally should hash or use something more robust.
    private static bool AreEqual(TextureDesc a, TextureDesc b)
    {
        return a.Width == b.Width
            && a.Height == b.Height
            && a.Format == b.Format
            && a.BindFlags == b.BindFlags
            && a.Type == b.Type
            && a.ArraySizeOrDepth == b.ArraySizeOrDepth
            && a.MipLevels == b.MipLevels;
    }

    private static bool AreEqual(BufferDesc a, BufferDesc b)
    {
        return a.Size == b.Size
            && a.BindFlags == b.BindFlags
            && a.Usage == b.Usage
            && a.Mode == b.Mode;
    }

    public ITexture? AcquireTexture(IRenderDevice device, TextureDesc desc)
    {
        for (int i = 0; i < _textures.Count; i++)
        {
            if (AreEqual(_textures[i].GetDesc(), desc))
            {
                var tex = _textures[i];
                _textures.RemoveAt(i);
                return tex;
            }
        }
        return device.CreateTexture(desc, null);
    }

    public void ReleaseTexture(ITexture texture)
    {
        _textures.Add(texture);
    }

    public IBuffer? AcquireBuffer(IRenderDevice device, BufferDesc desc)
    {
        for (int i = 0; i < _buffers.Count; i++)
        {
            if (AreEqual(_buffers[i].GetDesc(), desc))
            {
                var buf = _buffers[i];
                _buffers.RemoveAt(i);
                return buf;
            }
        }
        return device.CreateBuffer(desc, null);
    }

    public void ReleaseBuffer(IBuffer buffer)
    {
        _buffers.Add(buffer);
    }

    public void Dispose()
    {
        foreach (var tex in _textures)
            tex.Dispose();
        foreach (var buf in _buffers)
            buf.Dispose();
        _textures.Clear();
        _buffers.Clear();
    }
}
