using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 气候场：到海距离与湿度。水文生成（季节性河流、沙漠沿途渗漏）与
    /// 地形分类（沙漠/苔原/雨林）共用同一套判定，保证两者一致。
    /// 仅使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class ClimateModel
    {
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>湿度低于此值的地区，河流为季节性（间歇性）河流。</summary>
        public const double AridMoisture = 0.32;

        /// <summary>半干旱上限：集水区湿度低于此值时才可能成为季节性河流。</summary>
        public const double SemiAridMoisture = 0.42;

        /// <summary>湿度低于此值的地区，河流沿途强烈渗漏蒸发、可能中途消失。</summary>
        public const double VeryAridMoisture = 0.22;

        /// <summary>
        /// BFS 计算每格到海洋的距离（格数，陆地为正）。x 方向环绕、y 方向钳制。
        /// </summary>
        public static int[] SeaDistance(int width, int height, float[] heightField)
        {
            int w = width, h = height;
            int[] dist = new int[w * h];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;

            var queue = new Queue<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (heightField[i] <= 0f)
                {
                    dist[i] = 0;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;
                int d = dist[idx];

                for (int k = 0; k < 8; k++)
                {
                    int ny = y + DY8[k];
                    if (ny < 0 || ny >= h)
                        continue;

                    int nIdx = ny * w + ((x + DX8[k] + w) % w);
                    if (dist[nIdx] < 0)
                    {
                        dist[nIdx] = d + 1;
                        queue.Enqueue(nIdx);
                    }
                }
            }

            for (int i = 0; i < dist.Length; i++)
                if (dist[i] < 0)
                    dist[i] = 9999;

            return dist;
        }

        /// <summary>
        /// 湿度（0-1）：由纬度（大气环流）、距海距离（大陆性）与噪声共同决定。
        /// 副热带干旱带 + 内陆大陆性共同造成真正的沙漠，
        /// 而湿润的赤道/西风带沿岸则形成雨林与森林。
        /// </summary>
        public static double Moisture(double absLat, int seaDist, SeededNoise noise, Vector3D pos)
        {
            // 纬向基础湿度（大气环流）
            double baseMoisture;
            if (absLat < 10)
                baseMoisture = 0.95;                                     // 赤道辐合带（湿润）
            else if (absLat < 35)
            {
                double t = (absLat - 10.0) / 25.0;
                baseMoisture = 0.95 - t * 0.80;                          // 副热带干旱带（0.15）
            }
            else if (absLat < 60)
            {
                double t = (absLat - 35.0) / 25.0;
                baseMoisture = 0.30 + t * 0.40;                          // 西风带（0.70）
            }
            else
            {
                double t = Math.Min(1.0, (absLat - 60.0) / 30.0);
                baseMoisture = 0.45 - t * 0.10;                          // 极地干燥（0.35）
            }

            // 大陆性：距海越远越干燥
            double continentality = Math.Exp(-seaDist / 70.0);

            // 区域噪声变化
            double noiseFactor = (noise.Noise3D(pos.x * 4.0, pos.y * 4.0, pos.z * 4.0) - 0.5) * 0.30;

            double moisture = baseMoisture * (0.25 + 0.75 * continentality) + noiseFactor;
            return moisture < 0.0 ? 0.0 : (moisture > 1.0 ? 1.0 : moisture);
        }
    }
}
