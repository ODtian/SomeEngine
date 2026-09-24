using SomeEngine.Core.Collections;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    internal sealed class BindingIndex
    {
        private readonly record struct Entry(
            int Hash,
            BindingLayoutHandle Layout,
            BindingResourceDesc[] Resources);

        private readonly FlatDictionary<BindingLayoutHandle, HashSet<BindingSetHandle>> _layouts = new();
        private readonly FlatDictionary<TextureViewHandle, HashSet<BindingSetHandle>> _textures = new();
        private readonly FlatDictionary<BufferViewHandle, HashSet<BindingSetHandle>> _buffers = new();
        private readonly FlatDictionary<BindingSetHandle, Entry> _sets = new();

        public void Add(
            BindingSetHandle handle,
            int hash,
            BindingLayoutHandle layout,
            BindingResourceDesc[] resources)
        {
            if (!handle.IsValid)
                throw new InvalidOperationException("RenderGraph binding index cannot track an invalid binding set.");

            _sets[handle] = new Entry(hash, layout, resources);
            AddRef(_layouts, layout, handle);
            for (int i = 0; i < resources.Length; i++)
            {
                var resource = resources[i];
                if (UsesTexture(resource))
                    AddRef(_textures, resource.TextureView, handle);
                else if (UsesBuffer(resource))
                    AddRef(_buffers, resource.BufferView, handle);
            }
        }

        public bool TryHash(BindingSetHandle handle, out int hash)
        {
            if (_sets.TryGetValue(handle, out var entry))
            {
                hash = entry.Hash;
                return true;
            }

            hash = 0;
            return false;
        }

        public BindingSetHandle[] Layout(BindingLayoutHandle layout)
            => CopyMatches(_layouts, layout);

        public BindingSetHandle[] Texture(TextureViewHandle view)
            => CopyMatches(_textures, view);

        public BindingSetHandle[] Buffer(BufferViewHandle view)
            => CopyMatches(_buffers, view);

        public void Remove(BindingSetHandle handle)
        {
            if (!_sets.TryGetValue(handle, out var entry))
                return;

            RemoveRef(_layouts, entry.Layout, handle);
            for (int i = 0; i < entry.Resources.Length; i++)
            {
                var resource = entry.Resources[i];
                if (UsesTexture(resource))
                    RemoveRef(_textures, resource.TextureView, handle);
                else if (UsesBuffer(resource))
                    RemoveRef(_buffers, resource.BufferView, handle);
            }

            _sets.Remove(handle);
        }

        public void Clear()
        {
            _layouts.Clear();
            _textures.Clear();
            _buffers.Clear();
            _sets.Clear();
        }

        private BindingSetHandle[] CopyMatches<TRef>(
            FlatDictionary<TRef, HashSet<BindingSetHandle>> refs,
            TRef key)
            where TRef : notnull
        {
            return refs.TryGetValue(key, out var handles)
                ? handles.ToArray()
                : Array.Empty<BindingSetHandle>();
        }

        private static void AddRef<TRef>(
            FlatDictionary<TRef, HashSet<BindingSetHandle>> refs,
            TRef key,
            BindingSetHandle handle)
            where TRef : notnull
        {
            if (!refs.TryGetValue(key, out var handles))
            {
                handles = [];
                refs.Add(key, handles);
            }

            handles.Add(handle);
        }

        private static void RemoveRef<TRef>(
            FlatDictionary<TRef, HashSet<BindingSetHandle>> refs,
            TRef key,
            BindingSetHandle handle)
            where TRef : notnull
        {
            if (!refs.TryGetValue(key, out var handles))
                return;

            handles.Remove(handle);
            if (handles.Count == 0)
                refs.Remove(key);
        }

        private static bool UsesTexture(BindingResourceDesc resource)
            => resource.ResourceType is BindingType.TextureRead or BindingType.TextureReadWrite
                && resource.TextureView.IsValid;

        private static bool UsesBuffer(BindingResourceDesc resource)
            => BindRules.HasBufferView(resource.ResourceType)
                && resource.BufferView.IsValid;
    }
}
