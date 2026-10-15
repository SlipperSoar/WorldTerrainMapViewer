using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 水文生成：Priority-Flood 填洼 + 汇流累积，得到接近真实水系的河网。
    ///
    /// 流程：
    ///   1. 湿度场（季节性河流与沿途渗漏判定）与最高陆地海拔
    ///   2. Priority-Flood 填洼，同时确定每格流向 —— 保证任何一格都有一条
    ///      单调下降的路径入海，因此河流不会「走投无路」或倒流
    ///   3. 湖泊识别：被填高的洼地连通域
    ///   4. 汇流累积：高山径流加成、干旱区沿途渗漏、低坡度区分流
    ///   5. 提取河道并按流量计算河宽 —— 支流汇合变宽、主干分流变窄
    ///   6. 河口三角洲：分流河道呈扇形铺入浅海
    ///   7. 按宽度加粗河道，使干流在图上真正比支流宽
    ///
    /// 仅使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class HydrologyGenerator
    {
        // 8 邻域（x 环绕，y 钳制）
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// Priority-Flood 的抬升步长（米）。它只用于保证沿流向水面严格递增
        /// （汇流累积需要），取得足够小以免长距离平坦河段被误判成湖泊。
        /// </summary>
        private const float FillEpsilon = 0.001f;

        /// <summary>
        /// 湖泊门槛：只保留「水深足够」的洼地作为候选，是否真的积水交给水量平衡概率决定
        /// （见 <see cref="MarkLakes"/>）—— 于是大盆地不一定是湖，小洼地也不一定是坑。
        /// </summary>
        private const float MinLakeDepth = 6f;
        private const float MinLakeCellDepth = 2f;

        /// <summary>候选湖泊的最小面积（赤道等效格数，见 <see cref="MarkLakes"/> 的面积口径）。</summary>
        private const int MinLakeCellsAbsolute = 3;

        /// <summary>
        /// 「实际积水面积」的放宽门槛：盆地的面积口径与其蓄水后的等效格数
        /// 会因为浮点累加差一点点，门槛贴着 <see cref="MinLakeCellsAbsolute"/> 会误杀边缘盆地。
        /// 取一半已经足够滤掉"水位抬起来但没淹到任何格"的情况。
        /// </summary>
        private const float MinLakeMarkedArea = MinLakeCellsAbsolute * 0.5f;

        /// <summary>「够大」的参照格数：达到这个规模时，水量条件基本决定成败。</summary>
        private const float LakeSizeReference = 400f;

        /// <summary>成湖概率的总体增益（越大湖越多）。</summary>
        private const float LakeProbabilityGain = 0.95f;

        /// <summary>湖面蓄水比例的范围：湿润区接近灌满，干旱区只蓄一部分（季节性/盐湖）。</summary>
        private const float LakeFillMin = 0.35f;
        private const float LakeFillMax = 1.0f;

        /// <summary>高山径流加成：山顶相对海平面多出的径流比例。</summary>
        private const float OrographicBonus = 1.6f;

        /// <summary>径流的气候系数：产流 = 面积 × 地形加成 × (BaseRunoff + (1-BaseRunoff)·湿度)。</summary>
        private const float BaseRunoff = 0.10f;

        /// <summary>汇流排序键的扰动幅度（米）：只影响平坦区的流向排序。</summary>
        private const float RoutingJitterMeters = 8f;

        /// <summary>河道阈值：占全部陆地汇水权重的比例（控制河网密度）。</summary>
        private const float RiverThresholdRatio = 0.00035f;

        /// <summary>
        /// 河流起始宽度、宽度增益与流量指数（宽度 = MinRiverWidth + WidthGain·((Q/Q₀)^指数 - 1)）。
        /// 增益越小，河道随流量变宽越慢（支流更久地保持细窄）。
        /// </summary>
        private const float MinRiverWidth = 0.7f;
        private const float WidthGain = 1.25f;
        private const double WidthExponent = 0.52;

        /// <summary>最大河宽（像素）。</summary>
        private const float MaxRiverWidth = 12f;

        /// <summary>低坡度分流：坡度阈值（米/格）与分流比例范围。</summary>
        private const float BifurcationSlope = 8f;
        private const float BifurcationMinShare = 0.18f;
        private const float BifurcationMaxShare = 0.42f;

        /// <summary>干旱区沿途渗漏：每格保留的流量比例。</summary>
        private const float AridRetention = 0.90f;
        private const float VeryAridRetention = 0.76f;

        /// <summary>三角洲可铺开的浅海深度上限（米）。</summary>
        private const float DeltaMaxDepth = 900f;

        /// <summary>
        /// 生成河流、湖泊与三角洲。
        /// </summary>
        /// <param name="heightField">高度场（米，海平面为 0）</param>
        /// <param name="noise">噪声（用于湿度场）</param>
        /// <param name="onProgress">阶段进度回调（0-1），可为 null</param>
        /// <param name="moistureField">预先算好的湿度场（风带 + 雨影），null 时按纬度/距海自行计算</param>
        /// <param name="runoffField">
        /// 球面生成域的径流场（<see cref="SphereTerrainResult.Runoff"/>，已投影到本网格，长度 ≥ w×h）。
        /// 传入时河道判据用它（球面等面积格点上的真实汇流），平面汇流累积只用来决定河宽；
        /// 为 null 时退回平面判据（矩形生成域或外部高度图）。
        /// </param>
        /// <param name="runoffChannelThreshold">配合 <paramref name="runoffField"/> 的河道阈值分位（<see cref="SphereTerrainResult.RunoffChannelThreshold"/>）</param>
        /// <param name="axialTilt">地轴倾角（度）：湖泊判定里没有气候场时的回退剖面用它</param>
        public static HydrologyData Generate(int width, int height, float[] heightField,
            SeededNoise noise, Action<float> onProgress = null, float[] moistureField = null,
            float[] runoffField = null, float runoffChannelThreshold = 0f,
            float axialTilt = (float)PolarClimate.ReferenceTilt)
        {
            int w = width, h = height, n = w * h;

            var data = new HydrologyData
            {
                riverMask = new bool[n],
                lakeMask = new bool[n],
                riverWidth = new float[n],
                estuaryMask = new bool[n],
                seasonalMask = new bool[n],
                deltaMask = new bool[n],
                discharge = new float[n],
                waterDepth = new float[n],
                catchmentMoisture = new float[n]
            };

            // ── 1. 湿度场 ──
            onProgress?.Invoke(0.05f);
            bool useProvidedMoisture = moistureField != null && moistureField.Length >= n;
            int[] seaDist = useProvidedMoisture ? null : ClimateModel.SeaDistance(w, h, heightField);

            // 没有提供湿度场时，按纬度预计算每行的基础湿度（供逐格复用）
            float[] rowMoistureBase = null;
            if (!useProvidedMoisture)
            {
                rowMoistureBase = new float[h];
                for (int y = 0; y < h; y++)
                {
                    double absLatY = Math.Abs(90.0 - (double)y / (h - 1) * 180.0);
                    rowMoistureBase[y] = (float)ClimateModel.SeaSourceMoisture(absLatY);
                }
            }

            double[] rowCosLat = new double[h];
            double[] rowSinLat = new double[h];
            double[] colCosLon = new double[w];
            double[] colSinLon = new double[w];
            for (int y = 0; y < h; y++)
            {
                double lat = Math.PI * 0.5 - (double)y / (h - 1) * Math.PI;
                rowCosLat[y] = Math.Cos(lat);
                rowSinLat[y] = Math.Sin(lat);
            }
            for (int x = 0; x < w; x++)
            {
                double lon = (double)x / (w - 1) * 2.0 * Math.PI - Math.PI;
                colCosLon[x] = Math.Cos(lon);
                colSinLon[x] = Math.Sin(lon);
            }

            float[] moisture = useProvidedMoisture ? moistureField : new float[n];
            float[] jitter = new float[n];
            float maxLandElevation = 0f;
            for (int y = 0; y < h; y++)
            {
                double absLat = Math.Abs(90.0 - (double)y / (h - 1) * 180.0);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float elev = heightField[i];
                    if (elev > maxLandElevation)
                        maxLandElevation = elev;

                    var pos = new Vector3D(rowCosLat[y] * colCosLon[x], rowSinLat[y],
                                           rowCosLat[y] * colSinLon[x]);
                    if (!useProvidedMoisture)
                        moisture[i] = (float)ClimateModel.MoistureFromBase(
                            rowMoistureBase[y], seaDist[i], noise, pos);

                    // 汇流用的微起伏：只在 Priority-Flood 的排序键上叠加很小的相干噪声，
                    // 用来打散 D8 流向在平坦地形上产生的网格状直河道；
                    // 水面高程仍由真实地形决定，因此不会凭空造出湖泊。
                    jitter[i] = elev > 0f
                        ? (float)((noise.Noise3D(pos.x * 8.0, pos.y * 8.0, pos.z * 8.0) - 0.5)
                                  * 2.0 * RoutingJitterMeters)
                        : 0f;
                }
            }

            // 本地径流总量 → 河网阈值（地图越大、陆地越多，河道阈值越高）
            float totalRunoff = 0f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (heightField[i] > 0f)
                        totalRunoff += (float)rowCosLat[y]
                            * OrographicWeight(heightField[i], maxLandElevation)
                            * RunoffFactor(moisture[i]);
                }
            }
            float riverThreshold = Math.Max(6f, totalRunoff * RiverThresholdRatio);

            // ── 2. Priority-Flood：填洼 + 流向 ──
            onProgress?.Invoke(0.15f);
            float[] filled = new float[n];
            int[] flowTo = new int[n];
            int[] order = new int[n];
            int orderCount = PriorityFlood(w, h, heightField, jitter, filled, flowTo, order);

            // 汇流累积必须严格按水面高程从高到低处理。Priority-Flood 的弹出顺序
            // 带排序扰动，因此这里按 filled 重新排序（沿流向 filled 严格递增，
            // 故按 filled 排序一定是合法的拓扑序）。
            int[] flowOrder = new int[orderCount];
            float[] flowKeys = new float[orderCount];
            for (int k = 0; k < orderCount; k++)
            {
                flowOrder[k] = order[k];
                flowKeys[k] = filled[order[k]];
            }
            Array.Sort(flowKeys, flowOrder);

            // ── 3. 湖泊 ──
            onProgress?.Invoke(0.35f);

            // 没有气候场时按纬度预计算每行基础湿度（供逐格复用；用地图自身的地轴倾角）
            float[] rowLakeMoisture = rowMoistureBase;
            if (rowLakeMoisture == null)
            {
                rowLakeMoisture = new float[h];
                for (int y = 0; y < h; y++)
                {
                    double absLatY = Math.Abs(90.0 - (double)y / (h - 1) * 180.0);
                    rowLakeMoisture[y] = (float)ClimateModel.SeaSourceMoisture(absLatY, axialTilt);
                }
            }

            MarkLakes(w, h, heightField, filled, data.lakeMask, data.waterDepth,
                noise, moistureField, rowLakeMoisture);

            // ── 4. 汇流累积（含分流与渗漏） ──
            onProgress?.Invoke(0.45f);
            float[] accum = new float[n];
            float[] moistFlux = new float[n];
            Accumulate(w, h, heightField, maxLandElevation, moisture, rowCosLat,
                filled, flowTo, flowOrder, orderCount, riverThreshold, accum, moistFlux);

            // ── 5. 河道提取与河宽 ──
            onProgress?.Invoke(0.70f);
            ExtractRivers(w, h, heightField, riverThreshold, accum, moistFlux, moisture, data,
                runoffField, runoffChannelThreshold);

            // ── 6. 河口三角洲 ──
            onProgress?.Invoke(0.80f);
            CarveDeltas(w, h, heightField, flowTo, data);

            // ── 7. 按宽度加粗河道 ──
            onProgress?.Invoke(0.90f);
            WidenRivers(w, h, heightField, data);

            onProgress?.Invoke(1f);
            return data;
        }

        // ── 1. 地形与径流 ──

        /// <summary>高山降雨/融雪加成：相对高度越高，本地径流越多。</summary>
        private static float OrographicWeight(float elevation, float maxLandElevation)
        {
            if (maxLandElevation <= 1f || elevation <= 0f)
                return 1f;
            float t = elevation / maxLandElevation;
            if (t > 1f) t = 1f;
            return 1f + OrographicBonus * t * t;
        }

        /// <summary>
        /// 产流的气候系数：湿润地区降水多、产流多；干旱地区产流很少
        /// （因此沙漠中的河道大多靠上游来水，也更容易成为季节性河流）。
        /// </summary>
        private static float RunoffFactor(float moisture)
        {
            float m = moisture;
            if (m < 0f) m = 0f;
            if (m > 1f) m = 1f;
            return BaseRunoff + (1f - BaseRunoff) * m;
        }

        // ── 2. Priority-Flood ──

        /// <summary>
        /// 从海洋出发的 Priority-Flood：把洼地填平并记录每格流向，
        /// 返回处理顺序（filled 升序），供后续按「高→低」做汇流累积。
        /// </summary>
        /// <param name="jitter">仅用于排序键的微小扰动（打散平坦区的网格状河道），不参与水面高程</param>
        private static int PriorityFlood(int w, int h, float[] heightField, float[] jitter,
            float[] filled, int[] flowTo, int[] order)
        {
            int n = w * h;
            for (int i = 0; i < n; i++)
                flowTo[i] = -1;

            float[] keys = new float[n];
            var heap = new IndexMinHeap(keys);
            bool[] queued = new bool[n];

            int seeds = 0;
            for (int i = 0; i < n; i++)
            {
                if (heightField[i] <= 0f)
                {
                    filled[i] = heightField[i];
                    keys[i] = filled[i] + jitter[i];
                    queued[i] = true;
                    heap.Push(i);
                    seeds++;
                }
            }

            // 全陆地地图：从最低点开始排水
            if (seeds == 0)
            {
                int lowest = 0;
                for (int i = 1; i < n; i++)
                    if (heightField[i] < heightField[lowest])
                        lowest = i;
                filled[lowest] = heightField[lowest];
                keys[lowest] = filled[lowest] + jitter[lowest];
                queued[lowest] = true;
                heap.Push(lowest);
            }

            int count = 0;
            while (heap.Count > 0)
            {
                int c = heap.Pop();
                order[count++] = c;

                int cx = c % w, cy = c / w;
                float level = filled[c] + FillEpsilon;

                for (int d = 0; d < 8; d++)
                {
                    int ny = cy + DY8[d];
                    if (ny < 0 || ny >= h)
                        continue;

                    int j = ny * w + ((cx + DX8[d] + w) % w);
                    if (queued[j])
                        continue;

                    filled[j] = heightField[j] > level ? heightField[j] : level;
                    keys[j] = filled[j] + jitter[j];
                    flowTo[j] = c;
                    queued[j] = true;
                    heap.Push(j);
                }
            }

            return count;
        }

        // ── 3. 湖泊 ──

        /// <summary>
        /// 被 Priority-Flood 抬高的洼地即候选湖泊，但**不是够大就一定是湖**：
        /// 由「水量平衡」决定成湖概率 ——
        ///   湿润度（当地降水/汇水）越高、盆地越大越深 → 越可能是湖；
        ///   干旱区的大盆地常常只是干盆地（盐湖/滩地），湿润区的小洼地也常蓄成小湖。
        /// 再叠一层与地图种子绑定的稳定随机，于是湖的「有大有小」是自然分布，
        /// 而不是一刀切的门槛。蓄水比例也会随湿润度变化，同一盆地可大湖可小湖。
        ///
        /// 门槛按**真实球面面积**算，而不是像素格数：等距圆柱图上一个像素的真实面积
        /// 随纬度按 cos(lat) 收缩，同一个「格数」门槛在高纬实际放行的物理面积只有赤道的
        /// 四分之一 —— 会凭空放大高纬的湖（实测 75°N 的湖泊密度是中低纬的 4 倍）。
        /// 现在把盆地面积按行权重（cos lat）折算成「赤道等效格数」，门槛才对所有纬度一致。
        /// </summary>
        private static void MarkLakes(int w, int h, float[] heightField,
            float[] filled, bool[] lakeMask, float[] waterDepth,
            SeededNoise noise, float[] moistureField, float[] rowMoistureBase)
        {
            int n = w * h;

            int seedSalt = (int)(noise.Noise3D(12.34, 56.78, 90.12) * 1000000.0);

            // 没有气候场时的逐行兜底湿度（由 Generate 预计算好，避免逐格求解经验公式）
            float[] rowFallback = moistureField == null ? rowMoistureBase : null;

            bool[] pending = new bool[n];
            for (int i = 0; i < n; i++)
                pending[i] = heightField[i] > 0f && filled[i] > heightField[i] + FillEpsilon * 2f;

            var stack = new Stack<int>();
            var component = new List<int>();

            // 每行的面积权重：等距圆柱图上一格的球面面积 ∝ cos(纬度)。
            // 用行号而不是 y 反推纬度，是为了和 heightField 的行定义严格对齐。
            var rowArea = new float[h];
            for (int y = 0; y < h; y++)
            {
                double lat = Math.PI * 0.5 - (double)y / (h - 1) * Math.PI;
                rowArea[y] = (float)Math.Cos(lat);
                if (rowArea[y] < 0.01f)
                    rowArea[y] = 0.01f;              // 极点行只占极小面积，别让它归零
            }

            for (int start = 0; start < n; start++)
            {
                if (!pending[start])
                    continue;

                component.Clear();
                stack.Push(start);
                pending[start] = false;

                float maxDepth = 0f;
                int spillIdx = start;
                double areaCells = 0.0;              // 赤道等效格数（真实面积 / 赤道一格面积）
                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    component.Add(idx);
                    areaCells += rowArea[idx / w];

                    float depth = filled[idx] - heightField[idx];
                    if (depth > maxDepth)
                    {
                        maxDepth = depth;
                        spillIdx = idx;
                    }

                    int x = idx % w, y = idx / w;
                    for (int d = 0; d < 8; d++)
                    {
                        int ny = y + DY8[d];
                        if (ny < 0 || ny >= h)
                            continue;
                        int j = ny * w + ((x + DX8[d] + w) % w);
                        if (pending[j])
                        {
                            pending[j] = false;
                            stack.Push(j);
                        }
                    }
                }

                if (areaCells < MinLakeCellsAbsolute || maxDepth < MinLakeDepth)
                    continue;

                // ── 水量平衡：盆地能存住多少水 ──

                // 流域湿润度：盆地自身及其周边（用格子的平均湿度近似汇水区的降水）
                double wetSum = 0.0;
                for (int k = 0; k < component.Count; k++)
                    wetSum += CellMoisture(w, component[k], moistureField, rowFallback);

                float wet = (float)(wetSum / component.Count);

                // 规模：面积越大，汇水区越大（对数尺度，避免巨型盆地压倒一切）
                float sizeNorm = (float)(Math.Log(areaCells / MinLakeCellsAbsolute)
                                       / Math.Log(LakeSizeReference / MinLakeCellsAbsolute));
                if (sizeNorm < 0f) sizeNorm = 0f;
                if (sizeNorm > 1f) sizeNorm = 1f;

                // 深度也会有帮助：越深越不容易被蒸发干
                float deepNorm = (maxDepth - MinLakeCellDepth) / 60f;
                if (deepNorm < 0f) deepNorm = 0f;
                if (deepNorm > 1f) deepNorm = 1f;

                float supply = 0.05f + 0.95f * wet + 0.15f * deepNorm;
                float probability = supply * (0.25f + 0.95f * sizeNorm) * LakeProbabilityGain;
                if (probability > 1f) probability = 1f;

                float dice = Hash01(spillIdx, seedSalt);
                if (dice >= probability)
                    continue;                        // 干盆地：不积水，仍是平坦的洼地

                // ── 蓄水高度：湿润区接近灌满，干旱区只蓄一部分，于是湖面有大有小 ──
                float fillDice = Hash01(spillIdx ^ 0x5F3759DF, seedSalt);
                float fill = LakeFillMin + (LakeFillMax - LakeFillMin) * (0.55f * wet + 0.45f * fillDice);
                if (fill > LakeFillMax) fill = LakeFillMax;

                // 至少要淹过最深处，否则这个湖会凭空消失
                float minFill = (MinLakeCellDepth + 0.5f) / Math.Max(maxDepth, 0.5f);
                if (fill < minFill) fill = minFill;
                if (fill > 1f) fill = 1f;

                float waterLevel = heightField[spillIdx] + maxDepth * fill;
                double markedArea = 0.0;
                for (int k = 0; k < component.Count; k++)
                {
                    int idx = component[k];
                    if (waterLevel - heightField[idx] >= MinLakeCellDepth)
                        markedArea += rowArea[idx / w];
                }

                if (markedArea < MinLakeMarkedArea)
                    continue;

                // 盆地边缘的薄水层（沼泽/滩地）仍按陆地处理
                for (int k = 0; k < component.Count; k++)
                {
                    int idx = component[k];
                    float depth = waterLevel - heightField[idx];
                    if (depth < MinLakeCellDepth)
                        continue;
                    lakeMask[idx] = true;
                    waterDepth[idx] = depth;
                }
            }
        }

        /// <summary>取某格的湿度：优先用气候场，没有则退回按纬度预计算的每行湿度。</summary>
        private static double CellMoisture(int w, int idx, float[] moistureField, float[] rowFallback)
        {
            if (moistureField != null && idx < moistureField.Length)
                return moistureField[idx];

            return rowFallback != null ? rowFallback[idx / w] : 0.4;
        }

        /// <summary>由格号与地图盐值算出的稳定随机数（0-1）：同一张图结果一致。</summary>
        private static float Hash01(int index, int salt)
        {
            uint x = (uint)index * 2654435761u ^ (uint)salt * 2246822519u;
            x ^= x >> 15;
            x *= 2654435761u;
            x ^= x >> 13;
            x *= 2246822519u;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }

        // ── 4. 汇流累积 ──

        /// <summary>
        /// 按 filled 从高到低累积汇水量：
        ///   - 每格先产生本地径流（高山更多）
        ///   - 干旱区沿途渗漏蒸发，流量按比例衰减
        ///   - 低坡度平缓区允许一次分流，形成「主干分出支流」
        /// 最终 accum 即该格的有效流量（赤道等效格数）。
        /// </summary>
        private static void Accumulate(int w, int h, float[] heightField, float maxLandElevation,
            float[] moisture, double[] rowCosLat, float[] filled, int[] flowTo,
            int[] order, int orderCount, float riverThreshold,
            float[] accum, float[] moistFlux)
        {
            for (int k = orderCount - 1; k >= 0; k--)
            {
                int i = order[k];

                // 本地径流（高山降雨/融雪更多，干旱区产流更少）
                if (heightField[i] > 0f)
                {
                    float areaW = (float)rowCosLat[i / w];
                    float contrib = areaW
                                  * OrographicWeight(heightField[i], maxLandElevation)
                                  * RunoffFactor(moisture[i]);
                    accum[i] += contrib;
                    moistFlux[i] += moisture[i] * contrib;
                }

                int down = flowTo[i];
                if (down < 0)
                    continue;

                float outflow = accum[i];

                // 干旱区渗漏与蒸发
                float m = moisture[i];
                if (m < (float)ClimateModel.AridMoisture)
                    outflow *= m < (float)ClimateModel.VeryAridMoisture
                        ? VeryAridRetention : AridRetention;

                float share2 = 0f;
                int second = -1;
                if (accum[i] >= riverThreshold)
                    second = PickBranch(i, w, h, filled, heightField, down, out share2);

                float toMain = outflow * (1f - share2);
                float toBranch = outflow * share2;
                accum[down] += toMain;
                moistFlux[down] += moistFlux[i] * (1f - share2);

                if (second >= 0 && toBranch > 0f)
                {
                    accum[second] += toBranch;
                    moistFlux[second] += moistFlux[i] * share2;
                }
            }
        }

        /// <summary>
        /// 在坡度平缓的河段上寻找第二去向（分流）。返回 -1 表示不分流。
        /// 第二去向必须严格低于当前格的水面，保证不会出现环流。
        /// </summary>
        private static int PickBranch(int i, int w, int h, float[] filled,
            float[] heightField, int down, out float share)
        {
            share = 0f;

            // 只在平缓段分流（水面坡降小 = 冲积平原/辫状河段）
            if (filled[i] - filled[down] > BifurcationSlope)
                return -1;

            int x = i % w, y = i / w;
            int best = -1;
            float bestLevel = float.MaxValue;

            for (int d = 0; d < 8; d++)
            {
                int ny = y + DY8[d];
                if (ny < 0 || ny >= h)
                    continue;

                int j = ny * w + ((x + DX8[d] + w) % w);
                if (j == down)
                    continue;
                if (heightField[j] <= 0f)          // 分流去向仍须是陆地
                    continue;
                if (filled[j] >= filled[i])        // 必须更低，避免环流
                    continue;

                if (filled[j] < bestLevel)
                {
                    bestLevel = filled[j];
                    best = j;
                }
            }

            if (best < 0)
                return -1;

            share = BifurcationMinShare
                  + (BifurcationMaxShare - BifurcationMinShare) * (float)Hash01(i * 7 + 13);
            return best;
        }

        // ── 5. 河道与河宽 ──

        /// <summary>
        /// 流量超过阈值的格子成为河道；河宽按流量幂律增长
        /// （支流汇合 → 流量跃升 → 河面变宽；主干分流 → 流量被分走 → 河面收窄）。
        /// </summary>
        /// <param name="sphereRunoff">
        /// 球面径流场（可为 null）。传入时判据用它 —— 语义是：河网走向完全交给球面径流场
        /// （等面积格点上的真实汇流），平面累积只用于决定河宽。这样高纬不会再有沿纬线的伪河道，
        /// 也不会出现"平面河网 + 球面河网"两套叠加。
        /// </param>
        /// <param name="sphereThreshold">球面径流的河道阈值分位；未传入径流场时忽略</param>
        private static void ExtractRivers(int w, int h, float[] heightField, float riverThreshold,
            float[] accum, float[] moistFlux, float[] moisture, HydrologyData data,
            float[] sphereRunoff, float sphereThreshold)
        {
            int n = w * h;
            int riverCount = 0;
            int[] histogram = new int[102];      // 集水区湿度分布（1% 分箱）
            bool hasSphere = sphereRunoff != null && sphereRunoff.Length >= n;

            for (int i = 0; i < n; i++)
            {
                if (heightField[i] <= 0f)
                    continue;
                if (data.lakeMask[i])            // 湖面单独绘制，不叠河道
                    continue;

                float channelFlow = hasSphere ? sphereRunoff[i] : accum[i];
                float channelThreshold = hasSphere ? sphereThreshold : riverThreshold;
                if (channelFlow < channelThreshold)
                    continue;

                float width = MinRiverWidth
                            + WidthGain * ((float)Math.Pow(accum[i] / riverThreshold, WidthExponent) - 1f);
                if (width > MaxRiverWidth)
                    width = MaxRiverWidth;
                if (width < MinRiverWidth)
                    width = MinRiverWidth;

                data.riverMask[i] = true;
                data.riverWidth[i] = width;
                data.discharge[i] = accum[i];

                // 集水区平均湿度（产流加权）
                float catchmentMoisture = accum[i] > 1e-4f ? moistFlux[i] / accum[i] : moisture[i];
                data.catchmentMoisture[i] = catchmentMoisture;

                int bin = (int)(catchmentMoisture * 100f);
                if (bin < 0) bin = 0;
                if (bin > 101) bin = 101;
                histogram[bin]++;
                riverCount++;
            }

            // 季节性（间歇性）河流：集水区气候偏干（低于半干旱上限），
            // 且属于本地河网中最干的那四分之一 —— 湿润世界里几乎不会出现，
            // 干旱世界里则大量出现。
            float seasonalThreshold = (float)ClimateModel.SemiAridMoisture;
            if (riverCount > 0)
            {
                int target = riverCount / 4;
                int cumulative = 0;
                for (int b = 0; b <= 101; b++)
                {
                    cumulative += histogram[b];
                    if (cumulative >= target)
                    {
                        float p25 = b / 100f;
                        if (p25 < seasonalThreshold)
                            seasonalThreshold = p25;
                        break;
                    }
                }
            }

            if (riverCount == 0)
                return;

            for (int i = 0; i < n; i++)
            {
                if (!data.riverMask[i])
                    continue;
                if (data.catchmentMoisture[i] < seasonalThreshold)
                    data.seasonalMask[i] = true;
            }
        }

        // ── 6. 三角洲 ──

        /// <summary>
        /// 河道入海处冲刷出三角洲：若干条分流河道呈扇形铺入浅海。
        /// </summary>
        private static void CarveDeltas(int w, int h, float[] heightField,
            int[] flowTo, HydrologyData data)
        {
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                if (!data.riverMask[i])
                    continue;

                int down = flowTo[i];
                if (down < 0 || heightField[down] > 0f)
                    continue;                        // 只在河道入海的那一格处理

                data.estuaryMask[i] = true;
                CarveDeltaChannels(w, h, heightField, i, down, data.riverWidth[i], data);
            }
        }

        private static void CarveDeltaChannels(int w, int h, float[] heightField,
            int mouthIdx, int seaIdx, float width, HydrologyData data)
        {
            int mx = mouthIdx % w, my = mouthIdx / w;
            int sx = seaIdx % w, sy = seaIdx / w;

            double dirX = sx - mx;
            double dirY = sy - my;
            double len = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (len < 1e-6)
            {
                dirX = 0.0; dirY = 1.0;
            }
            else
            {
                dirX /= len;
                dirY /= len;
            }

            int branches = 2 + (int)(width / 3.5f);
            if (branches > 5) branches = 5;

            int steps = 2 + (int)(width * 0.9f);
            if (steps > 18) steps = 18;

            const double spread = 0.45;
            for (int b = 0; b < branches; b++)
            {
                double ang = (b - (branches - 1) * 0.5) * spread
                           + (Hash01(mouthIdx * 31 + b) - 0.5) * 0.25;
                double ca = Math.Cos(ang), sa = Math.Sin(ang);
                double bx = dirX * ca - dirY * sa;
                double by = dirX * sa + dirY * ca;

                double px = mx, py = my;
                for (int s = 1; s <= steps; s++)
                {
                    px += bx;
                    py += by;

                    int iy = (int)Math.Round(py);
                    if (iy < 0 || iy >= h)
                        break;

                    int j = iy * w + (((int)Math.Round(px)) % w + w) % w;
                    if (heightField[j] < -DeltaMaxDepth)
                        break;                       // 水太深，淤积不出三角洲

                    data.deltaMask[j] = true;
                    data.catchmentMoisture[j] = data.catchmentMoisture[mouthIdx];
                    if (data.seasonalMask[mouthIdx])
                        data.seasonalMask[j] = true;
                    float taper = 1f - (float)s / (steps + 1);
                    float channelWidth = width * (0.35f + 0.5f * taper);
                    if (channelWidth > data.riverWidth[j])
                        data.riverWidth[j] = channelWidth;
                }
            }
        }

        // ── 7. 加粗河道 ──

        /// <summary>
        /// 按河宽把河道掩膜加粗，使干流在图上占据多个像素，而支流仍是一两条线。
        /// </summary>
        private static void WidenRivers(int w, int h, float[] heightField, HydrologyData data)
        {
            int n = w * h;
            float[] widened = null;
            int[] widenedFrom = null;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!data.riverMask[i])
                        continue;

                    float width = data.riverWidth[i];
                    int radius = (int)(width * 0.35f);
                    if (radius < 1)
                        continue;
                    if (radius > 4)
                        radius = 4;

                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h)
                            continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > radius + 0.5)
                                continue;

                            int j = ny * w + ((x + dx + w) % w);
                            if (data.lakeMask[j])
                                continue;
                            if (heightField[j] <= 0f && !data.deltaMask[j])
                                continue;            // 不把海面撑宽

                            float wj = width * (1f - (float)(d / (radius + 1.0)) * 0.6f);
                            if (widened == null)
                            {
                                widened = new float[n];
                                widenedFrom = new int[n];
                            }
                            if (wj > widened[j])
                            {
                                widened[j] = wj;
                                widenedFrom[j] = i;   // 记住来源，便于继承季节性等属性
                            }
                        }
                    }
                }
            }

            if (widened == null)
                return;

            for (int i = 0; i < n; i++)
            {
                if (widened[i] <= data.riverWidth[i])
                    continue;

                int from = widenedFrom[i];
                data.riverWidth[i] = widened[i];
                data.catchmentMoisture[i] = data.catchmentMoisture[from];
                if (data.seasonalMask[from])
                    data.seasonalMask[i] = true;

                if (!data.riverMask[i])
                {
                    if (data.lakeMask[i])
                        continue;
                    if (heightField[i] <= 0f && !data.deltaMask[i])
                        continue;
                    data.riverMask[i] = true;
                }
            }
        }

        // ── 工具 ──

        /// <summary>由整数索引得到 [0,1) 的确定性伪随机数（线程安全）。</summary>
        private static double Hash01(int value)
        {
            double t = Math.Sin(value * 12.9898) * 43758.5453;
            return t - Math.Floor(t);
        }

        /// <summary>以 float[] 为键的索引最小堆（避免复制键值）。</summary>
        private sealed class IndexMinHeap
        {
            private readonly float[] keys;
            private int[] items;
            private int count;

            public IndexMinHeap(float[] keys)
            {
                this.keys = keys;
                items = new int[1024];
            }

            public int Count => count;

            public void Push(int idx)
            {
                if (count == items.Length)
                    Array.Resize(ref items, count * 2);

                items[count] = idx;
                int i = count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (keys[items[i]] < keys[items[parent]])
                    {
                        int t = items[i];
                        items[i] = items[parent];
                        items[parent] = t;
                        i = parent;
                    }
                    else
                        break;
                }
            }

            public int Pop()
            {
                int result = items[0];
                count--;

                if (count > 0)
                {
                    items[0] = items[count];
                    int i = 0;
                    while (true)
                    {
                        int left = (i << 1) + 1;
                        int right = left + 1;
                        int smallest = i;

                        if (left < count && keys[items[left]] < keys[items[smallest]])
                            smallest = left;
                        if (right < count && keys[items[right]] < keys[items[smallest]])
                            smallest = right;

                        if (smallest == i)
                            break;

                        int t = items[i];
                        items[i] = items[smallest];
                        items[smallest] = t;
                        i = smallest;
                    }
                }

                return result;
            }
        }
    }
}
