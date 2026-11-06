using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// 行星渲染「数据契约」的两张数据贴图（步骤 1）。
    ///
    /// 与给人看的彩色图不同，这两张图的 RGBA 通道是**物理量编码**，供后续步骤的
    /// 着色器直接采样（步骤 2 的法线 / 独立水面 pass，步骤 3 与 4 的雪线 / 潮汐）。
    ///
    ///   _surface.png  R = 高度（-10000..10000 m → 0..255）
    ///                 G = 水体分类（V3.2 起，间隔编码，见 WaterClass）
    ///                 B = 气温（最热月平均气温 -40..40℃ → 0..255，随海拔递减）
    ///                 A = 湿度（0..1 → 0..255）
    ///
    ///   _detail.png   R/G = 切线空间法线 XY（由高度场梯度求得，Z 在着色器里重建）
    ///                 B   = AO（坡度越大越暗，山脊/谷地的环境光遮蔽近似）
    ///                 A   = 粗糙度（由地形类型与湿度估计，1 = 粗糙，0 = 光滑/镜面）
    ///
    /// 本类只提供**按行带的纯渲染函数与标量计算**：
    ///   - 不建 Texture2D、不写文件 —— 那些统一由 <see cref="MapExporter"/> 的流式管线负责；
    ///   - 只读取生成结果，不修改任何生成状态，因此不改变生成语义（11 个 dump 基线不变）；
    ///   - 只用 <see cref="System.Math"/> 与 <c>Color32</c>，可后台线程 / 离线校验台运行。
    /// 编码常量集中在 <see cref="SurfaceContract"/>，解码公式见 VERIFICATION.md。
    /// </summary>
    public static class SurfaceDataLayers
    {
        // ── 渲染参数（写进 _meta.json）─────────────────────────────────────

        /// <summary>
        /// 由「最高陆地海拔 + 地轴倾角」计算可复现的渲染参数（海平面 / 雪线 / 潮汐）。
        ///
        /// 纯函数：只读高度场与倾角，不碰 <c>TerrainContext</c> 的其它字段，
        /// 因此离线校验台、探针、运行时导出三条路径得到同一组值。
        /// </summary>
        public static SurfaceRenderParams ComputeRenderParams(float[] heightField, float axialTilt)
        {
            float maxLand = 0f;
            if (heightField != null)
            {
                for (int i = 0; i < heightField.Length; i++)
                    if (heightField[i] > maxLand) maxLand = heightField[i];
            }

            double tilt = axialTilt;
            // 雪线取「湿润侧」区间下限：着色器画雪以它为基准，湿度再往上浮动。
            // 赤道 / 中纬 / 极地三个采样点足够让着色器按纬度插值出整条雪线。
            PolarClimate.ScaledSnowLineRange(0.0, tilt, maxLand, out double eqMin, out _);
            PolarClimate.ScaledSnowLineRange(45.0, tilt, maxLand, out double midMin, out _);
            PolarClimate.ScaledSnowLineRange(85.0, tilt, maxLand, out double poleMin, out _);

            return new SurfaceRenderParams
            {
                seaLevelMeters = 0f,          // 海平面标定后高度场以 0 为海平面
                snowLineEquatorMeters = (float)eqMin,
                snowLineMidMeters = (float)midMin,
                snowLinePoleMeters = (float)poleMin,
                tideAmplitudeMeters = SurfaceRenderParams.DefaultTideAmplitudeMeters,
                tidePeriodDays = SurfaceRenderParams.DefaultTidePeriodDays
            };
        }

        /// <summary>便捷重载：直接从生成上下文取参数。</summary>
        public static SurfaceRenderParams ComputeRenderParams(TerrainContext ctx)
        {
            if (ctx == null)
                return new SurfaceRenderParams();
            return ComputeRenderParams(ctx.HeightField, ctx.Config.axialTilt);
        }

        // ── 水体分类（供 surface 的 G 通道与后续独立水面 pass 共用）─────────

        /// <summary>
        /// 地形类型 → 水体分类。与 <c>TerrainClassifier</c> 的类型一一对应；
        /// <c>Land</c> 表示没有水面（所有陆地群系）。
        /// </summary>
        public static WaterClass ClassifyWater(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.DeepOcean:    return WaterClass.DeepOcean;
                case TerrainType.Ocean:        return WaterClass.Ocean;
                case TerrainType.ShallowOcean: return WaterClass.ShallowOcean;
                case TerrainType.Lake:         return WaterClass.Lake;
                case TerrainType.River:        return WaterClass.River;
                case TerrainType.SeasonalRiver:return WaterClass.SeasonalRiver;
                case TerrainType.Delta:        return WaterClass.Delta;
                case TerrainType.SeaIce:       return WaterClass.SeaIce;
                case TerrainType.Glacier:      return WaterClass.Glacier;
                case TerrainType.SnowPeak:     return WaterClass.SnowPeak;
                default:                       return WaterClass.Land;
            }
        }

        /// <summary>该地形类型是否有水面（含冰）。供 roughness 等处复用。</summary>
        public static bool IsWater(TerrainType type) => ClassifyWater(type) != WaterClass.Land;

        // ── 地表图（R 高度 / G 水体分类 / B 气温 / A 湿度）─────────────────

        /// <summary>
        /// 把 [y0, y1) 行渲染成 RGBA 地表数据，写进 <paramref name="dest"/>（从 <paramref name="destOffset"/> 起）。
        /// 行序与贴图 / PNG 一致：第 0 行 = 北极。
        /// </summary>
        public static void RenderSurfaceRows(TerrainContext ctx, int y0, int y1,
            Color32[] dest, int destOffset = 0)
        {
            int w = ctx.Width, h = ctx.Height;
            float[] heightField = ctx.HeightField;
            byte[] types = ctx.TerrainTypes;
            float[] moisture = ctx.Moisture;
            double tilt = ctx.Config.axialTilt;

            for (int y = y0; y < y1; y++)
            {
                double lat = 90.0 - (double)y / (h - 1) * 180.0;
                double absLat = Math.Abs(lat);
                double seaLevelTemp = PolarClimate.SeaLevelSummerTemperature(absLat, tilt);

                int src = y * w;
                int dst = destOffset + (y - y0) * w;
                for (int x = 0; x < w; x++)
                {
                    int i = src + x;
                    float elev = heightField[i];

                    // R：高度（与灰度高度图同一编码范围，便于复用）
                    byte r = SurfaceContract.Encode(elev,
                        SurfaceContract.HeightMinMeters, SurfaceContract.HeightMaxMeters);

                    // G：水体分类（间隔编码）。types 为空时全部按 Land 处理。
                    WaterClass waterCls = types != null
                        ? ClassifyWater((TerrainType)types[i])
                        : WaterClass.Land;
                    byte g = SurfaceContract.EncodeWater(waterCls);

                    // B：该海拔的最热月平均气温（与 BiomeModel 用同一气温函数）
                    double localTemp = PolarClimate.TemperatureAtElevation(seaLevelTemp, elev);
                    byte b = SurfaceContract.Encode(localTemp,
                        SurfaceContract.TemperatureMinC, SurfaceContract.TemperatureMaxC);

                    // A：湿度场（0-1）
                    byte a = SurfaceContract.Encode(
                        moisture != null && i < moisture.Length ? moisture[i] : 0.0,
                        SurfaceContract.MoistureMin, SurfaceContract.MoistureMax);

                    dest[dst + x] = new Color32(r, g, b, a);
                }
            }
        }

        // ── 细节图（R/G 法线 XY / B AO / A 粗糙度）─────────────────────────

        /// <summary>
        /// 把 [y0, y1) 行渲染成 RGBA 细节数据，写进 <paramref name="dest"/>。
        /// 法线由高度场中央差分求梯度，再按 <c>reliefScale</c> 归一化 —— 与彩色地形图
        /// 的起伏阴影用的是同一尺度，避免出现「法线与明暗对不上」。
        /// </summary>
        public static void RenderDetailRows(TerrainContext ctx, float reliefScale,
            int y0, int y1, Color32[] dest, int destOffset = 0)
        {
            int w = ctx.Width, h = ctx.Height;
            float[] heightField = ctx.HeightField;
            byte[] types = ctx.TerrainTypes;
            float[] moisture = ctx.Moisture;
            if (reliefScale <= 0f) reliefScale = 1f;

            for (int y = y0; y < y1; y++)
            {
                int yu = y > 0 ? y - 1 : 0;
                int yd = y + 1 < h ? y + 1 : h - 1;
                int row = y * w;
                int rowUp = yu * w;
                int rowDown = yd * w;
                int dst = destOffset + (y - y0) * w;

                for (int x = 0; x < w; x++)
                {
                    int xl = x > 0 ? x - 1 : w - 1;     // 经度环绕
                    int xr = x + 1 < w ? x + 1 : 0;
                    int i = row + x;

                    // 中央差分（米/格）；除以 reliefScale 得到无量纲坡度
                    float gx = (heightField[row + xr] - heightField[row + xl]) / reliefScale;
                    float gy = (heightField[rowDown + x] - heightField[rowUp + x]) / reliefScale;

                    // 法线 = normalize(-gx, -gy, 1)，把 XY 从 [-1,1] 映射到 [0,1]
                    double nx = -gx * TerrainMapRenderer.DetailNormalStrength;
                    double ny = -gy * TerrainMapRenderer.DetailNormalStrength;
                    double inv = 1.0 / Math.Sqrt(nx * nx + ny * ny + 1.0);
                    byte nr = SurfaceContract.Encode(nx * inv * 0.5 + 0.5, 0.0, 1.0);
                    byte ng = SurfaceContract.Encode(ny * inv * 0.5 + 0.5, 0.0, 1.0);

                    // AO：坡度越大越暗（谷地/山脊的粗略环境光遮蔽）
                    float slope = (float)Math.Sqrt(gx * gx + gy * gy);
                    float ao = 1f - TerrainMapRenderer.DetailAoStrength * (slope / (1f + slope));
                    if (ao < 0f) ao = 0f;
                    if (ao > 1f) ao = 1f;

                    // 粗糙度：水面/冰面光滑，裸岩/戈壁粗糙，湿润森林居中
                    float roughness = RoughnessOf(
                        types != null ? (TerrainType)types[i] : TerrainType.Grassland,
                        moisture != null && i < moisture.Length ? moisture[i] : 0.5f);

                    dest[dst + x] = new Color32(
                        nr, ng,
                        SurfaceContract.Encode(ao, 0.0, 1.0),
                        SurfaceContract.Encode(roughness, 0.0, 1.0));
                }
            }
        }

        /// <summary>单格粗糙度（0=镜面光滑，1=完全粗糙），只依赖类型与湿度。</summary>
        public static float RoughnessOf(TerrainType type, float moisture)
        {
            if (IsWater(type))
            {
                // 冰面比液态水略粗糙；季节河/三角洲带泥沙
                switch (type)
                {
                    case TerrainType.SeaIce:
                    case TerrainType.Glacier:
                    case TerrainType.SnowPeak: return 0.35f;
                    case TerrainType.SeasonalRiver:
                    case TerrainType.Delta: return 0.30f;
                    default: return 0.08f;
                }
            }

            switch (type)
            {
                case TerrainType.BareRock:
                case TerrainType.Gobi:      return 0.90f;
                case TerrainType.HighMountain:
                case TerrainType.Mountain:
                case TerrainType.Highland:  return 0.85f;
                case TerrainType.Desert:
                case TerrainType.Steppe:    return 0.80f;
                case TerrainType.Tundra:    return 0.75f;
                case TerrainType.Coast:     return 0.70f;
                case TerrainType.Marsh:     return 0.55f;
                case TerrainType.Savanna:
                case TerrainType.Grassland: return 0.70f;
                case TerrainType.Rainforest:
                case TerrainType.Forest:
                case TerrainType.ConiferForest:
                case TerrainType.AlpineMeadow:
                    // 湿润植被略降低粗糙度（叶面含水量）
                    return 0.72f - 0.12f * Math.Max(0f, Math.Min(1f, moisture));
                default:                    return 0.70f;
            }
        }
    }
}
