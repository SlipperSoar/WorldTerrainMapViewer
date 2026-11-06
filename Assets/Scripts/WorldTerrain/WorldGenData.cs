using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// Lightweight 3D vector using doubles, thread-safe (no UnityEngine dependency).
    /// </summary>
    public struct Vector3D
    {
        public double x, y, z;

        public Vector3D(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public double Dot(Vector3D o)
        {
            return x * o.x + y * o.y + z * o.z;
        }

        public double LengthSquared => Dot(this);

        public double Length => Math.Sqrt(LengthSquared);

        public Vector3D Normalized
        {
            get
            {
                double len = Length;
                if (len < 1e-12)
                    return new Vector3D(0, 0, 0);
                return new Vector3D(x / len, y / len, z / len);
            }
        }

        public static Vector3D operator -(Vector3D a, Vector3D b)
            => new Vector3D(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3D operator +(Vector3D a, Vector3D b)
            => new Vector3D(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3D operator *(Vector3D a, double s)
            => new Vector3D(a.x * s, a.y * s, a.z * s);

        public override string ToString()
            => $"({x:F4}, {y:F4}, {z:F4})";
    }

    /// <summary>
    /// Tectonic plate metadata. Plates are purely spatial divisions — they do
    /// not determine land/sea; that emerges from the continuous height field.
    /// </summary>
    public struct PlateInfo
    {
        public Vector3D center;
        public Vector3D velocity;
        public float driftSpeed;
    }

    /// <summary>
    /// Generation configuration.
    /// </summary>
    public struct WorldGenConfig
    {
        public int seed;
        public int width;
        public int height;
        public int minPlates;
        public int maxPlates;
        public float maxElevation;
        public float minElevation;
        public float waterCoverage;  // 0-1: fraction of surface that should be ocean
        public float axialTilt;      // 地轴倾角（黄赤交角），0-90 度；地图属性

        /// <summary>星球半径（km）；地图属性，只影响显示尺寸与镜头距离，不影响地形。</summary>
        public float planetRadiusKm;

        /// <summary>是否逆向自转；地图属性，决定时间流转时太阳的转动方向。</summary>
        public bool rotationRetrograde;

        /// <summary>
        /// false（默认）：在单位球面的等面积网格上生成地形，再投影成等距圆柱高度图（推荐）；
        /// true：沿用旧的「直接在矩形网格上生成」路径，便于对比与回退。
        /// </summary>
        public bool planarGeneration;

        /// <summary>
        /// 保留水文诊断场（<c>discharge</c> / <c>catchmentMoisture</c>）。
        /// 默认关闭：这两个数组在生成过程中只被水文自己使用，之后没有任何消费者，
        /// 关掉后水文步骤一结束就释放（8192 档各 134 MB）。验证台需要 dump 时才打开。
        /// </summary>
        public bool keepHydrologyDiagnostics;
    }

    /// <summary>
    /// Terrain classification types for the final map.
    /// </summary>
    public enum TerrainType
    {
        DeepOcean,
        Ocean,
        ShallowOcean,
        Coast,
        Grassland,
        Forest,
        Hills,
        Highland,
        Plateau,
        Mountain,
        HighMountain,
        SnowPeak,
        Desert,
        Gobi,
        Tundra,
        Rainforest,
        Glacier,
        Lake,
        River,
        SeaIce,
        /// <summary>季节性（间歇性）河流：集水区气候干旱，仅雨季有水</summary>
        SeasonalRiver,
        /// <summary>河口三角洲的分流河道与三角洲平原</summary>
        Delta,

        // ── 气候驱动的生物群系（由 BiomeModel 判定，见该类注释）──
        // 追加在末尾：已有成员的数值不变，避免影响已保存的数据。

        /// <summary>沼泽 / 湿地：低洼、平缓、极湿（也偏冷的地方排水不畅）</summary>
        Marsh,
        /// <summary>针叶林：冷温带湿润区（最热月 7~12℃）</summary>
        ConiferForest,
        /// <summary>稀树草原：热带半干旱/半湿润区</summary>
        Savanna,
        /// <summary>干草原：温带半干旱区</summary>
        Steppe,
        /// <summary>裸岩 / 高山荒漠：林线以上、雪线以下的干旱地带</summary>
        BareRock,
        /// <summary>高山草甸：林线以上、雪线以下的湿润地带</summary>
        AlpineMeadow
    }

    /// <summary>
    /// 水文生成结果：河道、湖泊、河宽与各类水体掩膜。
    /// </summary>
    public struct HydrologyData
    {
        /// <summary>河道掩膜（含加粗后的干流）</summary>
        public System.Collections.BitArray riverMask;

        /// <summary>湖泊掩膜（被填平的洼地）</summary>
        public System.Collections.BitArray lakeMask;

        /// <summary>河宽（像素），合流变宽、分流变窄</summary>
        public float[] riverWidth;

        /// <summary>入海口（河流注入海洋的那一格）</summary>
        public System.Collections.BitArray estuaryMask;

        /// <summary>季节性河流掩膜（集水区平均湿度低于气候干旱阈值）</summary>
        public System.Collections.BitArray seasonalMask;

        /// <summary>三角洲分流河道掩膜（浅海中的扇形河道）</summary>
        public System.Collections.BitArray deltaMask;

        /// <summary>有效流量（赤道等效格数），用于宽度与调试</summary>
        public float[] discharge;

        /// <summary>湖泊水深（米），非湖泊为 0；由填洼后的水面高程减原高程得到</summary>
        public float[] waterDepth;

        /// <summary>河道的集水区平均湿度（0-1），用于判定季节性河流</summary>
        public float[] catchmentMoisture;
    }

    /// <summary>
    /// 水体分类（V3.2.0 起）。写进 <c>_surface.png</c> 的 **G 通道**。
    ///
    /// 步骤 1 里 G 是「0/255 二值硬掩膜」，只能分水/陆；步骤 2 的独立水面 pass
    /// 需要区分河 / 湖 / 浅海 / 深海 / 冰，才能分别做深度着色、泡沫与河网。
    /// 因此 V3.2.0 把 G 改成**分类值**。值本身不直接写进通道 —— 见
    /// <see cref="SurfaceContract.WaterClassStride"/> 的间隔编码说明。
    ///
    /// 顺序即数值，新增类只能追加在末尾，不要插入中间（否则已导出图语义漂移）。
    /// </summary>
    public enum WaterClass : byte
    {
        /// <summary>陆地（无水）。</summary>
        Land = 0,
        /// <summary>深海。</summary>
        DeepOcean = 1,
        /// <summary>海洋。</summary>
        Ocean = 2,
        /// <summary>浅海 / 大陆架。</summary>
        ShallowOcean = 3,
        /// <summary>湖泊（淡水）。</summary>
        Lake = 4,
        /// <summary>常年河流。</summary>
        River = 5,
        /// <summary>季节性（间歇）河流。</summary>
        SeasonalRiver = 6,
        /// <summary>河口三角洲分流河道。</summary>
        Delta = 7,
        /// <summary>海冰（海面常年冻结）。</summary>
        SeaIce = 8,
        /// <summary>陆地冰川 / 极地冰盖。</summary>
        Glacier = 9,
        /// <summary>终年积雪的山峰。</summary>
        SnowPeak = 10
    }

    /// <summary>
    /// 行星渲染「数据契约」的编码常量（步骤 1 定稿，V3.2.0 修订 G 通道）。
    ///
    /// 两张新图都是 **RGBA8 数据贴图**（不是给人看的彩色图），供后续步骤的着色器采样：
    ///   - <c>_surface.png</c>：R=高度、G=水体分类、B=气温、A=湿度
    ///   - <c>_detail.png</c> ：R/G=切线空间法线 XY、B=AO、A=粗糙度
    ///
    /// 所有编码都只用整数与 <see cref="System.Math"/>，可在后台线程 / 离线校验台运行。
    /// 解码公式与这里一一对应，改一个必须同时改另一个，并更新 VERIFICATION.md。
    /// </summary>
    public static class SurfaceContract
    {
        /// <summary>高度编码范围（米）：与 <c>WorldGenConfig</c> 的 min/maxElevation 一致。</summary>
        public const float HeightMinMeters = -10000f;
        public const float HeightMaxMeters = 10000f;

        /// <summary>气温编码范围（℃，最热月平均气温的钳制区间）。</summary>
        public const double TemperatureMinC = -40.0;
        public const double TemperatureMaxC = 40.0;

        /// <summary>湿度本身已在 0-1，直接线性编码。</summary>
        public const double MoistureMin = 0.0;
        public const double MoistureMax = 1.0;

        // ── G 通道：水体分类的间隔编码 ──────────────────────────────────
        //
        // 为什么不用 0/1/2/... 直接存：分类值太小（0..10 → 归一化后 0..0.04），
        // 一旦有人误用 Bilinear 采样或线性到 sRGB 的转换，数值就会被抹平，
        // 河可能被判成海。间隔编码把每类放到一个「平台」上，插值落在两类中点时
        // 也只会 round 到相邻之一，鲁棒得多；且 shader 里判断「有水」只需 g > 半个间隔。
        //
        //   G_byte = (int)WaterClass * 16        （Land=0, DeepOcean=16, ... SnowPeak=160）
        //   解码   = (WaterClass)round(G_byte / 16)
        //   是否有水 = G_byte >= 8（首个非陆地类的半间隔）
        //
        // 上限：255/16 = 15 类，当前 10 类（不含 Land），留 5 类余量。

        /// <summary>水体分类编码间隔。</summary>
        public const int WaterClassStride = 16;

        /// <summary>首个非陆地类编码值的一半：G 大于等于它就算「有水」。</summary>
        public const byte WaterThreshold = WaterClassStride / 2;

        /// <summary>把水体分类编码进 G 通道。</summary>
        public static byte EncodeWater(WaterClass cls)
        {
            return (byte)((int)cls * WaterClassStride);
        }

        /// <summary>从 G 通道字节解出分类（四舍五入到最近的平台）。</summary>
        public static WaterClass DecodeWater(byte g)
        {
            int cls = (int)System.Math.Round((double)g / WaterClassStride);
            if (cls < 0) cls = 0;
            if (cls > (int)WaterClass.SnowPeak) cls = (int)WaterClass.SnowPeak;
            return (WaterClass)cls;
        }

        /// <summary>G 通道是否表示有水（含冰）。</summary>
        public static bool IsWaterByte(byte g) => g >= WaterThreshold;

        /// <summary>法线 XY 编码到 0-255（解码时 <c>xy = c/255*2-1</c>，Z 在着色器里重建）。</summary>
        public const float NormalNeutral = 0.5f;

        /// <summary>把物理量线性量化到 0-255（四舍五入，与 TerrainMapRenderer.ToByte 一致）。</summary>
        public static byte Encode(double value, double min, double max)
        {
            if (max <= min) return 0;
            double t = (value - min) / (max - min);
            if (t <= 0.0) return 0;
            if (t >= 1.0) return 255;
            return (byte)System.Math.Round(t * 255.0);
        }
    }

    /// <summary>
    /// 「渲染参数」：写进 <c>_meta.json</c>、供着色器读取的**可复现标量**。
    ///
    /// 这些值不是生成器输入（改它们不会改变地形），而是对生成结果的**描述** ——
    /// 海平面、雪线、潮汐。步骤 2/3/4 的着色器据此换算海面高度、雪线与潮位。
    /// 全部由生成参数与结果确定，因此同一配方两次生成必然得到同一组值。
    ///
    /// 这里刻意做成**只含字段的普通类**（不引用 <c>TerrainContext</c>、<c>PolarClimate</c>），
    /// 计算逻辑放在 <c>SurfaceDataLayers.ComputeRenderParams</c>：离线校验台会直接编译本文件，
    /// 但不一定把那些类型一起编进来，保持本文件零依赖才不会破坏 gate / icecheck。
    /// </summary>
    [System.Serializable]
    public class SurfaceRenderParams
    {
        /// <summary>海平面（米）。高度场在「海平面标定」步骤已平移到海平面=0，故恒为 0。</summary>
        public float seaLevelMeters;

        /// <summary>赤道雪线（米，按地图起伏标定，湿润侧）。</summary>
        public float snowLineEquatorMeters;

        /// <summary>中纬（45°）雪线（米）。</summary>
        public float snowLineMidMeters;

        /// <summary>极地雪线（米，通常为 0，表示海平面即终年积雪）。</summary>
        public float snowLinePoleMeters;

        /// <summary>潮汐振幅（米）：海面相对海平面的最大升降，步骤 4 的简化潮汐用。</summary>
        public float tideAmplitudeMeters = DefaultTideAmplitudeMeters;

        /// <summary>潮汐周期（地球日）。</summary>
        public float tidePeriodDays = DefaultTidePeriodDays;

        /// <summary>默认潮汐振幅（米）：量级参考地球，不至于把 1~2 km 起伏的地图淹没。</summary>
        public const float DefaultTideAmplitudeMeters = 2f;

        /// <summary>默认潮汐周期（地球日，半日潮的近似包络）。</summary>
        public const float DefaultTidePeriodDays = 0.5f;
    }

    /// <summary>
    /// Complete result of a world generation run.
    /// </summary>
    public class WorldGenResult
    {
        public int width;
        public int height;
        public float[] heightField;
        public ushort[] plateIds;
        public byte[] terrainTypes;

        /// <summary>本次生成使用的地图属性：地轴倾角（度）。</summary>
        public float axialTilt;

        /// <summary>本次生成使用的地图属性：水面占比（0-1）。</summary>
        public float waterCoverage;

        /// <summary>本次生成使用的地图属性：星球半径（km）。</summary>
        public float planetRadiusKm;

        /// <summary>本次生成使用的地图属性：是否逆向自转。</summary>
        public bool rotationRetrograde;

        /// <summary>人文图层：聚落、国家、交通线与自然地名。</summary>
        public HumanGeographyData human;
    }
}
