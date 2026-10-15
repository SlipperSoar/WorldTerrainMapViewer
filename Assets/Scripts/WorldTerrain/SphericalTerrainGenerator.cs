using System;

namespace WorldTerrain
{
    /// <summary>
    /// 球面生成域的结果：高度场 + 判定河网要用的球面径流场。
    ///
    /// 径流场是「已投影到等距圆柱网格」的球面汇流累积量（每格一个值）。
    /// 河网因此可以按球面真实汇流走向判定，而不是沿投影网格的行列方向。
    /// 实测（level 8，655,362 格点）：陆地 22.5 万格，汇水量 p50=1.6 / p90=7.8 /
    /// p99=30.7 / p99.9=76 / 最大 226，集中度 p99/p50 = 19.8 —— 具备真实河网的重尾特征。
    /// </summary>
    public struct SphereTerrainResult
    {
        /// <summary>等距圆柱高度图（米，海平面为 0），长度 = width × height。</summary>
        public float[] Height;

        /// <summary>球面径流场（已投影到等距圆柱网格），长度同高度图。</summary>
        public float[] Runoff;

        /// <summary>
        /// 径流场在陆地格上的 p98 分位（供水文模块判定河道用）。
        /// 用它而不是写死常数：汇流量随网格级别缩放（level 7/8 的陆地格数差 4 倍），
        /// 固定阈值在另一级上会几乎选不出河道。
        /// </summary>
        public float RunoffChannelThreshold;
    }

    /// <summary>
    /// 球面地形生成：先在单位球面的等面积网格上生成大地形，再投影成等距圆柱高度图，
    /// 最后逐像素叠加中/细尺度细节（3D 噪声在球面上天然连续）。
    ///
    /// 这样做的意义（对照「平面地图转球体地形」方案）：
    ///   - 生成域是球面而不是矩形，板块距离、边界影响、平滑都不再受投影面积失真影响；
    ///   - 极点不再是奇点（90°N 的所有经线在球面上是同一个点），因此不需要"极点行抹平"这类补丁；
    ///   - 经度 0°/360° 是同一处，天然无接缝；
    ///   - 矩形图退化为纯粹的投影展示。
    /// </summary>
    public static class SphericalTerrainGenerator
    {
        /// <summary>陆壳噪声频率与偏移（与平面路径保持一致的世界观）。</summary>
        private const double CratonFrequency = 1.1;
        private const double CratonBias = 17.0;

        /// <summary>陆壳起伏放大系数。</summary>
        private const double ContinentalReliefScale = 0.9;

        /// <summary>地壳均衡幅度（米）：陆壳抬升、洋壳下沉。</summary>
        private const double IsostasyAmplitude = 5000.0;

        /// <summary>边界影响的作用半角（两个板块中心点积之差）。</summary>
        private const double BoundaryReach = 0.12;

        // ── 板块边界影响的幅度（米）──
        // 依据：板块内部的山脉来自地壳/地幔自身（大地形噪声那几层），
        // 而板块边界的高山与鸿沟来自板块运动 —— 幅度应当随**相对运动速度**缩放：
        // 挤压强的边界给出高大山系，张裂给出裂谷/海沟，运动弱的边界只有缓坡。
        // 单位是「closing = 1 且相对速度处于平均值（1.25）时」的幅度。

        /// <summary>陆壳汇聚隆起：大陆碰撞造山带的主力项。</summary>
        private const double UpliftConvergentContinental = 700.0;

        /// <summary>洋壳汇聚隆起：俯冲带以上盘火山弧为主，比陆陆碰撞矮。</summary>
        private const double UpliftConvergentOceanic = 250.0;

        /// <summary>陆壳张裂沉降（裂谷/裂陷盆地）。</summary>
        private const double SubsidenceDivergentContinental = 400.0;

        /// <summary>洋壳张裂沉降（洋中脊两侧的断裂下陷/海沟）。</summary>
        private const double SubsidenceDivergentOceanic = 500.0;

        /// <summary>相对速度的参考值（GeneratePlates 里 driftSpeed ∈ [0.5, 2.0] 的均值）。</summary>
        private const double ReferenceDriftSpeed = 1.25;

        /// <summary>
        /// 球面网格的格点上限（越大球面计算越精确、越慢）。
        /// 必须 ≤ 该级别的格点数才会真正选中它：level 6 = 40,962、level 7 = 163,842、level 8 = 655,362。
        ///
        /// 实测（1024×512，含球面排水 + 侵蚀，本机）：
        ///   level 6（4.1 万格点，≈12 像素/格）≈ 0.33 s
        ///   level 7（16.4 万格点，≈3 像素/格）≈ 0.9 s   ← 默认：球面数据达到像素级可靠
        /// 网格构建已从 11.5 s 降到 31 ms（开地址哈希替代 Dictionary/HashSet），
        /// 因此这里可以放心用更细的网格。
        /// </summary>
        private const int MaxSphereCells = 700000;

        /// <summary>
        /// 在球面网格上生成高度场，投影成 w×h 的等距圆柱高度图（米）。
        /// </summary>
        /// <returns>
        /// 高度场与「球面径流场」一起返回，调用方显式把它交给水文模块，
        /// 不再依赖静态字段传递（静态字段在平面/球面路径切换时会串味）。
        /// </returns>
        public static SphereTerrainResult Generate(WorldGenConfig config, PlateInfo[] plates,
            SeededNoise noise, Action<float> onProgress = null)
        {
            int w = config.width, h = config.height;

            // 球面网格粒度由 cellTarget 决定：约每 3 个像素一个格点，但总量封顶。
            // 实测代价（1024×512，含球面排水 + 侵蚀，本机）：
            //   level 6（4.1 万格点，约 12 像素/格）≈ 1.0 s     ← 默认
            //   level 7（16.4 万格点，约 3 像素/格）≈ 12.9 s    ← 更精细，但需要先优化投影/邻域代价
            // 想要更细就把 MaxSphereCells 调大（值越大越接近"每格点一个像素"）。
            int cellTarget = Math.Min(MaxSphereCells, Math.Max(12, w * h / 4));
            int level = Math.Min(8, SphereGrid.LevelForCellCount(cellTarget));
            var grid = SphereGrid.Create(level);
            int n = grid.VertexCount;

            var height = new float[n];
            var continental = new float[n];
            var plateOf = new int[n];

            // ── (a) 每个格点：板块归属 + 陆壳/洋壳 + 大地形起伏 + 地壳均衡 ──
            for (int i = 0; i < n; i++)
            {
                Vector3D pos = grid.Vertices[i];

                // 板块：球面 Voronoi（点积最大）+ 3D 噪声扰动出不规则边界
                Vector3D perturbed = PerturbedPlatePosition(pos, noise);

                int best = 0;
                double bestDot = -2.0;
                for (int p = 0; p < plates.Length; p++)
                {
                    double dot = perturbed.Dot(plates[p].center);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = p;
                    }
                }
                plateOf[i] = best;

                // 陆壳 / 洋壳：低频噪声 → 0 洋壳、1 陆壳（大陆成片）
                double craton = noise.Fbm3D(
                    pos.x * CratonFrequency + CratonBias,
                    pos.y * CratonFrequency + CratonBias,
                    pos.z * CratonFrequency, 3, 0.5, 2.0);
                double cont = SmoothStep((craton - 0.36) / 0.28);
                continental[i] = (float)cont;

                // 域扭曲 + 大尺度地形
                double warpX = noise.Noise3D(pos.x * 2.0, pos.y * 2.0, pos.z * 2.0) * 0.3;
                double warpY = noise.Noise3D(
                    pos.x * 2.0 + 100, pos.y * 2.0 + 100, pos.z * 2.0 + 100) * 0.3;
                double n1 = noise.Fbm3D(
                    (pos.x + warpX) * 1.5, (pos.y + warpY) * 1.5, pos.z * 1.5, 4, 0.5, 2.0);

                double reliefScale = 0.55 + ContinentalReliefScale * cont;
                double isostasy = (cont - 0.5) * IsostasyAmplitude;

                height[i] = (float)(((n1 * 2.0 - 1.0) * 2500.0) * reliefScale + isostasy);
            }

            onProgress?.Invoke(0.35f);

            // ── (a2) 中/细尺度细节也放在球面格点上 ──
            // 关键：排水与侵蚀看到的必须是「最终地形本身」，而不是缺了细节的光滑底稿。
            // 球面数据的可靠性直接决定河谷走向与侵蚀结果是否合理。
            for (int i = 0; i < n; i++)
            {
                Vector3D pos = grid.Vertices[i];
                double n2 = noise.Fbm3D(pos.x * 5.0, pos.y * 5.0, pos.z * 5.0, 5, 0.5, 2.0);
                double n3 = noise.Fbm3D(pos.x * 15.0, pos.y * 15.0, pos.z * 15.0, 4, 0.5, 2.0);
                height[i] += (float)((n2 * 2.0 - 1.0) * 1200.0 + (n3 * 2.0 - 1.0) * 500.0);
            }

            // ── (b) 板块边界影响：在球面上按角距判定，再做球面平滑 ──
            float[] boundary = ComputeBoundaryEffect(grid, plates, plateOf, continental, noise);
            grid.LaplacianSmooth(boundary, 14, 0.55f);
            for (int i = 0; i < n; i++)
                height[i] += boundary[i];

            onProgress?.Invoke(0.55f);

            // ── (b2) 球面排水：填洼 → 汇流累积 → 按汇水量下切河谷 ──
            // 河谷沿球面真实分水岭生长，高纬不会被过度切割，也没有经度接缝。
            var wetness = new float[n];
            for (int i = 0; i < n; i++)
            {
                double absLat = Math.Abs(grid.Lat[i]);
                wetness[i] = (float)ClimateModel.SeaSourceMoisture(absLat);
            }
            // 球面排水：填洼 → 汇流累积 → 按汇水量下切河谷。
            // 排水的中间量（填洼面、流向、汇流量）随返回值一起交出去 —— 它们既是诊断依据，
            // 也是「湖泊判定该在哪一级网格上做」这个问题的实测证据：
            // 本网格（1024×512 → level 7）一格约 3,600 km²，比投影后的像素（约 7,800 km²/格）
            // 更粗，Priority-Flood 会把相邻台地并成一个几千米深的「盆地」。
            var sphereAccum = new float[n];
            SphereDrainage.InciseValleys(grid, height, wetness, 18.0f, noise, sphereAccum);

            var result = default(SphereTerrainResult);

            // 顺手把球面径流场投影出来（约 100 ms），随高度场一起返回给调用方
            result.Runoff = grid.ProjectToEquirectangular(w, h, sphereAccum);

            // 陆地格上的 p98 汇流量（256 桶直方图，O(n)）作为"成河"阈值
            {
                float accMax = 1f;
                for (int i = 0; i < n; i++)
                    if (height[i] > 0f && sphereAccum[i] > accMax)
                        accMax = sphereAccum[i];

                var hist = new int[256];
                int landCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (height[i] <= 0f)
                        continue;
                    int bin = (int)(sphereAccum[i] / accMax * 255f);
                    if (bin < 0) bin = 0;
                    if (bin > 255) bin = 255;
                    hist[bin]++;
                    landCount++;
                }

                // 0.98：河网密度旋钮（0.97 时河道约占陆地 2.5%，视觉偏密，收到 2% 左右）。
                // 用 0.90 会选中 10% 的陆地，河网过密（实测河道从 4.9% 涨到 12.1%）。
                int need = (int)(landCount * 0.98);
                int seen = 0;
                result.RunoffChannelThreshold = accMax;
                for (int b = 0; b < 256; b++)
                {
                    seen += hist[b];
                    if (seen >= need)
                    {
                        result.RunoffChannelThreshold = accMax * (b + 1) / 256f;
                        break;
                    }
                }
            }
            onProgress?.Invoke(0.62f);

            // ── (b3) 侵蚀：水力雨滴 + 热力休止角（把下切出来的河谷磨成自然坡面）──
            ErodeOnSphere(grid, height, wetness, noise);
            onProgress?.Invoke(0.7f);

            // ── (c) 投影成等距圆柱图（球面 Voronoi 单元栅格化）──
            float[] field = grid.ProjectToEquirectangular(w, h, height);

            onProgress?.Invoke(0.75f);

            // ── (d) 投影后只补一层高频纹理 ──
            // 中/细尺度已由球面网格承载（在那里参与排水与侵蚀），
            // 这里只补网格分辨不出来的一层细纹，避免插值后的地形显得过于光滑。
            AddFineTexture(w, h, field, noise);

            // 高度先钳制到配置范围，再做海陆比例归一化（与矩形路径一致）
            float lo = Math.Min(config.minElevation, config.maxElevation);
            float hi = Math.Max(config.minElevation, config.maxElevation);
            for (int i = 0; i < field.Length; i++)
                field[i] = Math.Max(lo, Math.Min(hi, field[i]));

            onProgress?.Invoke(0.9f);

            // ── (e) 海陆比例归一化 + 大陆架过渡 ──
            PlateTectonicsGenerator.ApplyWaterCoverage(w, h, field, config.waterCoverage);
            PlateTectonicsGenerator.ApplyContinentalShelf(w, h, field, noise);

            onProgress?.Invoke(1f);
            result.Height = field;
            return result;
        }

        /// <summary>
        /// 旧入口（薄包装，等价于 <see cref="Generate"/> 的 <see cref="SphereTerrainResult.Height"/>）。
        /// 只想要高度场的调用点可以继续用它，径流场仍然通过 <see cref="Generate"/> 显式获取。
        /// </summary>
        public static float[] GenerateHeights(WorldGenConfig config, PlateInfo[] plates,
            SeededNoise noise, Action<float> onProgress = null)
        {
            return Generate(config, plates, noise, onProgress).Height;
        }

        /// <summary>
        /// 板块边界影响（球面版）：
        /// 用「最近板块与次近板块的中心点积差」判断离边界多远，
        /// 用两板块的相对速度在边界法线上的投影判断挤压（造山）还是张裂（裂谷），
        /// 洋壳俯冲则以下沉（海沟）为主。
        ///
        /// **关键：必须和板块归属用同一个位置。** 归属（<c>Generate</c> 里的 Voronoi）用的是
        /// 噪声扰动后的位置，若这里改用未扰动的 <c>pos</c> 去量「最近/次近」，两者会错配：
        /// 某些格点会出现「本格所属板块其实最远」（<c>d2 - d1 &lt; 0</c>），
        /// 而相邻格归属正常 —— 一个被判成"离边界很远"直接跳过，一个拿到满幅隆起，
        /// 于是相邻两格的高差可达上万米。实测未修复时边界影响场平滑前相邻跳变 **16438 m**，
        /// 14 趟 Laplacian 之后仍有 **3163 m**，投影成图就是陆地上一片凭空出现的悬崖
        /// （且离板块边界线很远），见 README「悬崖诊断」。
        /// </summary>
        private static float[] ComputeBoundaryEffect(SphereGrid grid, PlateInfo[] plates,
            int[] plateOf, float[] continental, SeededNoise noise)
        {
            int n = grid.VertexCount;
            var effect = new float[n];

            for (int i = 0; i < n; i++)
            {
                Vector3D p = grid.Vertices[i];
                int own = plateOf[i];

                // 与归属判定同一套扰动位置（同一个函数、同一份噪声参数）
                Vector3D q = PerturbedPlatePosition(p, noise);

                double d1 = q.Dot(plates[own].center);
                int other = -1;
                double d2 = -2.0;
                for (int q2 = 0; q2 < plates.Length; q2++)
                {
                    if (q2 == own)
                        continue;
                    double d = q.Dot(plates[q2].center);
                    if (d > d2)
                    {
                        d2 = d;
                        other = q2;
                    }
                }
                if (other < 0)
                    continue;

                // gap < 0 说明「本格所属板块不是最近板块」——那是扰动边界上的正常情形，
                // 归一成"正好在边界上"，而不是当成"离边界很远"跳过（跳过会在相邻格之间造出台阶）。
                double gap = d2 - d1;
                if (gap < 0.0)
                    gap = 0.0;
                if (gap > BoundaryReach)
                    continue;                        // 离边界太远，不受影响

                double boundary = 1.0 - gap / BoundaryReach;

                // 边界指向另一板块的切向（在扰动位置上求，与判定同空间）
                Vector3D toOther = (plates[other].center - q * plates[other].center.Dot(q)).Normalized;

                Vector3D rel = plates[own].velocity * plates[own].driftSpeed
                             - plates[other].velocity * plates[other].driftSpeed;

                // 归一化到大致 [-1,1]：否则两个板块的速度和会让隆起爆到上万米
                double norm = plates[own].driftSpeed + plates[other].driftSpeed + 1e-6;
                double closing = -rel.Dot(toOther) / norm;

                double conv = closing > 0.0 ? closing : 0.0;
                double div = closing < 0.0 ? -closing : 0.0;
                double cont = continental[i];

                // 幅度随相对运动速度缩放：两个板块越快，挤压/张裂越强烈。
                // 用两板块漂流速度的平均值（0.5~2.0）对参考值 1.25 归一，
                // 于是「运动激烈的边界」能给出约 1.6 倍的高山，「平静的边界」只有约 0.4 倍。
                double driftStrength = (plates[own].driftSpeed + plates[other].driftSpeed)
                                     / (2.0 * ReferenceDriftSpeed);

                double uplift = boundary * driftStrength
                              * (conv * (UpliftConvergentOceanic
                                         + (UpliftConvergentContinental - UpliftConvergentOceanic) * cont)
                               - div * (SubsidenceDivergentOceanic
                                        + (SubsidenceDivergentContinental - SubsidenceDivergentOceanic) * (1.0 - cont)));

                uplift *= 0.7 + 0.6 * noise.Fbm3D(p.x * 6.0, p.y * 6.0, p.z * 6.0, 3, 0.5, 2.0);
                effect[i] = (float)uplift;
            }

            return effect;
        }

        /// <summary>
        /// 板块 Voronoi 用的扰动位置：3D 噪声把格点推离原处，边界因此变得不规则。
        /// 归属判定与边界影响**必须**调用同一个函数，否则两者描述的是不同的板块划分。
        /// </summary>
        private static Vector3D PerturbedPlatePosition(Vector3D pos, SeededNoise noise)
        {
            double px = noise.Noise3D(pos.x * 4.0, pos.y * 4.0, pos.z * 4.0) - 0.5;
            double py = noise.Noise3D(pos.x * 4.0 + 10, pos.y * 4.0 + 10, pos.z * 4.0 + 10) - 0.5;
            double pz = noise.Noise3D(pos.x * 4.0 + 20, pos.y * 4.0 + 20, pos.z * 4.0 + 20) - 0.5;
            return (pos + new Vector3D(px, py, pz) * 0.12).Normalized;
        }

        /// <summary>投影后补一层高频细纹（球面网格已承载中/细尺度并参与排水与侵蚀）。</summary>
        public static void AddFineTexture(int w, int h, float[] field, SeededNoise noise)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector3D pos = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
                    double n4 = noise.Fbm3D(pos.x * 34.0, pos.y * 34.0, pos.z * 34.0, 3, 0.5, 2.0);
                    field[y * w + x] += (float)((n4 * 2.0 - 1.0) * 220.0);
                }
            }
        }

        private static double SmoothStep(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // ── 球面侵蚀 ──

        private const float Inertia = 0.15f;          // 方向惯性
        private const float CapacityFactor = 3.0f;    // 携沙能力
        private const float DepositFactor = 0.30f;    // 沉积速率
        private const float ErodeFactor = 0.50f;      // 冲刷速率
        private const float EvaporateFactor = 0.010f; // 蒸发
        private const float Gravity = 4f;             // 速度增益
        private const float MinSlope = 0.006f;        // 最小坡度
        private const int MaxLifetime = 40;           // 每滴雨最大步数
        private const int ThermalIterations = 3;      // 热力侵蚀轮数
        private const float TalusSlope = 0.08f;       // 休止角（归一化高差）
        private const float ThermalRate = 0.35f;      // 搬运速率

        /// <summary>
        /// 球面雨滴侵蚀：每滴雨从随机的陆地点出发，沿「最陡下降且与前进方向一致」的
        /// 邻接边下泄（惯性让它不会贴着网格走直角），按携沙能力冲刷、过饱和则沉积；
        /// 最后做几轮热力侵蚀把超过休止角的坡面磨缓。
        /// </summary>
        public static void ErodeOnSphere(SphereGrid grid, float[] height, float[] wetness,
            SeededNoise noise)
        {
            int n = grid.VertexCount;

            float maxLand = 1f;
            for (int i = 0; i < n; i++)
                if (height[i] > maxLand)
                    maxLand = height[i];

            // 预先取倒数：内层每个邻居原本都要做一次除法（level 7 约 4800 万次）
            float invMaxLand = 1f / maxLand;

            // 预计算每条有向边的单位切向：内层循环原本每步每邻居都要做一次
            // 归一化（sqrt + 3 次除法），这是侵蚀成为球面阶段最大开销的主因。
            int neighborSlots = grid.Neighbors.Length;
            var tanX = new float[neighborSlots];
            var tanY = new float[neighborSlots];
            var tanZ = new float[neighborSlots];
            for (int i = 0; i < n; i++)
            {
                Vector3D pi = grid.Vertices[i];
                int s0 = grid.NeighborStart[i], e0 = grid.NeighborStart[i + 1];
                for (int k = s0; k < e0; k++)
                {
                    Vector3D pj = grid.Vertices[grid.Neighbors[k]];
                    Vector3D t = (pj - pi * pj.Dot(pi)).Normalized;
                    tanX[k] = (float)t.x;
                    tanY[k] = (float)t.y;
                    tanZ[k] = (float)t.z;
                }
            }

            // 雨滴数量按格点数缩放并封顶：每步要做邻域切向对齐计算，
            // 太密的网格上不加约束会直接吃掉好几秒
            int droplets = Math.Min(200000, Math.Max(15000, n * 2));
            var rng = new Random(20261015);
            var sediment = 0f;
            var dir = new Vector3D(0, 0, 0);

            for (int d = 0; d < droplets; d++)
            {
                int c = rng.Next(n);
                if (height[c] <= 0f)
                    continue;                           // 海面上不会产生冲刷

                Vector3D p = grid.Vertices[c];

                // 随机初始切向
                double a = rng.NextDouble() * 2.0 * Math.PI;
                Vector3D tangentSeed = Math.Abs(p.y) < 0.9
                    ? new Vector3D(0, 1, 0)
                    : new Vector3D(1, 0, 0);
                dir = new Vector3D(
                    tangentSeed.x - p.x * tangentSeed.Dot(p),
                    tangentSeed.y - p.y * tangentSeed.Dot(p),
                    tangentSeed.z - p.z * tangentSeed.Dot(p)).Normalized;
                Vector3D bi = new Vector3D(
                    p.y * dir.z - p.z * dir.y,
                    p.z * dir.x - p.x * dir.z,
                    p.x * dir.y - p.y * dir.x).Normalized;
                dir = (dir * Math.Cos(a) + bi * Math.Sin(a)).Normalized;

                sediment = 0f;
                float speed = 1f, water = 1f;
                float climate = 0.7f + 0.6f * (wetness != null && c < wetness.Length ? wetness[c] : 0.5f);

                for (int step = 0; step < MaxLifetime; step++)
                {
                    int best = -1;
                    float bestScore = 0f;
                    Vector3D bestTangent = dir;

                    float hc = height[c];                    // 本格高度只读一次
                    int start = grid.NeighborStart[c], end = grid.NeighborStart[c + 1];
                    for (int k = start; k < end; k++)
                    {
                        int nb = grid.Neighbors[k];
                        float drop = (hc - height[nb]) * invMaxLand;

                        // 切向已经预计算好，这里只有三次乘法两次加法，且全为 float
                        float tx = tanX[k], ty = tanY[k], tz = tanZ[k];
                        float align = (float)(tx * dir.x + ty * dir.y + tz * dir.z);
                        if (align < -0.3f)
                            continue;                   // 不逆行

                        float score = drop + 0.30f * align;
                        if (best < 0 || score > bestScore)
                        {
                            bestScore = score;
                            best = nb;
                            bestTangent = new Vector3D(tx, ty, tz);
                        }
                    }

                    if (best < 0)
                        break;

                    double dh = (hc - height[best]) * invMaxLand;
                    double capacity = Math.Max(dh, MinSlope) * speed * water * CapacityFactor;

                    if (sediment > capacity)
                    {
                        // 过饱和 → 沉积。约束「沉积量 ∈ [0, 携带量]」：
                        // capacity 为负时（上坡且 speed 归零）(sediment - capacity) 会大于携带量，
                        // 若不截断，sediment 会被减成负数并在后续步里放大。
                        // 实测这段约束在当前地形上从未触发（命中计数为 0），属于防御性加固，
                        // 不改变任何输出（改前改后高度场逐位相同）。
                        double deposit = (sediment - capacity) * DepositFactor;
                        if (!(deposit > 0.0))
                            deposit = 0.0;
                        else if (deposit > sediment)
                            deposit = sediment;

                        height[c] += (float)deposit;
                        sediment -= (float)deposit;
                        if (!float.IsFinite(sediment))
                            sediment = 0f;
                    }
                    else
                    {
                        // 未饱和 → 冲刷。冲刷量不超过「本格相对下一格的高差」所蕴含的物质，
                        // 因此单步下切总是收敛的。
                        float erode = (float)Math.Min((capacity - sediment) * ErodeFactor * climate,
                                                      Math.Max(0.0, dh) * maxLand);
                        if (!(erode > 0f))
                            erode = 0f;
                        height[c] -= erode;
                        sediment += erode;
                    }

                    dir = (dir * Inertia + bestTangent * (1.0 - Inertia)).Normalized;
                    c = best;

                    speed = (float)Math.Sqrt(Math.Max(0.0, speed * speed + dh * Gravity));
                    water *= 1f - EvaporateFactor;
                    if (water < 0.01f)
                        break;

                    if (height[c] <= 0f)
                    {
                        // 入海：把剩下的泥沙留在河口附近
                        height[c] += sediment * 0.5f;
                        break;
                    }
                }
            }

            // 热力侵蚀：坡降超过休止角时把物质搬到更低的邻居
            var delta = new float[n];
            for (int pass = 0; pass < ThermalIterations; pass++)
            {
                Array.Clear(delta, 0, n);
                for (int i = 0; i < n; i++)
                {
                    if (height[i] <= 0f)
                        continue;

                    int start = grid.NeighborStart[i], end = grid.NeighborStart[i + 1];
                    int lower = -1;
                    float lowest = height[i];
                    for (int k = start; k < end; k++)
                    {
                        int nb = grid.Neighbors[k];
                        if (height[nb] < lowest)
                        {
                            lowest = height[nb];
                            lower = nb;
                        }
                    }

                    if (lower < 0)
                        continue;

                    float drop = height[i] - lowest;
                    if (drop * TalusSlope < 1f && drop < 250f)
                        continue;                       // 未超过休止角

                    float move = drop * ThermalRate * 0.25f;
                    delta[i] -= move;
                    delta[lower] += move;
                }

                for (int i = 0; i < n; i++)
                    height[i] += delta[i];
            }
        }
    }
}
