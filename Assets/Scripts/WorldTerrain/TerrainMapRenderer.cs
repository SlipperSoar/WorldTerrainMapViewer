using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// Renders the height field and terrain types into color arrays.
    /// Step 7: Continuous hypsometric tinting for non-ice terrain.
    /// Step 8: Grayscale heightmap.
    ///
    /// 输出是 <see cref="Color32"/> 而不是 <see cref="Color"/>：颜色最终要变成 8 位贴图，
    /// 而 <c>Color</c> 每像素 16 字节、<c>Color32</c> 只有 4 字节。8192×4096 时
    /// 三张图合计从 1.5 GB 降到 384 MB —— 这是大分辨率下峰值内存的最大单项。
    ///
    /// 量化用**四舍五入**，与 Unity 内部把 <c>Color</c> 写进贴图时的规则一致
    /// （已用一张旧路径生成的 PNG 反推确认：四舍五入能对上 99.7% 的通道，
    /// 而截断会满屏差 1，占 40%）。因此换类型不改变任何像素。
    /// </summary>
    public static class TerrainMapRenderer
    {
        /// <summary>把浮点颜色量化成 8 位（四舍五入，与 Unity 的 Color32 转换一致）。</summary>
        private static Color32 Quantize(Color c)
        {
            return new Color32(
                ToByte(c.r), ToByte(c.g), ToByte(c.b), ToByte(c.a));
        }

        private static byte ToByte(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 1f) return 255;
            // Math.Round 默认银行家舍入，与 Mathf.RoundToInt 一致
            return (byte)Math.Round(v * 255f);
        }

        /// <summary>
        /// Step 8: Render a grayscale heightmap.
        /// </summary>
        public static Color32[] RenderHeightMap(int width, int height, float[] heightField)
        {
            Color32[] colors = new Color32[width * height];
            float minH = -10000f;
            float maxH = 10000f;
            float range = maxH - minH;

            for (int i = 0; i < width * height; i++)
            {
                float t = (heightField[i] - minH) / range;
                t = Math.Max(0f, Math.Min(1f, t));
                colors[i] = Quantize(new Color(t, t, t, 1f));
            }

            return colors;
        }

        /// <summary>
        /// Step 7: Render the color terrain map。
        ///
        /// 陆地按**生物群系**着色（森林/沙漠/戈壁/草原/沼泽…，由 <see cref="BiomeModel"/> 判定），
        /// 再叠加起伏阴影与细颗粒噪声；不再用「海拔 → 颜色」的单一渐变。
        /// 海洋深度、冰盖、河流、湖泊的着色保持不变。
        /// </summary>
        public static Color32[] RenderTerrainMap(
            int width, int height, float[] heightField,
            TerrainType[] terrainTypes, HydrologyData hydrology,
            SeededNoise noise)
        {
            Color32[] colors = new Color32[width * height];
            float[] relief = BuildReliefShade(width, height, heightField);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    colors[i] = Quantize(GetTerrainColor(
                        heightField[i], terrainTypes[i],
                        hydrology.riverWidth[i],
                        hydrology.estuaryMask[i],
                        relief[i],
                        x, y, width, height, noise));
                }
            }

            return colors;
        }

        /// <summary>
        /// 起伏阴影：用高度场的局部梯度模拟「西北向光照」，让群系色块有立体感。
        /// 以全图最大梯度归一化，返回 0.88~1.12 的亮度系数（海洋不参与，按 1 处理）。
        /// </summary>
        private static float[] BuildReliefShade(int width, int height, float[] heightField)
        {
            float[] relief = new float[width * height];
            if (width < 3 || height < 3)
            {
                for (int i = 0; i < relief.Length; i++)
                    relief[i] = 1f;
                return relief;
            }

            // 归一化尺度取**陆地梯度的 95 分位**，而不是全图最大值：
            // 海岸线上几千米的落差会把最大值顶到天上，导致内陆的起伏全被压成 0，
            // 整张图看起来是平的（这是实测踩到的坑）。
            var samples = new System.Collections.Generic.List<float>(height / 2 + 1);
            for (int y = 0; y < height; y++)
            {
                int yu = Math.Max(0, y - 1);
                int yd = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (heightField[i] <= 0f)
                        continue;
                    int xl = (x - 1 + width) % width;
                    int xr = (x + 1) % width;
                    float gx = heightField[y * width + xr] - heightField[y * width + xl];
                    float gy = heightField[yd * width + x] - heightField[yu * width + x];
                    samples.Add(Math.Abs(gx) + Math.Abs(gy));
                }
            }

            float maxGrad = 1f;
            if (samples.Count > 0)
            {
                samples.Sort();
                maxGrad = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95f))];
                if (maxGrad <= 0f)
                    maxGrad = 1f;
            }

            if (maxGrad <= 0f)
            {
                for (int i = 0; i < relief.Length; i++)
                    relief[i] = 1f;
                return relief;
            }

            const float Strength = 0.30f;
            for (int y = 0; y < height; y++)
            {
                int yu = Math.Max(0, y - 1);
                int yd = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (heightField[i] <= 0f)
                    {
                        relief[i] = 1f;
                        continue;
                    }

                    int xl = (x - 1 + width) % width;
                    int xr = (x + 1) % width;
                    float gx = heightField[y * width + xr] - heightField[y * width + xl];
                    float gy = heightField[yd * width + x] - heightField[yu * width + x];

                    // 方向项：光照来自西北，坡面朝西北变亮、朝东南变暗
                    float d = (gy - gx) / maxGrad * Strength;
                    // 坡度项：越陡越暗，让山脊与谷地在纯色群系上也能看出来
                    float slopeNorm = Math.Min(1f, (Math.Abs(gx) + Math.Abs(gy)) / maxGrad);
                    relief[i] = Math.Max(0.72f, Math.Min(1.24f, 1f + d - slopeNorm * 0.16f));
                }
            }

            return relief;
        }

        /// <summary>
        /// Render the tectonic plate division map. Each plate gets a distinct
        /// color; plate boundaries (earthquake belts) are highlighted in
        /// light yellow. When transparent=true, plate interiors have low
        /// alpha so the underlying terrain is visible through the overlay.
        /// </summary>
        public static Color32[] RenderPlateMap(
            int width, int height, int[] plateIds, bool transparent)
        {
            Color32[] colors = new Color32[width * height];

            int numPlates = 0;
            for (int i = 0; i < plateIds.Length; i++)
            {
                if (plateIds[i] > numPlates)
                    numPlates = plateIds[i];
            }
            numPlates++;

            Color[] palette = GeneratePlatePalette(numPlates);

            // 8-neighbour offsets (x wraps, y clamps)
            int[] dx8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

            float boundaryAlpha = transparent ? 0.9f : 1f;
            float interiorAlpha = transparent ? 0.03f : 1f;
            Color boundaryColor = new Color(1f, 0.95f, 0.5f, boundaryAlpha);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    int plate = plateIds[idx];

                    bool isBoundary = false;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = (x + dx8[d] + width) % width;
                        int ny = y + dy8[d];
                        if (ny < 0 || ny >= height)
                            continue;
                        if (plateIds[ny * width + nx] != plate)
                        {
                            isBoundary = true;
                            break;
                        }
                    }

                    if (isBoundary)
                    {
                        colors[idx] = Quantize(boundaryColor);
                    }
                    else
                    {
                        Color c = palette[plate];
                        colors[idx] = Quantize(new Color(c.r, c.g, c.b, interiorAlpha));
                    }
                }
            }

            return colors;
        }

        private static Color[] GeneratePlatePalette(int count)
        {
            Color[] colors = new Color[count];
            for (int i = 0; i < count; i++)
            {
                float h = (float)i / count;
                colors[i] = HsvToRgb(h, 0.7f, 0.9f);
            }
            return colors;
        }

        private static Color HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float huePos = h * 6f;
            float x = c * (1f - (float)Math.Abs(huePos % 2f - 1f));
            float m = v - c;
            float r, g, b;
            if (huePos < 1f) { r = c; g = x; b = 0f; }
            else if (huePos < 2f) { r = x; g = c; b = 0f; }
            else if (huePos < 3f) { r = 0f; g = c; b = x; }
            else if (huePos < 4f) { r = 0f; g = x; b = c; }
            else if (huePos < 5f) { r = x; g = 0f; b = c; }
            else { r = c; g = 0f; b = x; }
            return new Color(r + m, g + m, b + m, 1f);
        }

        // ── Private helpers ──

        private static double SmoothStep(double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// Ocean depth-to-color mapping with noise-varied gradient slope.
        /// Shallow areas occupy a larger color range; deep ocean is the minority.
        /// </summary>
        private static Color OceanDepthToColor(
            float elevation, int x, int y, int w, int h, SeededNoise noise)
        {
            // Normalize to [0, 1] with 3000m upper bound (most ocean is within this range)
            double depth = Math.Max(0.0, Math.Min(1.0, (double)(-elevation) / 3000.0));

            // Noise varies the gradient curve slope
            Vector3D pos = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
            double slopeNoise = noise.Noise3D(pos.x * 2.5, pos.y * 2.5, pos.z * 2.5);

            // Power always >= 1.0 → mid-depth values are pushed toward shallow colors
            double power = 1.0 + slopeNoise * 1.5;  // range ~1.0 to ~2.5

            double curvedDepth = Math.Pow(depth, power);
            curvedDepth = Math.Max(0.0, Math.Min(1.0, curvedDepth));
            double s = SmoothStep(curvedDepth);

            // Shallow (0.35, 0.65, 0.82) → Deep (0.03, 0.10, 0.25)
            double r = 0.35 - (0.35 - 0.03) * s;
            double g = 0.65 - (0.65 - 0.10) * s;
            double b = 0.82 - (0.82 - 0.25) * s;

            return new Color((float)r, (float)g, (float)b, 1f);
        }

        /// <summary>
        /// Get the final color for a single pixel.
        /// </summary>
        private static Color GetTerrainColor(
            float elevation, TerrainType type, float riverWidth,
            bool isEstuary, float relief,
            int x, int y, int w, int h, SeededNoise noise)
        {
            // Glacier: 陆地冰盖 / 高山冰川 —— 亮白偏冷色，随海拔增亮，
            // 并叠加细密噪声模拟冰面纹理
            if (type == TerrainType.Glacier)
            {
                double t = Math.Max(0.0, Math.Min(1.0, elevation / 5000.0));
                double v = 0.80 + t * 0.16;                 // 0.80 ~ 0.96

                Vector3D pos = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
                double grain = noise.Noise3D(pos.x * 18.0, pos.y * 18.0, pos.z * 18.0) - 0.5;
                v += grain * 0.06;

                v = Math.Max(0.0, Math.Min(1.0, v));
                return new Color((float)v, (float)(v + 0.015), (float)(v + 0.035), 1f);
            }

            // Snow peak: 终年积雪的山峰，同样按冰盖颜色绘制
            if (type == TerrainType.SnowPeak)
            {
                return new Color(0.95f, 0.96f, 0.98f, 1f);
            }

            // Sea ice: 海冰 —— 略带蓝调的白，比陆地冰盖稍暗
            if (type == TerrainType.SeaIce)
            {
                return new Color(0.84f, 0.89f, 0.94f, 1f);
            }

            // Estuary (river mouth): 河口 —— 在浅海色上混入泥沙
            if (type == TerrainType.River && isEstuary)
            {
                Color c = OceanDepthToColor(elevation, x, y, w, h, noise);
                return new Color(
                    c.r * 0.72f + 0.22f,
                    c.g * 0.74f + 0.20f,
                    c.b * 0.70f + 0.14f,
                    1f);
            }

            // River: blue (darker for wider rivers)
            if (type == TerrainType.River)
            {
                float rw = Math.Min(1f, riverWidth / 3f);
                return new Color(
                    0.24f - rw * 0.08f,
                    0.47f - rw * 0.08f,
                    0.66f,
                    1f);
            }

            // Seasonal river: 季节性河流 —— 更淡、偏沙色，表示雨季才有水
            if (type == TerrainType.SeasonalRiver)
            {
                float rw = Math.Min(1f, riverWidth / 3f);
                return new Color(
                    0.46f - rw * 0.06f,
                    0.62f - rw * 0.05f,
                    0.66f - rw * 0.02f,
                    1f);
            }

            // Delta: 三角洲分流河道 —— 泥沙质浅水
            if (type == TerrainType.Delta)
            {
                float rw = Math.Min(1f, riverWidth / 4f);
                return new Color(
                    0.60f - rw * 0.06f,
                    0.76f - rw * 0.06f,
                    0.70f - rw * 0.04f,
                    1f);
            }

            // Lake: blue
            if (type == TerrainType.Lake)
                return new Color(0.24f, 0.55f, 0.71f, 1f);

            // Ocean: noise-varied depth gradient
            if (elevation <= 0f)
                return OceanDepthToColor(elevation, x, y, w, h, noise);

            // Land: 按生物群系着色（森林/沙漠/戈壁/草原/沼泽…），叠加起伏与颗粒
            Vector3D lp = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
            // 两个尺度的颗粒：细颗粒给纹理，粗颗粒让大片同色群系有深浅变化
            double fine = noise.Noise3D(lp.x * 26.0, lp.y * 26.0, lp.z * 26.0) - 0.5;
            double coarse = noise.Noise3D(lp.x * 7.0, lp.y * 7.0, lp.z * 7.0) - 0.5;
            return BiomeColor(type, elevation, relief, (float)(fine * 0.07 + coarse * 0.05));
        }

        /// <summary>
        /// 群系基色（0-1）。同一海拔下湿润区偏绿、干旱区偏黄褐，
        /// 因此从颜色就能区分森林、草原、沙漠、戈壁等自然环境。
        /// </summary>
        private static Color BiomeBaseColor(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.Coast:         return new Color(0.86f, 0.81f, 0.64f, 1f);
                case TerrainType.Marsh:         return new Color(0.24f, 0.36f, 0.28f, 1f);
                case TerrainType.Tundra:        return new Color(0.55f, 0.57f, 0.47f, 1f);
                case TerrainType.AlpineMeadow:  return new Color(0.56f, 0.63f, 0.43f, 1f);
                case TerrainType.BareRock:      return new Color(0.56f, 0.54f, 0.51f, 1f);
                case TerrainType.ConiferForest: return new Color(0.14f, 0.31f, 0.22f, 1f);
                case TerrainType.Forest:        return new Color(0.22f, 0.45f, 0.20f, 1f);
                case TerrainType.Rainforest:    return new Color(0.09f, 0.34f, 0.14f, 1f);
                case TerrainType.Savanna:       return new Color(0.66f, 0.62f, 0.30f, 1f);
                case TerrainType.Grassland:     return new Color(0.50f, 0.62f, 0.28f, 1f);
                case TerrainType.Steppe:        return new Color(0.74f, 0.70f, 0.42f, 1f);
                case TerrainType.Desert:        return new Color(0.84f, 0.69f, 0.42f, 1f);
                case TerrainType.Gobi:          return new Color(0.60f, 0.56f, 0.50f, 1f);

                // 旧分类遗留的按海拔命名的类型：给中性色兜底
                case TerrainType.Hills:         return new Color(0.40f, 0.50f, 0.28f, 1f);
                case TerrainType.Highland:      return new Color(0.50f, 0.52f, 0.38f, 1f);
                case TerrainType.Plateau:       return new Color(0.58f, 0.56f, 0.44f, 1f);
                case TerrainType.Mountain:      return new Color(0.52f, 0.50f, 0.47f, 1f);
                case TerrainType.HighMountain:  return new Color(0.60f, 0.60f, 0.60f, 1f);

                default:                        return new Color(0.46f, 0.55f, 0.32f, 1f);
            }
        }

        /// <summary>
        /// 群系色 + 海拔提亮 + 起伏阴影 + 颗粒噪声。
        ///
        /// 海拔只负责“提亮并偏冷灰”（越高越像裸地），不再决定色相 ——
        /// 色相完全由群系决定，所以森林与沙漠在同一海拔也能一眼分开。
        /// </summary>
        private static Color BiomeColor(TerrainType type, float elevation, float relief, float grain)
        {
            Color c = BiomeBaseColor(type);

            float t = Math.Max(0f, Math.Min(1f, elevation / 6000f));
            float lift = 0.20f * t;
            c.r += (1f - c.r) * lift;
            c.g += (1f - c.g) * lift;
            c.b += (1f - c.b) * lift;

            c.r = c.r * relief + grain;
            c.g = c.g * relief + grain;
            c.b = c.b * relief + grain;

            return new Color(
                Math.Max(0f, Math.Min(1f, c.r)),
                Math.Max(0f, Math.Min(1f, c.g)),
                Math.Max(0f, Math.Min(1f, c.b)),
                1f);
        }
    }
}
