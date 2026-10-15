using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 单位球面上的等面积网格（细分二十面体 / icosphere）。
    ///
    /// 生成域与展示域分离：地形在球面的等面积格点上生成（没有极点奇点、没有左右接缝、
    /// 每格面积相同），再投影成等距圆柱图给查看器使用。
    /// 矩形图只是「地图」，不再是生成算法的真实几何。
    /// </summary>
    public sealed class SphereGrid
    {
        /// <summary>细分级别：顶点数 = 10·4^L + 2。</summary>
        public readonly int Level;

        /// <summary>每个格点的单位球面向量。</summary>
        public readonly Vector3D[] Vertices;

        /// <summary>每个格点的纬度 / 经度（度，经度 -180~180）。</summary>
        public readonly float[] Lat;
        public readonly float[] Lon;

        /// <summary>邻接表（CSR 存储）：第 i 个格点的邻居是 Neighbors[NeighborStart[i]..NeighborStart[i+1])。</summary>
        public readonly int[] NeighborStart;
        public readonly int[] Neighbors;

        /// <summary>
        /// 每条有向边的精确大圆长度（公里），下标与 <see cref="Neighbors"/> 一一对应。
        /// 气候、水文、人文统一用它作为球面度量，避免各模块各自近似一次
        /// （例如等距圆柱下的 cos(lat) 修正）。
        /// </summary>
        public readonly float[] EdgeLengthKm;

        /// <summary>地球半径（公里）。</summary>
        private const double EarthRadiusKm = 6371.0;

        /// <summary>三角形索引，每 3 个一组。</summary>
        public readonly int[] Faces;

        public int VertexCount => Vertices.Length;

        private SphereGrid(int level, List<Vector3D> vertices, List<int> faces,
            LongIntMap midpointCache)
        {
            Level = level;
            Faces = faces.ToArray();
            Vertices = new Vector3D[vertices.Count];
            Lat = new float[vertices.Count];
            Lon = new float[vertices.Count];

            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3D p = vertices[i];
                Vertices[i] = p;
                Lat[i] = (float)(Math.Asin(Math.Max(-1.0, Math.Min(1.0, p.y))) * 180.0 / Math.PI);
                Lon[i] = (float)(Math.Atan2(p.z, p.x) * 180.0 / Math.PI);
            }

            // 由面表统计无向边，生成 CSR 邻接表。
            // 去重同样用开地址映射：level 7 有约 49 万条边，
            // HashSet<long> 在这个量级上和细分用的 Dictionary 一样会拖慢整个构建。
            int faceCount = faces.Count / 3;
            var edgeIndex = new LongIntMap(faceCount * 2);
            var edgeA = new int[faceCount * 3];
            var edgeB = new int[faceCount * 3];
            int edgeCount = 0;

            for (int f = 0; f < faces.Count; f += 3)
            {
                AddUniqueEdge(edgeIndex, edgeA, edgeB, ref edgeCount, faces[f], faces[f + 1]);
                AddUniqueEdge(edgeIndex, edgeA, edgeB, ref edgeCount, faces[f + 1], faces[f + 2]);
                AddUniqueEdge(edgeIndex, edgeA, edgeB, ref edgeCount, faces[f + 2], faces[f]);
            }

            var degree = new int[vertices.Count];
            for (int e = 0; e < edgeCount; e++)
            {
                degree[edgeA[e]]++;
                degree[edgeB[e]]++;
            }

            NeighborStart = new int[vertices.Count + 1];
            for (int i = 0; i < vertices.Count; i++)
                NeighborStart[i + 1] = NeighborStart[i] + degree[i];

            Neighbors = new int[NeighborStart[vertices.Count]];
            var cursor = new int[vertices.Count];
            Array.Copy(NeighborStart, cursor, vertices.Count);

            for (int e = 0; e < edgeCount; e++)
            {
                int a = edgeA[e];
                int b = edgeB[e];
                Neighbors[cursor[a]++] = b;
                Neighbors[cursor[b]++] = a;
            }

            // 每条有向边的精确大圆长度（公里）。
            // 用弦长再转角度：dot 在近距离上精度差，2·asin(弦/2) 对小角度稳定。
            EdgeLengthKm = new float[Neighbors.Length];
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3D pi = Vertices[i];
                int s = NeighborStart[i], e = NeighborStart[i + 1];
                for (int k = s; k < e; k++)
                {
                    Vector3D d = Vertices[Neighbors[k]] - pi;
                    double chord = Math.Sqrt(d.Dot(d));
                    if (chord > 2.0)
                        chord = 2.0;
                    EdgeLengthKm[k] = (float)(2.0 * Math.Asin(chord * 0.5) * EarthRadiusKm);
                }
            }

            midpointCache?.Clear();
        }

        /// <summary>把一条无向边加入唯一边表（已存在则跳过）。</summary>
        private static void AddUniqueEdge(LongIntMap map, int[] edgeA, int[] edgeB,
            ref int edgeCount, int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;

            if (map.TryGet(key, out int _))
                return;

            map.Set(key, edgeCount);
            edgeA[edgeCount] = lo;
            edgeB[edgeCount] = hi;
            edgeCount++;
        }

        private static void AddEdge(HashSet<long> set, int a, int b)        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            set.Add(((long)lo << 32) | (uint)hi);
        }

        /// <summary>按目标格点数选择细分级别（格点数 ≈ 10·4^L + 2，上限 8 级）。</summary>
        public static int LevelForCellCount(int target)
        {
            int level = 0;
            int count = 12;
            while (count < target && level < 8)
            {
                level++;
                count = 10 * (1 << (2 * level)) + 2;
            }
            return level;
        }

        /// <summary>创建细分 level 级的球面网格。</summary>
        public static SphereGrid Create(int level)
        {
            if (level < 0) level = 0;
            if (level > 8) level = 8;

            double t = (1.0 + Math.Sqrt(5.0)) * 0.5;
            var verts = new List<Vector3D>
            {
                new Vector3D(-1, t, 0), new Vector3D(1, t, 0), new Vector3D(-1, -t, 0), new Vector3D(1, -t, 0),
                new Vector3D(0, -1, t), new Vector3D(0, 1, t), new Vector3D(0, -1, -t), new Vector3D(0, 1, -t),
                new Vector3D(t, 0, -1), new Vector3D(t, 0, 1), new Vector3D(-t, 0, -1), new Vector3D(-t, 0, 1)
            };
            for (int i = 0; i < verts.Count; i++)
                verts[i] = verts[i].Normalized;

            var faces = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };

            // 容量按 level 8 的最坏情况预留：最后一级新增约 49 万个中点
            var cache = new LongIntMap(1 << 20);
            for (int l = 0; l < level; l++)
            {
                var next = new List<int>(faces.Count * 4);
                for (int f = 0; f < faces.Count; f += 3)
                {
                    int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                    int ab = Midpoint(verts, cache, a, b);
                    int bc = Midpoint(verts, cache, b, c);
                    int ca = Midpoint(verts, cache, c, a);

                    next.Add(a); next.Add(ab); next.Add(ca);
                    next.Add(b); next.Add(bc); next.Add(ab);
                    next.Add(c); next.Add(ca); next.Add(bc);
                    next.Add(ab); next.Add(bc); next.Add(ca);
                }
                faces = next;
                cache.Clear();
            }

            return new SphereGrid(level, verts, faces, cache);
        }

        private static int Midpoint(List<Vector3D> verts, LongIntMap cache, int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            if (cache.TryGet(key, out int cached))
                return cached;

            Vector3D mid = ((verts[a] + verts[b]) * 0.5).Normalized;
            verts.Add(mid);
            int index = verts.Count - 1;
            cache.Set(key, index);
            return index;
        }

        /// <summary>球面上的拉普拉斯平滑（平面高斯模糊的球面等价物）。</summary>
        public void LaplacianSmooth(float[] values, int passes, float rate)
        {
            int n = values.Length;
            var buffer = new float[n];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    int start = NeighborStart[i], end = NeighborStart[i + 1];
                    if (end <= start)
                    {
                        buffer[i] = values[i];
                        continue;
                    }

                    float sum = 0f;
                    for (int k = start; k < end; k++)
                        sum += values[Neighbors[k]];

                    buffer[i] = values[i] + rate * (sum / (end - start) - values[i]);
                }

                Array.Copy(buffer, values, n);
            }
        }

        /// <summary>
        /// 把格点标量场投影成等距圆柱图。
        ///
        /// 做法是把球面三角网逐三角形栅格化（重心坐标插值），而不是按像素做最近邻：
        /// 后者是「像素空间」的 Voronoi，在高纬度会把球面上相邻的格点拉成条纹
        /// （极点附近经线间距极小，像素空间的距离完全失真）。
        /// 跨 0°/360° 的三角形会平移一份重复绘制，因此左右天然无缝。
        /// 极少数没被任何三角形覆盖的像素再按最近格点补齐。
        /// </summary>
        public float[] ProjectToEquirectangular(int w, int h, float[] vertexValues)
        {
            int n = w * h;
            var grid = new float[n];
            var filled = new bool[n];

            var px = new double[3];
            var py = new double[3];

            for (int f = 0; f < Faces.Length; f += 3)
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;

                for (int k = 0; k < 3; k++)
                {
                    int v = Faces[f + k];
                    px[k] = (Lon[v] + 180.0) / 360.0 * w;
                    py[k] = (90.0 - Lat[v]) / 180.0 * h;
                }

                // 求「覆盖这三个点的最小圆弧」作为扫描窗口。
                // 只按原始经度取包围盒会在高纬炸掉：纬度越高，同一个球面小三角形
                // 跨的经度越宽（极点附近可以横跨大半个圆周），包围盒里绝大多数像素
                // 其实落在三角形之外，扫描它们纯属浪费 —— 之前球面网格一提级就慢十几倍就是这个原因。
                int i0 = 0, i1 = 1, i2 = 2;
                if (px[i0] > px[i1]) { int t = i0; i0 = i1; i1 = t; }
                if (px[i1] > px[i2]) { int t = i1; i1 = i2; i2 = t; }
                if (px[i0] > px[i1]) { int t = i0; i0 = i1; i1 = t; }

                double s0 = px[i0], s1 = px[i1], s2 = px[i2];
                double gap1 = s1 - s0, gap2 = s2 - s1, gap3 = s0 + w - s2;

                if (gap1 >= gap2 && gap1 >= gap3)
                {
                    px[i0] += w;                 // 窗口 [s1, s0+w]
                }
                else if (gap2 >= gap1 && gap2 >= gap3)
                {
                    px[i0] += w;                 // 窗口 [s2, s1+w]
                    px[i1] += w;
                }
                // 否则最大缺口跨 0°/360°：窗口就是 [s0, s2]，不平移

                for (int k = 0; k < 3; k++)
                {
                    if (px[k] < minX) minX = px[k];
                    if (px[k] > maxX) maxX = px[k];
                    if (py[k] < minY) minY = py[k];
                    if (py[k] > maxY) maxY = py[k];
                }

                int y0 = (int)Math.Floor(minY), y1 = (int)Math.Ceiling(maxY);
                int x0 = (int)Math.Floor(minX), x1 = (int)Math.Ceiling(maxX);
                if (y0 < 0) y0 = 0;
                if (y1 > h - 1) y1 = h - 1;
                if (x1 - x0 > w) { x0 = 0; x1 = w - 1; }

                double x0d = px[0], y0d = py[0];
                double x1d = px[1], y1d = py[1];
                double x2d = px[2], y2d = py[2];
                double denom = (y1d - y2d) * (x0d - x2d) + (x2d - x1d) * (y0d - y2d);
                if (Math.Abs(denom) < 1e-12)
                    continue;

                float v0 = vertexValues[Faces[f]];
                float v1 = vertexValues[Faces[f + 1]];
                float v2 = vertexValues[Faces[f + 2]];

                // 高度在三角形内是 (x,y) 的线性函数：先解出平面，再按扫描线只填真实覆盖的区间。
                // 之前是「包围盒 + 逐像素重心判定」：包围盒面积可能远大于三角形面积
                // （高纬的细长三角形尤其如此），网格一提级就成了主要瓶颈。
                double planeA = ((y1d - y2d) * (v0 - v2) + (y2d - y0d) * (v1 - v2)) / denom;
                double planeB = ((x2d - x1d) * (v0 - v2) + (x0d - x2d) * (v1 - v2)) / denom;
                double planeC = v0 - planeA * x0d - planeB * y0d;

                for (int y = y0; y <= y1; y++)
                {
                    // 该行与三条边的交点 → 凸三角形的覆盖区间（单一线段）
                    double cy = y + 0.5;
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int e = 0; e < 3; e++)
                    {
                        int ea = e, eb = (e + 1) % 3;
                        double ya = py[ea], yb = py[eb];
                        if (cy < Math.Min(ya, yb) - 0.5 || cy > Math.Max(ya, yb) + 0.5)
                            continue;

                        double t = Math.Abs(yb - ya) < 1e-9 ? 0.0 : (cy - ya) / (yb - ya);
                        if (t < 0.0) t = 0.0;
                        if (t > 1.0) t = 1.0;

                        double xc = px[ea] + (px[eb] - px[ea]) * t;
                        if (xc < lo) lo = xc;
                        if (xc > hi) hi = xc;
                    }

                    if (hi < lo)
                        continue;

                    int xs = (int)Math.Floor(lo - 0.5);
                    int xe = (int)Math.Ceiling(hi + 0.5);
                    if (xs < x0) xs = x0;
                    if (xe > x1) xe = x1;

                    double rowValue = planeA * (xs + 0.5) + planeB * cy + planeC;
                    for (int x = xs; x <= xe; x++)
                    {
                        int gx = ((x % w) + w) % w;
                        int idx = y * w + gx;

                        grid[idx] = (float)rowValue;
                        filled[idx] = true;
                        rowValue += planeA;          // 沿线递推，省掉逐像素乘法
                    }
                }
            }

            // 兜底：仍有没被覆盖的像素（三角形数值退化）→ 从最近的已填像素扩散
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (filled[i])
                    queue.Enqueue(i);

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w, y = idx / w;
                float value = grid[idx];

                for (int d = 0; d < 4; d++)
                {
                    int ny = y + (d == 0 ? -1 : (d == 1 ? 1 : 0));
                    int nx = x + (d == 2 ? -1 : (d == 3 ? 1 : 0));
                    if (ny < 0 || ny >= h)
                        continue;
                    nx = (nx + w) % w;

                    int j = ny * w + nx;
                    if (filled[j])
                        continue;

                    filled[j] = true;
                    grid[j] = value;
                    queue.Enqueue(j);
                }
            }

            // 接缝处两列来自不同的三角形副本，取平均让 0°/360° 完全对上
            for (int y = 0; y < h; y++)
            {
                int a = y * w;
                int b = y * w + w - 1;
                float avg = (grid[a] + grid[b]) * 0.5f;
                grid[a] = avg;
                grid[b] = avg;
            }

            return grid;
        }

        /// <summary>
        /// long → int 的开地址哈希表（键为非负的长整型，-1 作空槽标记）。
        /// 细分二十面体时要做几十万次「这条边的中点建过没有」查询，
        /// Dictionary&lt;long,int&gt; 在这个量级上会慢一个数量级 —— 换成一套连续内存的开放寻址后，
        /// 网格构建从十几秒降到毫秒级。
        /// </summary>
        private sealed class LongIntMap
        {
            private readonly long[] _keys;
            private readonly int[] _values;
            private readonly int _mask;

            public LongIntMap(int capacity)
            {
                int size = 16;
                while (size < capacity * 2)
                    size <<= 1;

                _keys = new long[size];
                _values = new int[size];
                _mask = size - 1;

                for (int i = 0; i < size; i++)
                    _keys[i] = -1;
            }

            /// <summary>清空（重新填满空槽标记）。</summary>
            public void Clear()
            {
                for (int i = 0; i < _keys.Length; i++)
                    _keys[i] = -1;
            }

            private static int Hash(long key)
            {
                ulong x = (ulong)key;
                x ^= x >> 33;
                x *= 0xff51afd7ed558ccdUL;
                x ^= x >> 33;
                x *= 0xc4ceb9fe1a85ec53UL;
                x ^= x >> 33;
                return (int)x;
            }

            public bool TryGet(long key, out int value)
            {
                int i = Hash(key) & _mask;
                while (_keys[i] != -1)
                {
                    if (_keys[i] == key)
                    {
                        value = _values[i];
                        return true;
                    }
                    i = (i + 1) & _mask;
                }

                value = 0;
                return false;
            }

            public void Set(long key, int value)
            {
                int i = Hash(key) & _mask;
                while (_keys[i] != -1 && _keys[i] != key)
                    i = (i + 1) & _mask;

                _keys[i] = key;
                _values[i] = value;
            }
        }

        private static double Max3(double[] v)
        {
            double m = v[0];
            if (v[1] > m) m = v[1];
            if (v[2] > m) m = v[2];
            return m;
        }

        private static double Min3(double[] v)
        {
            double m = v[0];
            if (v[1] < m) m = v[1];
            if (v[2] < m) m = v[2];
            return m;
        }
    }
}
