using System;

namespace WorldTerrain
{
    /// <summary>
    /// 球面排水的中间量：填洼后的水面、流向与汇流累积。
    ///
    /// 这些量原先只在 <see cref="SphereDrainage.InciseValleys"/> 内部使用，只把汇流量输出给水文。
    /// 现在一并交出来，是为了让「洼地有多深、网格有多粗」可以直接被外部检查 ——
    /// 正是这两个量证明了一件事：本网格（1024×512 → level 7，平均格面积 3,619 km²）
    /// 比投影像素（973 km²）粗 3.7 倍，Priority-Flood 会把相邻台地并成几千米深的假盆地，
    /// 因此**湖泊判定不能挪到这一级网格上做**（结论与实测见 README 的「待办 2」）。
    /// </summary>
    public struct SphereDrainageData
    {
        /// <summary>填洼后的水面高程（米）：洼地被抬到溢出高程，其余等于真实高度。</summary>
        public float[] Filled;

        /// <summary>每格的唯一流向（-1 = 海洋或没有更低邻格）。</summary>
        public int[] Receiver;

        /// <summary>汇流累积量（本地产流 + 上游来水）。</summary>
        public float[] Accum;

        /// <summary>与 <see cref="Accum"/> 等长的副本（下游水文可直接带走，不依赖上面的数组生命周期）。</summary>
        public float[] AccumOut;
    }

    /// <summary>
    /// 球面排水网络：在等面积球面网格上做 Priority-Flood 填洼 → 最陡下降流向 →
    /// 汇流累积 → 按汇水量下切河谷（stream power）。
    ///
    /// 意义：河谷会沿球面上的真实分水岭生长，而不是沿投影网格的行列 ——
    /// 高纬不会被过度或不足地切割，也没有经度接缝与极点奇点。
    /// 下切之后再叠加热力/水力侵蚀，地形就有了「先有河道、再被磨圆」的自然层次。
    /// </summary>
    public static class SphereDrainage
    {
        /// <summary>填洼的抬升步长（米）：只要保证单调上升即可。</summary>
        private const float FillEpsilon = 0.01f;

        /// <summary>形成河道的最小汇水权重（低于此值算坡面漫流，不下切）。</summary>
        private const float ChannelThreshold = 1.5f;

        /// <summary>单点最大下切深度（米）。</summary>
        private const float MaxIncision = 900f;

        /// <summary>
        /// 在球面格点上雕刻河谷。
        /// </summary>
        /// <param name="wetness">每格的湿度（0-1，通常取风带纬向剖面）；决定产流与下切强度</param>
        /// <param name="incisionScale">下切强度：深度 ≈ scale · √汇水面积 · 湿润系数</param>
        /// <param name="accumOut">汇流量输出（供水文判定河道），可为 null</param>
        /// <returns>填洼面 / 流向 / 汇流量（诊断与网格粒度检查用），不需要时忽略即可</returns>
        public static SphereDrainageData InciseValleys(SphereGrid grid, float[] height, float[] wetness,
            float incisionScale, SeededNoise noise = null, float[] accumOut = null)
        {
            int n = grid.VertexCount;
            if (n == 0)
                return default;

            // ── 1. Priority-Flood 填洼（从海洋向内陆推进）──
            var filled = new float[n];
            var visited = new bool[n];
            var heap = new MinHeap(n + 1);

            for (int i = 0; i < n; i++)
            {
                if (height[i] <= 0f)
                {
                    filled[i] = height[i];
                    visited[i] = true;
                    heap.Push(height[i], i);
                }
            }

            while (heap.Count > 0)
            {
                int c = heap.Pop(out float z);
                int start = grid.NeighborStart[c], end = grid.NeighborStart[c + 1];
                for (int k = start; k < end; k++)
                {
                    int nb = grid.Neighbors[k];
                    if (visited[nb])
                        continue;
                    visited[nb] = true;
                    float f = height[nb];
                    if (f < z + FillEpsilon)
                        f = z + FillEpsilon;
                    filled[nb] = f;
                    heap.Push(f, nb);
                }
            }

            // ── 2. 最陡下降流向（用真实高度，避免填出来的平台互相指环）──
            // 每个 i 只写自己的 receiver[i] / drop[i]，读的是 filled / height / 邻接表，
            // 全程只读，因此并行与串行逐位相同。
            var receiver = new int[n];
            var drop = new float[n];
            Parallelism.For(n, i =>
            {
                receiver[i] = -1;
                if (height[i] <= 0f)
                    return;

                int best = -1;
                float bestDrop = 0f;
                int start = grid.NeighborStart[i], end = grid.NeighborStart[i + 1];
                for (int k = start; k < end; k++)
                {
                    int nb = grid.Neighbors[k];

                    // 关键：流向必须沿「填洼后的面」选，而不是原始高度
                    // （按原始高度选时水流每几格就断一次）。
                    // 平整的填洼平台上各邻居 filled 完全相等，必须靠「逐格互不相关」的抖动定方向：
                    // 不能用平滑噪声 —— 40× 频率的 Fbm 波长约 24 格，相邻格取值几乎相同，
                    // 等于没有抖动（实测最大汇流量只有 35，流域全是碎的）。
                    float d = filled[i] - filled[nb] + Jitter(nb);
                    if (d > bestDrop)
                    {
                        bestDrop = d;
                        best = nb;
                    }
                }

                receiver[i] = best;
                drop[i] = bestDrop > 0f ? bestDrop : 0f;
            });

            // ── 3. 汇流累积：按 filled 从高到低累加 ──
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            Array.Sort(filled, order);

            var accum = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (height[i] <= 0f)
                {
                    accum[i] = 0f;
                    continue;
                }
                double w = wetness != null && i < wetness.Length ? wetness[i] : 0.6;
                accum[i] = (float)(0.15 + 1.2 * w);          // 产流：湿润区更多
            }

            for (int i = n - 1; i >= 0; i--)
            {
                int c = order[i];
                int r = receiver[c];
                if (r >= 0)
                    accum[r] += accum[c];
            }

            // 汇流量可作为「球面径流场」输出：投影后就是每格的汇水权重，
            // 供水文模块判定河道 —— 河网因此沿球面真实汇流走向生长，
            // 而不是沿投影网格的行列方向。
            if (accumOut != null && accumOut.Length >= n)
                Array.Copy(accum, accumOut, n);

            var result = new SphereDrainageData
            {
                Filled = filled,
                Receiver = receiver,
                Accum = accum,
                AccumOut = accumOut
            };

            // ── 4. 按汇水量下切河谷，两岸略微抬升 → 形成 V 形谷 ──
            var incision = new float[n];
            Parallelism.For(n, i =>
            {
                if (height[i] <= 0f || accum[i] < ChannelThreshold)
                    return;

                double w = wetness != null && i < wetness.Length ? wetness[i] : 0.6;
                double depth = incisionScale * Math.Sqrt(accum[i]) * (0.5 + 0.9 * w);
                if (depth > MaxIncision)
                    depth = MaxIncision;
                incision[i] = (float)depth;
            });

            // 这一步会把 incision[i] 散播到邻居格子，不能并行（会写别人的槽位）
            for (int i = 0; i < n; i++)
            {
                if (incision[i] <= 0f)
                    continue;

                height[i] -= incision[i];

                int start = grid.NeighborStart[i], end = grid.NeighborStart[i + 1];
                for (int k = start; k < end; k++)
                {
                    int nb = grid.Neighbors[k];
                    if (height[nb] > 0f)
                        height[nb] += incision[i] * 0.10f;    // 两岸抬升，谷形更立体
                }
            }

            return result;
        }

        /// <summary>
        /// 逐格抖动（米）：只用于平坦区的流向定序。幅度与平面版的 RoutingJitterMeters 一致（8 m），
        /// 且相邻格之间互不相关 —— 这是平坦区能定出唯一流向的前提。
        /// </summary>
        private static float Jitter(int index)
        {
            uint x = (uint)index;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0xFFFF) * (8f / 65536f);
        }

        /// <summary>以 float 为键、int 为值的二叉最小堆（球面格点数量不大，够用且无分配）。</summary>
        private sealed class MinHeap
        {
            private readonly float[] _keys;
            private readonly int[] _values;
            private int _count;

            public MinHeap(int capacity)
            {
                _keys = new float[capacity];
                _values = new int[capacity];
            }

            public int Count => _count;

            public void Push(float key, int value)
            {
                if (_count >= _keys.Length)
                    return;                       // 容量已按格点数预留，正常不会发生

                int i = _count++;
                _keys[i] = key;
                _values[i] = value;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= _keys[i])
                        break;
                    Swap(parent, i);
                    i = parent;
                }
            }

            public int Pop(out float key)
            {
                int result = _values[0];
                key = _keys[0];

                _count--;
                if (_count > 0)
                {
                    _keys[0] = _keys[_count];
                    _values[0] = _values[_count];

                    int i = 0;
                    while (true)
                    {
                        int left = i * 2 + 1;
                        int right = left + 1;
                        int smallest = i;
                        if (left < _count && _keys[left] < _keys[smallest])
                            smallest = left;
                        if (right < _count && _keys[right] < _keys[smallest])
                            smallest = right;
                        if (smallest == i)
                            break;
                        Swap(i, smallest);
                        i = smallest;
                    }
                }

                return result;
            }

            private void Swap(int a, int b)
            {
                float tk = _keys[a];
                _keys[a] = _keys[b];
                _keys[b] = tk;
                int tv = _values[a];
                _values[a] = _values[b];
                _values[b] = tv;
            }
        }
    }
}
