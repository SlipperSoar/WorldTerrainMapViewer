using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// Classifies terrain types based on elevation, latitude, moisture,
    /// and hydrology data. Applies glacier rules for high latitudes.
    /// </summary>
    public static class TerrainClassifier
    {
        // 8-neighbour offsets (x wraps, y clamps)
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// Classify every pixel into a TerrainType.
        /// Steps 5–6 of the pipeline.
        /// </summary>
        public static TerrainType[] Classify(
            int width, int height, float[] heightField,
            int[] plateIds, HydrologyData hydrology, SeededNoise noise)
        {
            int w = width, h = height;
            TerrainType[] types = new TerrainType[w * h];

            // Step 5a: Compute distance-to-sea field (BFS from all ocean pixels)
            int[] seaDist = ComputeSeaDistance(w, h, heightField);

            // Step 5b: Classify each pixel
            for (int y = 0; y < h; y++)
            {
                double lat = 90.0 - (double)y / (h - 1) * 180.0;
                double absLat = Math.Abs(lat);

                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    float elev = heightField[idx];

                    // ── Rivers and lakes ──
                    if (hydrology.riverMask[idx])
                    {
                        types[idx] = TerrainType.River;
                        continue;
                    }
                    if (hydrology.lakeMask[idx])
                    {
                        types[idx] = TerrainType.Lake;
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

                    // ── Polar ice caps (land only) ──
                    // Permanent ice on land above 73° latitude
                    if (absLat > 73.0)
                    {
                        types[idx] = TerrainType.Glacier;
                        continue;
                    }

                    // Transition zone 68°–73°: noise-based glacier boundary on land
                    if (absLat > 68.0)
                    {
                        double transition = (absLat - 68.0) / 5.0;
                        Vector3D pos = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
                        double n = noise.Noise3D(pos.x * 5.0, pos.y * 5.0, pos.z * 5.0);
                        if (n < transition)
                        {
                            types[idx] = TerrainType.Glacier;
                            continue;
                        }
                    }

                    // ── Step 5: Land classification ──
                    Vector3D p = PlateTectonicsGenerator.PixelToSphere(x, y, w, h);
                    double moisture = ComputeMoisture(absLat, seaDist[idx], noise, p);

                    types[idx] = ClassifyLand(elev, absLat, moisture, seaDist[idx]);
                }
            }

            return types;
        }

        // ── Private helpers ──

        /// <summary>
        /// BFS from all ocean pixels to compute distance-to-sea for every pixel.
        /// </summary>
        private static int[] ComputeSeaDistance(int w, int h, float[] heightField)
        {
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

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = (x + dx + w) % w;
                        int nIdx = ny * w + nx;
                        if (dist[nIdx] < 0)
                        {
                            dist[nIdx] = d + 1;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            for (int i = 0; i < dist.Length; i++)
                if (dist[i] < 0)
                    dist[i] = 9999;

            return dist;
        }

        /// <summary>
        /// Compute moisture based on latitude, distance to sea, and noise.
        /// Models global atmospheric circulation patterns.
        /// </summary>
        private static double ComputeMoisture(
            double absLat, int seaDist, SeededNoise noise, Vector3D pos)
        {
            // Base moisture from latitude (atmospheric circulation)
            double baseMoisture;
            if (absLat < 10)
                baseMoisture = 0.8;                                      // Equatorial (ITCZ)
            else if (absLat < 35)
            {
                double t = (absLat - 10.0) / 25.0;
                baseMoisture = 0.8 - t * 0.6;                            // Subtropical dry zone
            }
            else if (absLat < 60)
            {
                double t = (absLat - 35.0) / 25.0;
                baseMoisture = 0.2 + t * 0.4;                            // Temperate westerlies
            }
            else
            {
                double t = (absLat - 60.0) / 30.0;
                baseMoisture = 0.6 - t * 0.5;                            // Polar dry
            }

            // Distance from sea factor
            double distFactor = 1.0 / (1.0 + seaDist * 0.005);

            // Noise variation
            double noiseVal = noise.Noise3D(pos.x * 4.0, pos.y * 4.0, pos.z * 4.0);
            double noiseFactor = (noiseVal - 0.5) * 0.4;

            double moisture = baseMoisture * 0.5 + distFactor * 0.3 + noiseFactor + 0.2;
            return Math.Max(0.0, Math.Min(1.0, moisture));
        }

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
