using System;

namespace WorldTerrain
{
    /// <summary>
    /// 侵蚀模拟：把「噪声曲面」雕琢成更像真实地表的样子。
    ///
    ///   1. 水力侵蚀：雨滴沿坡面下泄，冲刷河谷并把泥沙带到下游、洼地与入海口沉积，
    ///      因此会自然形成 V 形谷、冲积扇、三角洲与山前平原
    ///   2. 热力侵蚀：坡度超过休止角时物质向下滑落，使陡崖变缓、坡面更自然
    ///
    /// 侵蚀强度与降水（湿度）相关：湿润地区冲刷更强（气候—侵蚀耦合）。
    /// 只使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class ErosionSimulator
    {
        // ── 雨滴参数（经典粒子侵蚀算法）──
        private const float Inertia = 0.06f;          // 方向惯性
        private const float CapacityFactor = 3.0f;    // 携沙能力
        private const float DepositFactor = 0.30f;    // 沉积速率
        private const float ErodeFactor = 0.50f;      // 冲刷速率
        private const float EvaporateFactor = 0.010f; // 蒸发
        private const float Gravity = 4f;             // 速度增益
        private const float MinSlope = 0.008f;        // 最小坡度（防止平地无限携沙）
        private const int MaxLifetime = 40;           // 每滴雨的最大步数
        private const int ErosionRadius = 3;          // 冲刷半径（格）

        // ── 热力侵蚀参数 ──
        private const int ThermalIterations = 3;
        private const float ThermalRate = 0.35f;

        // ── 雨滴数量 ──
        private const float DropletRatio = 0.10f;     // 每格雨滴数
        private const int MinDroplets = 40000;
        private const int MaxDroplets = 320000;

        // 8 邻域
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// 对高度场做水力 + 热力侵蚀，结果直接写回 heightField。
        /// </summary>
        /// <param name="heightField">高度场（米），海平面为 0</param>
        /// <param name="precipitation">降水/湿度场（0-1），可为 null；用于让湿润区侵蚀更强</param>
        /// <param name="seed">随机种子（保证可复现）</param>
        /// <param name="onProgress">进度回调 0-1</param>
        public static void Erode(int width, int height, float[] heightField, float[] precipitation,
            int seed, Action<float> onProgress = null)
        {
            int w = width, h = height, n = w * h;

            // 最高点保持串行：它只是一次线性扫描（约 1 ms），而一旦算错 scale，
            // 整个侵蚀结果都会变。并行归约需要额外同步，不值得为这点开销冒险。
            float maxLand = 0f;
            for (int i = 0; i < n; i++)
                if (heightField[i] > maxLand)
                    maxLand = heightField[i];
            if (maxLand < 1f)
                return;

            // 归一化到 ~0..1 的「侵蚀空间」，使算法参数与地形绝对高度无关
            float scale = 1f / maxLand;
            var map = new float[n];
            Parallelism.For(n, i => map[i] = heightField[i] * scale);

            onProgress?.Invoke(0.02f);

            // ── 1. 水力侵蚀 ──
            BuildBrush(ErosionRadius, out int[] brushDx, out int[] brushDy, out float[] brushWeight);

            int droplets = (int)(n * DropletRatio);
            if (droplets < MinDroplets) droplets = MinDroplets;
            if (droplets > MaxDroplets) droplets = MaxDroplets;

            var rng = new Random(seed);
            for (int d = 0; d < droplets; d++)
            {
                if ((d & 2047) == 0)
                    onProgress?.Invoke(0.02f + 0.83f * d / droplets);

                // 只在陆地上「下雨」
                float px = (float)rng.NextDouble() * (w - 2) + 0.5f;
                float py = (float)rng.NextDouble() * (h - 2) + 0.5f;
                int startIdx = (int)py * w + (int)px;
                if (map[startIdx] <= 0f)
                    continue;

                float climate = precipitation != null ? 0.35f + 1.3f * precipitation[startIdx] : 1f;

                float dirX = 0f, dirY = 0f, speed = 1f, water = 1f, sediment = 0f;

                for (int life = 0; life < MaxLifetime; life++)
                {
                    int cx = (int)px, cy = (int)py;
                    float fx = px - cx, fy = py - cy;

                    float h00 = Sample(map, w, h, cx, cy);
                    float h10 = Sample(map, w, h, cx + 1, cy);
                    float h01 = Sample(map, w, h, cx, cy + 1);
                    float h11 = Sample(map, w, h, cx + 1, cy + 1);

                    float gx = (h10 - h00) * (1f - fy) + (h11 - h01) * fy;
                    float gy = (h01 - h00) * (1f - fx) + (h11 - h10) * fx;
                    float hOld = h00 * (1f - fx) * (1f - fy) + h10 * fx * (1f - fy)
                               + h01 * (1f - fx) * fy + h11 * fx * fy;

                    // 惯性 + 重力方向
                    dirX = dirX * Inertia - gx * (1f - Inertia);
                    dirY = dirY * Inertia - gy * (1f - Inertia);
                    float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
                    if (len < 1e-7f)
                        break;                       // 落在坑底，结束
                    dirX /= len;
                    dirY /= len;

                    px += dirX;
                    py += dirY;

                    if (px < 0.5f || px > w - 1.5f || py < 0.5f || py > h - 1.5f)
                        break;                       // 出界：泥沙留在这里

                    float hNew = SampleBilinear(map, w, h, px, py);
                    float dh = hNew - hOld;

                    float capacity = (dh < 0f ? -dh : MinSlope) * speed * water * CapacityFactor;

                    if (sediment > capacity || dh > 0f)
                    {
                        // 沉积：上坡时把泥沙填在坡上，携沙过饱和时按比例卸下
                        float amount = dh > 0f
                            ? Math.Min(dh, sediment)
                            : (sediment - capacity) * DepositFactor;
                        if (amount > 0f)
                        {
                            sediment -= amount;
                            DepositBilinear(map, w, h, cx, cy, fx, fy, amount);
                        }
                    }
                    else
                    {
                        // 冲刷：按刷子半径取走泥沙（湿润区更强）
                        float amount = Math.Min((capacity - sediment) * ErodeFactor * climate, -dh);
                        if (amount > 0f)
                            sediment += ErodeBrush(map, w, h, brushDx, brushDy, brushWeight, cx, cy, amount);
                    }

                    speed = (float)Math.Sqrt(Math.Max(0f, speed * speed + (-dh) * Gravity));
                    water *= 1f - EvaporateFactor;
                    if (water < 0.01f)
                        break;
                }
            }

            // ── 2. 热力侵蚀（休止角）──
            onProgress?.Invoke(0.86f);
            float talus = 0.06f;                     // 归一化高度下的最大稳定坡降
            ThermalErode(w, h, map, ThermalIterations, talus, ThermalRate);

            // 写回（每格只写自己的槽位）
            Parallelism.For(n, i => heightField[i] = map[i] / scale);

            onProgress?.Invoke(1f);
        }

        // ── 采样与刷子 ──

        private static float Sample(float[] map, int w, int h, int x, int y)
        {
            x = ((x % w) + w) % w;
            if (y < 0) y = 0;
            if (y >= h) y = h - 1;
            return map[y * w + x];
        }

        private static float SampleBilinear(float[] map, int w, int h, float px, float py)
        {
            int x = (int)px, y = (int)py;
            float fx = px - x, fy = py - y;
            float h00 = Sample(map, w, h, x, y);
            float h10 = Sample(map, w, h, x + 1, y);
            float h01 = Sample(map, w, h, x, y + 1);
            float h11 = Sample(map, w, h, x + 1, y + 1);
            return h00 * (1f - fx) * (1f - fy) + h10 * fx * (1f - fy)
                 + h01 * (1f - fx) * fy + h11 * fx * fy;
        }

        private static void BuildBrush(int radius, out int[] dx, out int[] dy, out float[] weight)
        {
            int size = radius * 2 + 1;
            var listDx = new int[size * size];
            var listDy = new int[size * size];
            var listW = new float[size * size];
            int count = 0;
            float sum = 0f;

            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    float dist = (float)Math.Sqrt(x * x + y * y);
                    if (dist > radius)
                        continue;
                    float wgt = radius - dist + 0.5f;
                    listDx[count] = x;
                    listDy[count] = y;
                    listW[count] = wgt;
                    sum += wgt;
                    count++;
                }
            }

            dx = new int[count];
            dy = new int[count];
            weight = new float[count];
            for (int i = 0; i < count; i++)
            {
                dx[i] = listDx[i];
                dy[i] = listDy[i];
                weight[i] = listW[i] / sum;
            }
        }

        private static float ErodeBrush(float[] map, int w, int h,
            int[] brushDx, int[] brushDy, float[] brushWeight, int cx, int cy, float amount)
        {
            float removed = 0f;
            for (int k = 0; k < brushDx.Length; k++)
            {
                int nx = ((cx + brushDx[k]) % w + w) % w;
                int ny = cy + brushDy[k];
                if (ny < 0 || ny >= h)
                    continue;

                int j = ny * w + nx;
                if (map[j] <= 0f)
                    continue;                        // 不冲刷海底，保留大陆架

                float delta = amount * brushWeight[k];
                if (delta > map[j])
                    delta = map[j];
                map[j] -= delta;
                removed += delta;
            }
            return removed;
        }

        private static void DepositBilinear(float[] map, int w, int h,
            int cx, int cy, float fx, float fy, float amount)
        {
            int x0 = ((cx % w) + w) % w;
            int x1 = ((cx + 1) % w + w) % w;
            int y0 = cy < 0 ? 0 : (cy >= h ? h - 1 : cy);
            int y1 = cy + 1 < 0 ? 0 : (cy + 1 >= h ? h - 1 : cy + 1);

            map[y0 * w + x0] += amount * (1f - fx) * (1f - fy);
            map[y0 * w + x1] += amount * fx * (1f - fy);
            map[y1 * w + x0] += amount * (1f - fx) * fy;
            map[y1 * w + x1] += amount * fx * fy;
        }

        /// <summary>
        /// 热力侵蚀：坡降超过休止角时，多余的物质向最低邻居移动，使陡坡整体变缓。
        ///
        /// 并行化要点：邻域扫描只读 map、可并行；但一次搬运会写 delta[i] 和 delta[lowest]，
        /// 而 lowest 可能落在别的线程负责的槽位上，直接并行会丢更新。
        /// 因此拆成两趟：并行扫描只记录「搬多少 / 搬到哪」，再串行按原下标顺序累加 ——
        /// 加法顺序与原来的单循环完全相同，结果逐位一致。
        /// </summary>
        private static void ThermalErode(int w, int h, float[] map, int iterations, float talus, float rate)
        {
            int n = w * h;
            var delta = new float[n];
            var moveAmount = new float[n];
            var moveTarget = new int[n];

            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(delta, 0, n);

                Parallelism.ForRows(h, y =>
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        moveAmount[i] = 0f;
                        moveTarget[i] = -1;

                        float h0 = map[i];

                        int lowest = -1;
                        float lowestH = h0;
                        for (int d = 0; d < 8; d++)
                        {
                            int ny = y + DY8[d];
                            if (ny < 0 || ny >= h)
                                continue;
                            int j = ny * w + ((x + DX8[d] + w) % w);
                            if (map[j] < lowestH)
                            {
                                lowestH = map[j];
                                lowest = j;
                            }
                        }

                        if (lowest < 0)
                            continue;

                        float drop = h0 - lowestH;
                        if (drop <= talus)
                            continue;

                        moveAmount[i] = (drop - talus) * rate * 0.5f;
                        moveTarget[i] = lowest;
                    }
                });

                // 串行累加：顺序与原来的单循环一致，保证逐位相同
                for (int i = 0; i < n; i++)
                {
                    float move = moveAmount[i];
                    if (move == 0f)
                        continue;
                    delta[i] -= move;
                    delta[moveTarget[i]] += move;
                }

                Parallelism.For(n, i => map[i] += delta[i]);
            }
        }
    }
}
