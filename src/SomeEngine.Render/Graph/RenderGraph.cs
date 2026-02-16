using System;
using System.Collections.Generic;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public class RenderGraph : IDisposable
{
    private readonly List<RenderPass> _passes = new();
    private readonly List<RGResource> _resources = new();
    private readonly Dictionary<string, RGResourceHandle> _resourceMap = new();

    // Pass metadata
    private class PassMetadata
    {
        public bool IsActive = true;
        public List<(RGResourceHandle Handle, ResourceState State)> Reads = new();
        public List<(RGResourceHandle Handle, ResourceState State)> Writes = new();
    }
    private readonly Dictionary<RenderPass, PassMetadata> _passMetadata = new();

    // Dependency tracking (simplified for now)
    private readonly HashSet<RGResourceHandle> _compiledResources = new();

    public void AddPass(RenderPass pass)
    {
        _passes.Add(pass);
        _passMetadata[pass] = new PassMetadata();
    }

    public RGResourceHandle CreateTexture(string name, TextureDesc desc)
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var tex = new RGTexture(name, desc) { Handle = handle };
        _resources.Add(tex);
        _resourceMap[name] = handle;
        return handle;
    }

    public RGResourceHandle ImportTexture(string name, ITexture texture, ResourceState initialState = ResourceState.Undefined, ITextureView? view = null)
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var desc = texture.GetDesc();
        var tex = new RGTexture(name, desc)
        {
            Handle = handle,
            InternalTexture = texture,
            InternalView = view,
            IsImported = true,
            InitialState = initialState,
            CurrentState = initialState
        };
        _resources.Add(tex);
        _resourceMap[name] = handle;
        return handle;
    }
    
    public RGResourceHandle GetResourceHandle(string name)
    {
        if (_resourceMap.TryGetValue(name, out var handle))
            return handle;
        return RGResourceHandle.Invalid;
    }

    public void Compile()
    {
        // 1. Reset
        // 2. Setup passes
        foreach (var pass in _passes)
        {
            var builder = new RenderGraphBuilder(this, pass);
            pass.Setup(builder);
        }
        
        // 3. Resolve dependencies (Topological sort or just verify)
        // 4. Allocate resources (if not imported)
        // For this simple version, we allocate immediately during Execute or pre-allocate here.
        // Let's allocation here logic if needed, but for now we do it lazily or just ensure created.
    }

    public void Execute(RenderContext context)
    {
        RenderGraphContext rgContext = new RenderGraphContext(this, context);
        
        // Create/Ensure physical resources exist
        foreach (var res in _resources)
        {
            if (res is RGTexture tex && tex.InternalTexture == null && !tex.IsImported)
            {
                // Create actual texture
                tex.Desc.Name = tex.Name;
                tex.InternalTexture = context.Device?.CreateTexture(tex.Desc, null);
                tex.CurrentState = ResourceState.Undefined; // Newly created textures are undefined/common usually
            }
        }

        foreach (var pass in _passes)
        {
            if (!_passMetadata.TryGetValue(pass, out var meta) || !meta.IsActive)
                continue;

            // Insert Barriers
            // Note: Currently we track states but don't issue explicit barriers due to API binding uncertainty.
            // We rely on ResourceStateTransitionMode.Transition in passes, or future implementation of TransitionResourceStates.
            /*
            var barriers = new List<StateTransitionDesc>();

            // Handle Reads
            foreach (var (handle, reqState) in meta.Reads)
            {
                var res = _resources[handle.Id];
                if ((res.CurrentState & reqState) != reqState) 
                {
                     if (res.CurrentState != reqState) 
                     {
                         if(res is RGTexture tex && tex.InternalTexture != null)
                         {
                             barriers.Add(new StateTransitionDesc 
                             {
                                 Resource = tex.InternalTexture,
                                 OldState = res.CurrentState,
                                 NewState = reqState,
                                 Flags = StateTransitionFlags.None
                             });
                         }
                         res.CurrentState = reqState;
                     }
                }
            }

            // Handle Writes
            foreach (var (handle, reqState) in meta.Writes)
            {
                var res = _resources[handle.Id];
                 if (res.CurrentState != reqState) 
                 {
                     if(res is RGTexture tex && tex.InternalTexture != null)
                     {
                         barriers.Add(new StateTransitionDesc 
                         {
                             Resource = tex.InternalTexture,
                             OldState = res.CurrentState,
                             NewState = reqState,
                             Flags = StateTransitionFlags.None
                         });
                     }
                     res.CurrentState = reqState;
                 }
            }

            if (barriers.Count > 0)
            {
               // context.ImmediateContext?.TransitionResourceStates((uint)barriers.Count, barriers.ToArray());
            }
            */
            
            // Just update state for tracking for now, assuming Pass handles transition
            foreach (var (handle, reqState) in meta.Reads)
            {
                 var res = _resources[handle.Id];
                 res.CurrentState = reqState;
            }
            foreach (var (handle, reqState) in meta.Writes)
            {
                 var res = _resources[handle.Id];
                 res.CurrentState = reqState;
            }

            // Execute pass
            pass.Execute(context, rgContext);
        }
    }

    internal RGResourceHandle RegisterResourceRead(RGResourceHandle handle, RenderPass pass, ResourceState state)
    {
        if (_passMetadata.TryGetValue(pass, out var meta))
        {
            meta.Reads.Add((handle, state));
        }
        return handle;
    }

    internal RGResourceHandle RegisterResourceWrite(RGResourceHandle handle, RenderPass pass, ResourceState state)
    {
         if (_passMetadata.TryGetValue(pass, out var meta))
         {
             meta.Writes.Add((handle, state));
         }
         return handle;
    }

    internal ITexture? GetPhysicalTexture(RGResourceHandle handle)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGTexture;
            return res?.InternalTexture;
        }
        return null;
    }

    internal ITextureView? GetPhysicalTextureView(RGResourceHandle handle, TextureViewType type)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGTexture;
            // Return imported view if it matches requested type
            if (res != null && res.InternalView != null && res.InternalView.GetDesc().ViewType == type)
                return res.InternalView;
            
            // Otherwise try getting default view from texture
            return res?.InternalTexture?.GetDefaultView(type);
        }
        return null;
    }

    public void Reset()
    {
        // Don't dispose internal resources here if we want to reuse them, 
        // but for now we are clearing everything to rebuild graph
        // If we want to persistent resources across frames, we need a pool.
        // For simplicity: clear lists.
        _passes.Clear();
        _resources.Clear();
        _resourceMap.Clear();
    }

    public void Dispose()
    {
        foreach (var res in _resources)
        {
            if (!res.IsImported)
            {
                if (res is RGTexture tex) tex.InternalTexture?.Dispose();
                if (res is RGBuffer buf) buf.InternalBuffer?.Dispose();
            }
        }
        _resources.Clear();
        _passes.Clear();
        _resourceMap.Clear();
    }
}
