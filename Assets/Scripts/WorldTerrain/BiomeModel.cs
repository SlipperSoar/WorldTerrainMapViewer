using System;

namespace WorldTerrain
{
    /// <summary>
    /// 生物群系（自然环境）判定：由**气温、湿度、海拔、坡度**决定地表类型。
    ///
    /// 与旧做法的关键区别是**气候优先**：旧分类器先按海拔分段（500/1000/2000/3000 m）
    /// 命名地表，气候只在少数分支里打补丁，结果同一海拔的湿润区与干旱区长得一样。
    /// 这里反过来 —— 同样的海拔，湿润处是森林、干旱处是沙漠；
    /// 海拔只通过「气温随高度递减」和「坡度」起作用。
    ///
    /// 判定顺序（先特殊后一般）：
    ///   海岸 → 沼泽 → 气温带 × 湿度带矩阵
    ///
    /// 气温取**最热月平均气温**（℃），由海平面气温按 5.5℃/km 递减到该海拔。
    /// </summary>
    public static class BiomeModel
    {
        // ── 气温带边界（最热月平均气温，℃）──
        /// <summary>低于此值：无林带 → 苔原 / 高山草甸 / 裸岩。</summary>
        public const double ColdBandMax = 7.0;

        /// <summary>冷温带上限：针叶林。</summary>
        public const double CoolBandMax = 12.0;

        /// <summary>温带上限：阔叶林 / 草原 / 沙漠；以上算热带。</summary>
        public const double TemperateBandMax = 20.0;

        // ── 湿度带边界（0-1，与 ClimateModel 的湿度场同量纲）──
        /// <summary>低于此值算极端干旱：沙漠 / 戈壁。</summary>
        public const double AridMax = 0.05;

        /// <summary>半干旱上限：干草原 / 稀树草原。</summary>
        public const double SemiAridMax = 0.30;

        /// <summary>半湿润上限：草原；以上算湿润（森林）。</summary>
        public const double SubHumidMax = 0.50;

        // ── 沼泽（湿地）──
        /// <summary>形成沼泽所需的最低湿度。</summary>
        public const double MarshMoistureMin = 0.72;

        /// <summary>沼泽的坡度上限（m/km）：水排不走才会积水。</summary>
        public const double MarshSlopeMax = 6.0;

        /// <summary>沼泽的海拔上限（m）：低洼地才积水。</summary>
        public const float MarshElevationMax = 250f;

        // ── 海岸 ──
        /// <summary>判为海岸（沙滩）的海拔上限（m）。</summary>
        public const float CoastElevationMax = 30f;

        /// <summary>判为海岸的离海距离上限（格）。</summary>
        public const int CoastSeaDistanceMax = 1;

        // ── 沙漠与戈壁的区分 ──
        /// <summary>高于此海拔的干旱区算戈壁（砾石/岩漠）而不是沙漠（沙丘）。</summary>
        public const float GobiElevationMin = 1500f;

        /// <summary>坡度超过此值（m/km）的干旱区算戈壁：陡坡存不住沙。</summary>
        public const double GobiSlopeMin = 60.0;

        /// <summary>高于此海拔的半干旱区算戈壁（干旱高原）。</summary>
        public const float DryHighlandElevationMin = 2000f;

        // ── 无林带的高纬判定 ──
        /// <summary>纬度绝对值超过此值时不区分高山类型，统一算苔原。</summary>
        public const double TundraLatitudeMin = 55.0;

        /// <summary>无林带里判为高山草甸（而非裸岩）的最低湿度。</summary>
        public const double AlpineMeadowMoistureMin = 0.45;

        /// <summary>沼泽要求的最低气温（℃）：冻土带不会有沼泽。</summary>
        public const double MarshTemperatureMin = 5.0;

        /// <summary>
        /// 依据气候与地形判定单格生物群系。
        /// </summary>
        /// <param name="temperature">该海拔的最热月平均气温（℃）</param>
        /// <param name="moisture">湿度（0-1，已含风带与雨影）</param>
        /// <param name="elevation">海拔（m）</param>
        /// <param name="slopeMetersPerKm">地面坡度（m/km）</param>
        /// <param name="seaDistance">离海距离（格）</param>
        /// <param name="absLatitude">纬度绝对值（度）</param>
        public static TerrainType Classify(
            double temperature, double moisture, float elevation,
            double slopeMetersPerKm, int seaDistance, double absLatitude)
        {
            // 终年冰冻（雪线以上已在分类器里被 Glacier 截走，这里只是兜底）
            if (temperature <= 0.0)
                return TerrainType.Glacier;

            // ── 海岸：贴着海面的低平地带，沙质 ──
            if (elevation <= CoastElevationMax && seaDistance <= CoastSeaDistanceMax)
                return TerrainType.Coast;

            // ── 无林带：气温不足以长树 ──
            if (temperature < ColdBandMax)
            {
                if (absLatitude >= TundraLatitudeMin)
                    return TerrainType.Tundra;

                // 低纬度的无林带是高山地带：湿润处草甸、干旱处裸岩
                return moisture >= AlpineMeadowMoistureMin
                    ? TerrainType.AlpineMeadow
                    : TerrainType.BareRock;
            }

            // ── 沼泽：平缓、低洼、极湿 ──
            if (temperature >= MarshTemperatureMin
                && moisture >= MarshMoistureMin
                && slopeMetersPerKm <= MarshSlopeMax
                && elevation <= MarshElevationMax)
            {
                return TerrainType.Marsh;
            }

            // ── 极端干旱：沙漠（沙）还是戈壁（砾石/岩漠）──
            if (moisture < AridMax)
            {
                bool rocky = elevation >= GobiElevationMin || slopeMetersPerKm >= GobiSlopeMin;
                return rocky ? TerrainType.Gobi : TerrainType.Desert;
            }

            // ── 半干旱 ──
            if (moisture < SemiAridMax)
            {
                if (elevation >= DryHighlandElevationMin)
                    return TerrainType.Gobi;
                return temperature >= TemperateBandMax
                    ? TerrainType.Savanna
                    : TerrainType.Steppe;
            }

            // ── 半湿润：草原 ──
            if (moisture < SubHumidMax)
            {
                return temperature >= TemperateBandMax
                    ? TerrainType.Savanna
                    : TerrainType.Grassland;
            }

            // ── 湿润：按气温分林型 ──
            if (temperature >= TemperateBandMax)
                return TerrainType.Rainforest;
            if (temperature >= CoolBandMax)
                return TerrainType.Forest;
            return TerrainType.ConiferForest;
        }

        /// <summary>
        /// 该类型是否属于「陆地生物群系」（用于统计与渲染兜底）。
        /// 水体、冰雪不算在内。
        /// </summary>
        public static bool IsLandBiome(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.Coast:
                case TerrainType.Marsh:
                case TerrainType.Tundra:
                case TerrainType.AlpineMeadow:
                case TerrainType.BareRock:
                case TerrainType.ConiferForest:
                case TerrainType.Forest:
                case TerrainType.Rainforest:
                case TerrainType.Savanna:
                case TerrainType.Grassland:
                case TerrainType.Steppe:
                case TerrainType.Desert:
                case TerrainType.Gobi:
                // 旧分类遗留的按海拔命名的类型，仍算陆地
                case TerrainType.Hills:
                case TerrainType.Highland:
                case TerrainType.Plateau:
                case TerrainType.Mountain:
                case TerrainType.HighMountain:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>类型的中文名（日志与统计用）。</summary>
        public static string Name(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.DeepOcean: return "深海";
                case TerrainType.Ocean: return "海洋";
                case TerrainType.ShallowOcean: return "浅海";
                case TerrainType.Coast: return "海岸";
                case TerrainType.Marsh: return "沼泽";
                case TerrainType.Tundra: return "苔原";
                case TerrainType.AlpineMeadow: return "高山草甸";
                case TerrainType.BareRock: return "裸岩";
                case TerrainType.ConiferForest: return "针叶林";
                case TerrainType.Forest: return "温带林";
                case TerrainType.Rainforest: return "雨林";
                case TerrainType.Savanna: return "稀树草原";
                case TerrainType.Grassland: return "草原";
                case TerrainType.Steppe: return "干草原";
                case TerrainType.Desert: return "沙漠";
                case TerrainType.Gobi: return "戈壁";
                case TerrainType.Hills: return "丘陵";
                case TerrainType.Highland: return "高原";
                case TerrainType.Plateau: return "台地";
                case TerrainType.Mountain: return "山地";
                case TerrainType.HighMountain: return "高山";
                case TerrainType.SnowPeak: return "雪峰";
                case TerrainType.Glacier: return "冰川";
                case TerrainType.Lake: return "湖泊";
                case TerrainType.River: return "河流";
                case TerrainType.SeasonalRiver: return "季节河";
                case TerrainType.Delta: return "三角洲";
                case TerrainType.SeaIce: return "海冰";
                default: return type.ToString();
            }
        }
    }
}
