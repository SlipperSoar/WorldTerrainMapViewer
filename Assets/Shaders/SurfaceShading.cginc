// SurfaceShading.cginc
//
// 步骤 2 的「独立水面 + 法线贴图 + 潮汐」共享函数，供 EarthSurfaceLitShader 的
// ForwardBase / ForwardAdd 两个 pass 同时 #include，避免复制两份逻辑。
//
// 所有解码公式与 Assets/Scripts/WorldTerrain/WorldGenData.cs 的 SurfaceContract
// 一一对应：改一边必须改另一边，并同步 VERIFICATION.md。
//
//   _surface.png  R = 高度（-10000..10000 m → 0..255 → 采样 0..1）
//                 G = 水体分类（间隔编码：类别 * 16 / 255 → 采样 0..1）
//                 B = 气温   A = 湿度
//   _detail.png   RG = 法线 XY   B = AO   A = 粗糙度

#ifndef SURFACE_SHADING_INCLUDED
#define SURFACE_SHADING_INCLUDED

// ── 与 SurfaceContract 一致的常量 ──────────────────────────────────────
#define WATER_STRIDE        16.0        // SurfaceContract.WaterClassStride
#define WATER_THRESHOLD     8.0         // SurfaceContract.WaterThreshold（字节）
#define HEIGHT_MIN_M       (-10000.0)
#define HEIGHT_MAX_M       10000.0

// 水体分类（与 WaterClass 枚举顺序一致）
#define WC_LAND             0
#define WC_DEEP_OCEAN       1
#define WC_OCEAN            2
#define WC_SHALLOW_OCEAN    3
#define WC_LAKE             4
#define WC_RIVER            5
#define WC_SEASONAL_RIVER   6
#define WC_DELTA            7
#define WC_SEA_ICE          8
#define WC_GLACIER          9
#define WC_SNOW_PEAK       10

// 由材质传入的渲染参数（与 EarthManager.SetSurfaceData 对应）
// 本 cginc 自己声明全部需要的采样器，避免依赖 include 时的声明顺序。
sampler2D _SurfaceTex;
sampler2D _DetailTex;
sampler2D _HeightTex;
float4 _HeightTex_TexelSize;
float _SeaLevel;                // 米，恒 0（保留接口）
float _SnowLineEquator;
float _SnowLineMid;
float _SnowLinePole;
float _TideAmplitude;           // 米
float _TidePeriod;              // 地球日
float _TideTime;                // 累计时间（天），由 EarthManager.Update 写入
float _WaterSurfaceOn;          // 0/1：是否启用独立水面着色（无数据图时 0）
float _NormalOn;                // 0/1：是否启用法线贴图
float _FoamStrength;            // 泡沫强度
float _WaterSpecular;           // 水面高光强度

// 高度（米）从 _surface 的 R 通道反推
float DecodeHeightMeters(float r)
{
    return lerp(HEIGHT_MIN_M, HEIGHT_MAX_M, r);
}

// G 通道字节值（0..255）
float WaterByte(float g)
{
    return g * 255.0;
}

// 水体分类（0..10）
int WaterClassOf(float g)
{
    int cls = (int)round(WaterByte(g) / WATER_STRIDE);
    return clamp(cls, 0, WC_SNOW_PEAK);
}

// 是否有水面（含冰）
bool HasWater(float g)
{
    return WaterByte(g) >= WATER_THRESHOLD;
}

// 潮汐：返回相对海平面的升降（米）。
// _TidePeriod 以「地球日」为单位，默认 0.5 = 半日潮（一天两次涨落）。
// _TideTime 由 C# 侧按真实时间/模拟时间累计，单位同样是天。
float TideOffsetMeters()
{
    if (_TidePeriod <= 1e-4)
        return 0.0;
    float phase = _TideTime / _TidePeriod * 6.28318530718;
    return _TideAmplitude * sin(phase);
}

// 当前有效海平面（米）：静态海平面 + 潮汐升降
float SeaLevelMeters()
{
    return _SeaLevel + TideOffsetMeters();
}

// 顶点位移用的「有效高度」：
//   - 陆地：用高度图（保留凹凸）
//   - 水体：钳到海平面（水面水平，不跟着海底深度凹下去）
// 返回的是归一化高度图的等价 r 值（0..1），供 GetDisplacedHeight 使用。
float SurfaceDisplacementR(float2 uv)
{
    float r = tex2Dlod(_HeightTex, float4(uv, 0, 0)).r;   // 原始高度编码
#if defined(_SURFACE_DATA_ON)
    if (_WaterSurfaceOn > 0.5)
    {
        float g = tex2Dlod(_SurfaceTex, float4(uv, 0, 0)).g;
        if (HasWater(g))
        {
            // 海平面对应的编码值：把 0 米（+潮汐）映射回 0..1
            float seaR = (SeaLevelMeters() - HEIGHT_MIN_M) / (HEIGHT_MAX_M - HEIGHT_MIN_M);
            return clamp(seaR, 0.0, 1.0);
        }
    }
#endif
    return r;
}

// 水面颜色：只按「深度 + 类别」区分，不依赖几何高度。
// 深度 = 海平面 - 地形高度（越深越大），归一到 0..1。
float3 WaterColor(float g, float heightMeters, out float foam)
{
    int cls = WaterClassOf(g);
    float depth = max(0.0, SeaLevelMeters() - heightMeters);
    float d = saturate(depth / 6000.0);        // 0 浅 → 1 深

    float3 shallow = float3(0.20, 0.52, 0.68);
    float3 deep    = float3(0.02, 0.10, 0.28);
    float3 col = lerp(shallow, deep, d);

    // 淡水（湖 / 河 / 三角洲 / 季节河）略偏绿、整体更亮
    if (cls == WC_LAKE || cls == WC_RIVER || cls == WC_SEASONAL_RIVER || cls == WC_DELTA)
    {
        col = lerp(col, float3(0.24, 0.50, 0.58), 0.45);
        if (cls == WC_SEASONAL_RIVER)
            col = lerp(col, float3(0.48, 0.55, 0.50), 0.5);   // 季节河偏沙色
        if (cls == WC_DELTA)
            col = lerp(col, float3(0.52, 0.60, 0.50), 0.5);   // 三角洲带泥沙
    }
    // 冰：海冰 / 冰川 / 雪峰走冷白
    if (cls == WC_SEA_ICE || cls == WC_GLACIER || cls == WC_SNOW_PEAK)
        col = lerp(col, float3(0.80, 0.86, 0.92), 0.85);

    // 泡沫：近岸（深度很小）+ 潮汐涨落带
    foam = 0.0;
    float coast = 1.0 - smoothstep(0.0, 260.0, depth);
    float tideBand = abs(TideOffsetMeters()) / max(_TideAmplitude, 1e-3);
    foam = coast * (0.55 + 0.45 * tideBand);
    // 河流/三角洲也有轻微白沫
    if (cls == WC_RIVER || cls == WC_DELTA || cls == WC_SEASONAL_RIVER)
        foam = max(foam, 0.35);

    return col;
}

// 切线空间法线 → 世界空间扰动。**只用于陆地**：
// _detail 的法线来自高度场梯度，海洋格是海底负值，贴到水平海面上会变成错误
// 的波纹；水面保持球体法线（水平），深度只用颜色区分。
// 球面等距圆柱 UV 上，切向 T 沿经度（u 增大方向）、B 沿纬度（v 增大方向）、N 为球面外法线。
// 极点附近 cos(lat)→0，T 会退化，故对切线长度做下限保护。
float3 PerturbWorldNormal(float3 worldNormal, float3 worldPos, float2 uv, float3 detail)
{
    if (_NormalOn < 0.5)
        return worldNormal;

    float2 nxy = detail.rg * 2.0 - 1.0;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    float3 tangentNormal = normalize(float3(nxy, nz));

    // 由球面解析求切向：经度方向 T = normalize(cross(up, N))；纬度方向 B = cross(N, T)
    float3 N = normalize(worldNormal);
    float3 up = float3(0.0, 1.0, 0.0);
    float3 T = cross(up, N);
    float tLen = length(T);
    if (tLen < 1e-3)                       // 极点：换一个参考轴避免退化
        T = cross(float3(1.0, 0.0, 0.0), N);
    else
        T /= tLen;
    float3 B = normalize(cross(N, T));

    return normalize(T * tangentNormal.x + B * tangentNormal.y + N * tangentNormal.z);
}

#endif // SURFACE_SHADING_INCLUDED
