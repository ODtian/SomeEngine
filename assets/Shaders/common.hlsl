#ifndef COMMON_HLSL
#define COMMON_HLSL

struct GpuTransform
{
    float4 Rotation;
    float3 Position;
    float Scale;
    float3 Stretch;
    float Padding;
};

float3 ApplyTransform(float3 p, GpuTransform t)
{
    float3 ps = p * t.Scale * t.Stretch;
    
    // Rotate (Quat * Vector)
    float3 qvec = t.Rotation.xyz;
    float qw = t.Rotation.w;
    float3 uv = cross(qvec, ps);
    float3 uuv = cross(qvec, uv);
    ps = ps + ((uv * qw) + uuv) * 2.0;

    return ps + t.Position;
}

#endif
