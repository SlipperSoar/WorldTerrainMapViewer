using System;

namespace WorldTerrain
{
    /// <summary>
    /// Classifies terrain types based on elevation, latitude, moisture,
    /// and hydrology data. 依据地图属性「地轴倾角」计算永久雪线与冰盖纬度界限：
    /// 低纬度只有高海拔地区常年积雪，高纬度地区接近海平面即常年积雪，
    /// 极区海面则形成海冰。
    /// </summary>
    public static class TerrainClassifier
    {
        // 8-neighbour offsets (x wraps, y clamps)
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>冰缘噪声的空间频率（单位球坐标）。</summary>
        private const double IceEdgeFrequency = 9.0;

        /// <summary>雪线抖动噪声的空间频率。</summary>
        private const double SnowLineNoiseFrequency = 5.0;

        /// <summary>
        /// Classify every pixel into a TerrainType.
        /// Steps 5–6 of the pipeline.
        /// </summary>
        /// <param name="axialTilt">地图属性：地轴倾角（度），决定冰盖范围与雪线高度</param>
        public static TerrainType[] Classify(
            int width, int height, float[] heightField,
            int[] plateIds, HydrologyData hydrology, SeededNoise noise,
            float axialTilt = (float)PolarClimate.ReferenceTilt)
        {
            int w = width, h = height;
            TerrainType[] types = new TerrainType[w * h];

            // Step 5a: Compute distance-to-sea field (BFS from all ocean pixels)
            int[] seaDist = ClimateModel.SeaDistance(w, h, heightField);

            // Step 5b: 雪线标定基准 —— 本地图最高的陆地海拔。
            // 经验公式给出的是真实海拔（赤道约 5 km），而生成地图的起伏通常只有
            // 1~2 km，因此按「地图最高陆地海拔 / 地球最高陆地海拔」等比标定，
            // 使公式中的纬度、倾角与干湿差异都能在同一比例下体现。
            float maxLandElevation = 0f;
            for (int i = 0; i < heightField.Length; i++)
            {
                if (heightField[i] > maxLandElevation)
                    maxLandElevation = heightField[i];
            }
            double snowLineTransition = PolarClimate.SnowLineTransition(maxLandElevation);
            double snowLineNoiseRange = PolarClimate.SnowLineNoiseAmplitude(maxLandElevation);

            // Step 5c: Classify each pixel
            for (int y = 0; y < h; y++)
            {
                double lat = 90.0 - (double)y / (h - 1) * 180.0;
                double absLat = Math.Abs(lat);

                // 与纬度相关的气候量：每行只需计算一次
                double latitudeIce = PolarClimate.LatitudeIceCoverage(absLat, axialTilt);
                double seaIceCoverage = PolarClimate.SeaIceCoverage(absLat, axialTilt);

                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    float elev = heightField[idx];
                    Vector3D pos = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
                    double moisture = 0.0;   // 陆地湿度（冰盖判定与地形分类共用）

                    // ── 冰盖 1：海冰（高纬度常年冻结的海面）──
                    if (elev <= 0f)
                    {
                        if (seaIceCoverage > 0.0 && IsIceAt(pos, seaIceCoverage, noise))
                        {
                            types[idx] = TerrainType.SeaIce;
                            continue;
                        }
                    }
                    // ── 冰盖 2：极地冰盖与高山冰川（陆地）──
                    else
                    {
                        // 雪线由「纬度 + 地轴倾角」的经验公式给出（见 PolarClimate），
                        // 并按地图自身起伏标定；湿度决定取区间下/上限：
                        // 湿润海洋性山地雪线低，干旱内陆山地雪线高。
                        moisture = ClimateModel.Moisture(absLat, seaDist[idx], noise, pos);
                        double snowLine = PolarClimate.SnowLineElevation(
                            absLat, axialTilt, maxLandElevation, moisture);

                        // 再叠加噪声抖动，使雪盖与冰缘呈自然的不规则形态
                        double snowLineNoise =
                            (noise.Noise3D(pos.x * SnowLineNoiseFrequency,
                                           pos.y * SnowLineNoiseFrequency,
                                           pos.z * SnowLineNoiseFrequency) - 0.5) * 2.0;
                        double localSnowLine = snowLine + snowLineNoise * snowLineNoiseRange;

                        double iceCoverage = Math.Max(
                            latitudeIce,
                            PolarClimate.ElevationIceCoverage(
                                elev, localSnowLine, snowLineTransition));

                        if (iceCoverage > 0.0 && IsIceAt(pos, iceCoverage, noise))
                        {
                            types[idx] = TerrainType.Glacier;
                            continue;
                        }
                    }

                    // ── 水体：湖泊 / 三角洲 / 河流（含季节性河流）──
                    if (hydrology.lakeMask[idx])
                    {
                        types[idx] = TerrainType.Lake;
                        continue;
                    }
                    if (hydrology.deltaMask != null && hydrology.deltaMask[idx])
                    {
                        types[idx] = TerrainType.Delta;
                        continue;
                    }
                    if (hydrology.riverMask[idx])
                    {
                        types[idx] = hydrology.seasonalMask != null && hydrology.seasonalMask[idx]
                            ? TerrainType.SeasonalRiver
                            : TerrainType.River;
                        continue;
                    }

                    // ── Ocean ──
                    if (elev <= 0f)
                    {
                        if (elev < -2000f)
                            types[idx] = TerrainType.DeepOcean;
                        else if (elev < -500f)
                            types[idx] = TerrainType.Ocean;
                        else
                            types[idx] = TerrainType.ShallowOcean;
                        continue;
                    }

                    // ── Step 5: Land classification ──
                    // 湿度已在上面（冰盖判定前）算好，陆地分类直接复用
                    types[idx] = ClassifyLand(elev, absLat, moisture, seaDist[idx]);
                }
            }

            return types;
        }

        /// <summary>
        /// 依据覆盖率与噪声判断该像素是否被常年冰雪覆盖：
        /// 覆盖率为 1 时完全覆盖；过渡带内按噪声形成不规则的冰缘。
        /// </summary>
        private static bool IsIceAt(Vector3D pos, double coverage, SeededNoise noise)
        {
            if (coverage >= 1.0)
                return true;

            double n = noise.Noise3D(
                pos.x * IceEdgeFrequency,
                pos.y * IceEdgeFrequency,
                pos.z * IceEdgeFrequency);
            return n < coverage;
        }

        // ── Private helpers ──

        /// <summary>
        /// Classify land terrain based on elevation, latitude, and moisture.
        /// </summary>
        private static TerrainType ClassifyLand(
            float elev, double absLat, double moisture, int seaDist)
        {
            // Snow peak (highest mountains)
            if (elev > 7000f)
                return TerrainType.SnowPeak;

            // High mountain
            if (elev > 4000f)
                return TerrainType.HighMountain;

            // Mountain
            if (elev > 3000f)
                return TerrainType.Mountain;

            // Desert (low moisture, subtropical)
            if (elev < 2000f && moisture < 0.3 && absLat > 10 && absLat < 40)
                return TerrainType.Desert;

            // Gobi (low moisture, mid-latitude continental interior)
            if (elev > 1000f && elev < 3000f && moisture < 0.25 && absLat > 35 && absLat < 55)
                return TerrainType.Gobi;

            // Tundra (high latitude)
            if (elev < 1500f && absLat > 55)
                return TerrainType.Tundra;

            // Rainforest (high moisture, equatorial)
            if (elev < 1000f && moisture > 0.65 && absLat < 15)
                return TerrainType.Rainforest;

            // Desert plateau (low moisture, high elevation)
            if (elev > 2000f && elev <= 4000f && moisture < 0.2)
                return TerrainType.Gobi;

            // Plateau
            if (elev > 2000f && elev <= 3000f)
                return TerrainType.Plateau;

            // Highland
            if (elev > 1000f && elev <= 2000f)
                return TerrainType.Highland;

            // Hills
            if (elev > 500f && elev <= 1000f)
                return TerrainType.Hills;

            // Forest (moderate moisture, low-mid elevation)
            if (elev > 200f && elev <= 500f && moisture > 0.5)
                return TerrainType.Forest;

            // Grassland
            if (elev > 200f && elev <= 500f)
                return TerrainType.Grassland;

            // Coast / lowland
            return TerrainType.Coast;
        }
    }
}
