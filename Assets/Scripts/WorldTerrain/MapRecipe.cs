using System;

namespace WorldTerrain
{
    /// <summary>
    /// 地图**配方**：重新生成一张地图所需的全部参数，**只用整型**存储。
    ///
    /// 为什么必须是整型：
    ///   配方要经过 JSON 文本往返（`0.6f` 写成 "0.6" 再读回 double→float 未必还是同一个 float），
    ///   而「按配方重建」要求**逐位一致**。整型没有精度歧义，千分比/毫度/米都是精确的。
    ///
    /// 约定：内部生成仍然用 float，但 float 一律由整型**换算得到**（如 <c>permille / 1000f</c>），
    /// 因此同一个配方在任何时候、任何机器上都得到同一组输入。
    /// </summary>
    [Serializable]
    public struct MapRecipe
    {
        /// <summary>配方格式版本。生成规则变化导致「同配方不同结果」时递增。</summary>
        public int version;

        public int seed;

        /// <summary>地图宽度（像素），高度恒为一半。</summary>
        public int width;

        /// <summary>水面占比，千分比（0-1000）。</summary>
        public int waterPermille;

        /// <summary>地轴倾角，毫度（0-90000）。</summary>
        public int tiltMilliDeg;

        /// <summary>星球半径，米。</summary>
        public int radiusMeters;

        /// <summary>是否逆向自转。</summary>
        public bool retrograde;

        /// <summary>当前配方版本。</summary>
        public const int CurrentVersion = 1;

        private const int MinWaterPermille = 100;
        private const int MaxWaterPermille = 900;
        private const int MinTiltMilliDeg = 0;
        private const int MaxTiltMilliDeg = 90000;

        public static int ClampPermille(int v) =>
            v < MinWaterPermille ? MinWaterPermille : (v > MaxWaterPermille ? MaxWaterPermille : v);

        public static int ClampMilliDeg(int v) =>
            v < MinTiltMilliDeg ? MinTiltMilliDeg : (v > MaxTiltMilliDeg ? MaxTiltMilliDeg : v);

        /// <summary>把（UI 来的）浮点参数量化成整型配方。这是浮点进入系统的唯一入口。</summary>
        public static MapRecipe FromParameters(int seed, int width, float waterCoverage,
            float axialTilt, float planetRadiusKm, bool retrograde)
        {
            return new MapRecipe
            {
                version = CurrentVersion,
                seed = seed,
                width = width,
                waterPermille = ClampPermille((int)Math.Round(waterCoverage * 1000f)),
                tiltMilliDeg = ClampMilliDeg((int)Math.Round(axialTilt * 1000f)),
                radiusMeters = PlanetProfileToMeters(planetRadiusKm),
                retrograde = retrograde
            };
        }

        /// <summary>半径（km → 米，整型）。</summary>
        public static int PlanetProfileToMeters(float radiusKm)
        {
            float km = PlanetProfile.ClampRadiusKm(radiusKm);
            return (int)Math.Round(km * 1000f);
        }

        /// <summary>水面占比（浮点）。由千分比换算，结果只取决于整型。</summary>
        public float WaterCoverage => waterPermille / 1000f;

        /// <summary>地轴倾角（度）。由毫度换算。</summary>
        public float AxialTilt => tiltMilliDeg / 1000f;

        /// <summary>星球半径（km）。由米换算。</summary>
        public float PlanetRadiusKm => radiusMeters / 1000f;

        /// <summary>展开成生成配置（浮点全部来自整型换算）。</summary>
        public WorldGenConfig ToConfig()
        {
            int w = MapResolution.Snap(width);
            return new WorldGenConfig
            {
                seed = seed,
                width = w,
                height = MapResolution.HeightFor(w),
                minPlates = 4,
                maxPlates = 8,
                maxElevation = 10000f,
                minElevation = -10000f,
                waterCoverage = WaterCoverage,
                axialTilt = AxialTilt,
                planetRadiusKm = PlanetRadiusKm,
                rotationRetrograde = retrograde
            };
        }

        /// <summary>从配置反推配方（用于把已有配置登记成配方）。</summary>
        public static MapRecipe FromConfig(WorldGenConfig config, bool retrograde)
        {
            return FromParameters(config.seed, config.width, config.waterCoverage,
                config.axialTilt, config.planetRadiusKm, retrograde);
        }

        /// <summary>两个配方的整型字段是否完全一致。</summary>
        public bool SameAs(MapRecipe other)
        {
            return version == other.version
                && seed == other.seed
                && width == other.width
                && waterPermille == other.waterPermille
                && tiltMilliDeg == other.tiltMilliDeg
                && radiusMeters == other.radiusMeters
                && retrograde == other.retrograde;
        }

        public override string ToString()
        {
            return $"seed={seed} w={width} water={waterPermille}‰ tilt={tiltMilliDeg}m° " +
                   $"r={radiusMeters}m retro={retrograde} v{version}";
        }
    }
}
