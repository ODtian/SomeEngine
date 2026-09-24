#include <cstdint>
#include <cstddef>
#include <cstring>
#include <new>

#include "../../external/tracy/public/tracy/TracyC.h"

struct SomeEngineTracyZoneContext
{
    uint32_t Id;
    int32_t Active;
};

namespace
{
struct SomeEngineTracySourceLocation
{
    char* Name;
    char* Function;
    char* File;
    uint32_t Line;
};

char* CopyString(const char* value, const char* fallback)
{
    const char* resolved = value != nullptr && std::strlen(value) > 2 ? value : fallback;
    if (resolved == nullptr || std::strlen(resolved) <= 2)
        resolved = "Managed";
    const size_t length = std::strlen(resolved);
    char* copy = new char[length + 1];
    std::memcpy(copy, resolved, length + 1);
    return copy;
}
}

extern "C"
{
__declspec(dllexport) void SomeEngineTracyStartupProfiler()
{
    ___tracy_startup_profiler();
}

__declspec(dllexport) void SomeEngineTracyShutdownProfiler()
{
    ___tracy_shutdown_profiler();
}

__declspec(dllexport) int32_t SomeEngineTracyProfilerStarted()
{
    return ___tracy_profiler_started();
}

__declspec(dllexport) uint64_t SomeEngineTracyCreateSourceLocation(
    uint32_t line,
    const char* source,
    const char* function,
    const char* name)
{
    auto* location = new SomeEngineTracySourceLocation {};
    location->Name = CopyString(name, "Managed Zone");
    location->Function = CopyString(function, "Unknown");
    location->File = CopyString(source, "Managed");
    location->Line = line;
    return reinterpret_cast<uint64_t>(location);
}

__declspec(dllexport) SomeEngineTracyZoneContext SomeEngineTracyBeginZone(uint64_t sourceLocation)
{
    if (sourceLocation == 0)
    {
        return SomeEngineTracyZoneContext
        {
            0,
            0
        };
    }

    const auto* location = reinterpret_cast<const SomeEngineTracySourceLocation*>(sourceLocation);
    const uint64_t payload = ___tracy_alloc_srcloc_name(
        location->Line,
        location->File,
        std::strlen(location->File),
        location->Function,
        std::strlen(location->Function),
        location->Name,
        std::strlen(location->Name),
        0);
    const TracyCZoneCtx context = ___tracy_emit_zone_begin_alloc(payload, 1);
    return SomeEngineTracyZoneContext
    {
        context.id,
        context.active
    };
}

__declspec(dllexport) void SomeEngineTracyEndZone(SomeEngineTracyZoneContext context)
{
    const TracyCZoneCtx nativeContext
    {
        context.Id,
        context.Active
    };
    ___tracy_emit_zone_end(nativeContext);
}

__declspec(dllexport) void SomeEngineTracyFrameMark()
{
    ___tracy_emit_frame_mark(nullptr);
}

__declspec(dllexport) void SomeEngineTracyFrameMarkNamed(const char* name)
{
    ___tracy_emit_frame_mark(name != nullptr && std::strlen(name) > 2 ? name : nullptr);
}

__declspec(dllexport) void SomeEngineTracySetThreadName(const char* name)
{
    ___tracy_set_thread_name(name != nullptr && std::strlen(name) > 2 ? name : "Managed Thread");
}

__declspec(dllexport) void SomeEngineTracyPlotInt(const char* name, int64_t value)
{
    if (name != nullptr && std::strlen(name) > 2)
        ___tracy_emit_plot_int(name, value);
}

__declspec(dllexport) int32_t SomeEngineTracyIsConnected()
{
    return ___tracy_connected();
}
}
