using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// Generates tectonic plates and simulates drift to produce a height field.
    /// All methods are thread-safe (System.Math only, no UnityEngine).
    /// </summary>
    public static class PlateTectonicsGenerator
    {
        // 8-neighbour offsets (x wraps, y clamps)
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// 地壳均衡幅度（米）：陆壳（硅铝层）比洋壳（硅镁层）更厚更轻，
        /// 均衡补偿使大陆整体高出洋底数公里。
        /// </summary>
        private const double IsostasyAmplitude = 5000.0;

        /// <summary>陆壳起伏放大系数：造山带集中在陆壳，洋壳更平缓（深海平原）。</summary>
        private const double ContinentalReliefScale = 0.9;

        /// <summary>转换到 [0,1] 的平滑过渡（用于地壳类型等软掩膜）。</summary>
        private static double SmoothStep(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// Convert equirectangular pixel coordinates to a 3D unit-sphere position.
        /// x=0 → lon=-π, x=width-1 → lon≈π, y=0 → north pole, y=height-1 → south pole.
        /// </summary>
        public static Vector3D PixelToSphere(int px, int py, int width, int height)
        {
            double lon = (double)px / (double)(width - 1) * 2.0 * Math.PI - Math.PI;
            double lat = Math.PI * 0.5 - (double)py / (double)(height - 1) * Math.PI;

            double cosLat = Math.Cos(lat);
            return new Vector3D(
                cosLat * Math.Cos(lon),
                Math.Sin(lat),
                cosLat * Math.Sin(lon)
            );
        }

        /// <summary>
        /// Step 1: Generate 4–8 random tectonic plates on the unit sphere.
        /// </summary>
        public static PlateInfo[] GeneratePlates(WorldGenConfig config, Random rng)
        {
            int count = rng.Next(config.minPlates, config.maxPlates + 1);
            var plates = new PlateInfo[count];

            for (int i = 0; i < count; i++)
            {
                // Uniform random point on sphere
                double u = rng.NextDouble();
                double v = rng.NextDouble();
                double theta = 2.0 * Math.PI * u;
                double phi = Math.Acos(2.0 * v - 1.0);
                double sinPhi = Math.Sin(phi);

                plates[i].center = new Vector3D(
                    sinPhi * Math.Cos(theta),
                    Math.Cos(phi),
                    sinPhi * Math.Sin(theta)
                );

                plates[i].velocity = RandomUnitVector(rng);
                plates[i].driftSpeed = (float)(0.5 + rng.NextDouble() * 1.5);
            }

            return plates;
        }

        /// <summary>
        /// Voronoi plate assignment using dot product on 3D sphere positions.
        /// Noise perturbation on pixel positions creates irregular, zigzag boundaries.
        /// </summary>
        public static int[] AssignPlates(int width, int height, PlateInfo[] plates, SeededNoise noise)
        {
            int[] plateIds = new int[width * height];
            double perturbStrength = 0.12;

            // 每行只写自己那一行的 plateIds，噪声是纯函数 → 并行逐位一致
            Parallelism.ForRows(height, y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Vector3D pos = PixelToSphere(x, y, width, height);

                    // 3-axis noise perturbation to create irregular boundaries
                    double px = noise.Noise3D(
                        pos.x * 4.0, pos.y * 4.0, pos.z * 4.0) - 0.5;
                    double py = noise.Noise3D(
                        pos.x * 4.0 + 10, pos.y * 4.0 + 10, pos.z * 4.0 + 10) - 0.5;
                    double pz = noise.Noise3D(
                        pos.x * 4.0 + 20, pos.y * 4.0 + 20, pos.z * 4.0 + 20) - 0.5;

                    Vector3D perturbed = (pos + new Vector3D(px, py, pz) * perturbStrength).Normalized;

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

                    plateIds[row + x] = best;
                }
            });

            return plateIds;
        }

        /// <summary>
        /// Steps 2–3: Generate the height field from plate base elevations,
        /// fBm noise, and drift-induced boundary effects.
        /// </summary>
        public static float[] GenerateHeights(
            WorldGenConfig config, Random rng,
            int[] plateIds, PlateInfo[] plates, SeededNoise noise)
        {
            int w = config.width, h = config.height;
            float[] heightField = new float[w * h];
            float[] continental = new float[w * h];

            // (a) Pure noise-based height field — independent of plates.
            //     Land/sea emerges naturally where the field crosses 0m.
            //     同时按低频噪声区分陆壳 / 洋壳，并施加地壳均衡与起伏差异。
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    Vector3D pos = PixelToSphere(x, y, w, h);

                    // Domain warping: offset coordinates with a separate noise field
                    double warpX = noise.Noise3D(
                        pos.x * 2.0, pos.y * 2.0, pos.z * 2.0) * 0.3;
                    double warpY = noise.Noise3D(
                        pos.x * 2.0 + 100, pos.y * 2.0 + 100, pos.z * 2.0 + 100) * 0.3;

                    // 地壳类型：低频噪声 → 0 洋壳、1 陆壳（大陆成片而非随机碎块）
                    double craton = noise.Fbm3D(
                        pos.x * 1.1 + 17.0, pos.y * 1.1 + 17.0, pos.z * 1.1, 3, 0.5, 2.0);
                    double cont = SmoothStep((craton - 0.36) / 0.28);
                    continental[idx] = (float)cont;

                    // Layer 1: Large-scale terrain (mountain ranges, basins)
                    double n1 = noise.Fbm3D(
                        (pos.x + warpX) * 1.5,
                        (pos.y + warpY) * 1.5,
                        pos.z * 1.5, 4, 0.5, 2.0);

                    // Layer 2: Medium-scale (hills, valleys)
                    double n2 = noise.Fbm3D(
                        pos.x * 5.0, pos.y * 5.0, pos.z * 5.0, 5, 0.5, 2.0);

                    // Layer 3: Small-scale detail
                    double n3 = noise.Fbm3D(
                        pos.x * 15.0, pos.y * 15.0, pos.z * 15.0, 4, 0.5, 2.0);

                    // 陆壳起伏更大（造山带），洋壳更平缓（深海平原）
                    double reliefScale = 0.55 + ContinentalReliefScale * cont;

                    // 地壳均衡：陆壳整体抬升、洋壳整体下沉
                    double isostasy = (cont - 0.5) * IsostasyAmplitude;

                    // Map each layer from [0,1] to [-1,1], then scale — centered at 0
                    heightField[idx] = (float)(
                        (((n1 * 2.0 - 1.0) * 2500.0)    // [-2500, 2500]
                       + ((n2 * 2.0 - 1.0) * 1200.0)    // [-1200, 1200]
                       + ((n3 * 2.0 - 1.0) * 500.0))    // [-500, 500]
                        * reliefScale + isostasy);
                }
            }

            // (b) Boundary effects from plate drift simulation（大陆边缘造山更强）
            float[] boundaryEffect = ComputeBoundaryEffects(w, h, plateIds, plates, rng, noise, continental);

            // (c) Gaussian blur to diffuse boundary effects (sigma=40 → ~120px spread)
            float[] blurred = GaussianBlur(w, h, boundaryEffect, 40.0);

            // (d) Combine and clamp
            for (int i = 0; i < w * h; i++)
            {
                heightField[i] += blurred[i];
                heightField[i] = Math.Max(config.minElevation,
                                Math.Min(config.maxElevation, heightField[i]));
            }

            // (e) Water coverage normalization: shift heights so that the desired
            //     fraction of pixels falls below sea level (0m).
            ApplyWaterCoverage(w, h, heightField, config.waterCoverage);

            // (f) Continental shelf: gradual ocean floor transition near coastlines
            ApplyContinentalShelf(w, h, heightField, noise);

            // (g) Ensure pole rows are uniform (top = north pole, bottom = south pole)
            EnsurePoleUniformity(w, h, heightField);

            return heightField;
        }

        // ── Private helpers ──

        /// <summary>
        /// Shift all heights so that the given fraction of pixels is below sea level (0m).
        /// Uses a 256-bin histogram to find the percentile height, then offsets.
        /// waterCoverage=0.5 → 50% ocean; 0.7 → 70% ocean; 0.3 → 30% ocean.
        /// 侵蚀会整体降低陆地，因此侵蚀后需要再调用一次，保持海陆比例不变。
        /// </summary>
        public static void ApplyWaterCoverage(int w, int h, float[] heightField, float waterCoverage)
        {
            float wc = (float)Math.Max(0.0, Math.Min(1.0, (double)waterCoverage));
            if (wc <= 0f || wc >= 1f)
                return;

            // Build 256-bin histogram
            int bins = 256;
            int[] histogram = new int[bins];
            float minH = float.MaxValue;
            float maxH = float.MinValue;

            for (int i = 0; i < heightField.Length; i++)
            {
                if (heightField[i] < minH) minH = heightField[i];
                if (heightField[i] > maxH) maxH = heightField[i];
            }

            float range = maxH - minH;
            if (range < 1f)
                return;

            for (int i = 0; i < heightField.Length; i++)
            {
                int bin = (int)((heightField[i] - minH) / range * (bins - 1));
                if (bin < 0) bin = 0;
                if (bin >= bins) bin = bins - 1;
                histogram[bin]++;
            }

            // Find the height at the waterCoverage percentile
            int target = (int)(heightField.Length * wc);
            int cumulative = 0;
            float percentileHeight = minH;

            for (int b = 0; b < bins; b++)
            {
                cumulative += histogram[b];
                if (cumulative >= target)
                {
                    percentileHeight = minH + (float)b / (bins - 1) * range;
                    break;
                }
            }

            // Shift all heights so the percentile maps to 0m (sea level)
            float offset = percentileHeight;
            for (int i = 0; i < heightField.Length; i++)
            {
                heightField[i] -= offset;
            }
        }

        /// <summary>
        /// Create gradual continental shelf transitions near coastlines.
        /// Most coastal areas get a shallow shelf; ~15% keep steep drops.
        /// </summary>
        public static void ApplyContinentalShelf(
            int w, int h, float[] heightField, SeededNoise noise)
        {
            // (1) BFS distance from land to all ocean pixels
            int[] distFromLand = new int[w * h];
            for (int i = 0; i < distFromLand.Length; i++)
                distFromLand[i] = -1;

            var queue = new System.Collections.Generic.Queue<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (heightField[i] > 0f)
                {
                    distFromLand[i] = 0;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;
                int d = distFromLand[idx];

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
                        if (distFromLand[nIdx] < 0)
                        {
                            distFromLand[nIdx] = d + 1;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            // (2) Raise ocean floor near coast to create continental shelf + slope
            int shelfWidth = 150;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (heightField[idx] > 0f)
                        continue; // skip land

                    int dist = distFromLand[idx];
                    if (dist < 0 || dist > shelfWidth)
                        continue;

                    Vector3D pos = PixelToSphere(x, y, w, h);
                    double shelfNoise = noise.Noise3D(
                        pos.x * 3.0, pos.y * 3.0, pos.z * 3.0);

                    // ~15% of coastal areas keep steep drops (no shelf)
                    if (shelfNoise < 0.15)
                        continue;

                    float targetH;
                    if (dist <= 50)
                    {
                        // Continental shelf: shallow transition (-200m → -800m)
                        double shelfT = (double)dist / 50.0;
                        targetH = (float)(-200.0 - 600.0 * shelfT);
                    }
                    else
                    {
                        // Continental slope: medium-to-deep transition (-800m → -2500m)
                        double slopeT = (double)(dist - 50) / 100.0;
                        targetH = (float)(-800.0 - 1700.0 * slopeT);
                    }

                    if (heightField[idx] < targetH)
                    {
                        heightField[idx] = (float)(heightField[idx] * 0.3 + targetH * 0.7);
                    }
                }
            }
        }

        private static Vector3D RandomUnitVector(Random rng)
        {
            double u = rng.NextDouble();
            double v = rng.NextDouble();
            double theta = 2.0 * Math.PI * u;
            double phi = Math.Acos(2.0 * v - 1.0);
            double sinPhi = Math.Sin(phi);
            return new Vector3D(
                sinPhi * Math.Cos(theta),
                Math.Cos(phi),
                sinPhi * Math.Sin(theta)
            );
        }

        /// <summary>
        /// Compute elevation changes at plate boundaries based on drift history.
        /// For each plate pair: generate 1–5 random drift events (convergent/divergent/transform),
        /// then apply the cumulative effect to boundary pixels.
        /// </summary>
        private static float[] ComputeBoundaryEffects(
            int w, int h, int[] plateIds, PlateInfo[] plates, Random rng, SeededNoise noise,
            float[] continental)
        {
            float[] effect = new float[w * h];
            int numPlates = plates.Length;

            // Pre-compute convergence value for each plate pair
            float[,] pairConv = new float[numPlates, numPlates];
            for (int a = 0; a < numPlates; a++)
            {
                for (int b = a + 1; b < numPlates; b++)
                {
                    Vector3D dir = (plates[b].center - plates[a].center).Normalized;
                    Vector3D relVel = plates[a].velocity * plates[a].driftSpeed
                                    - plates[b].velocity * plates[b].driftSpeed;
                    float conv = (float)relVel.Dot(dir) * 3000f;

                    // Add random drift history: 1–5 events per pair
                    int events = rng.Next(1, 6);
                    for (int e = 0; e < events; e++)
                    {
                        double r = rng.NextDouble();
                        if (r < 0.7)
                            conv += (float)(rng.NextDouble() * 3000.0 + 1000.0);
                        else if (r < 0.9)
                            conv -= (float)(rng.NextDouble() * 3000.0 + 1000.0);
                        // else: transform — minimal change
                    }

                    pairConv[a, b] = conv;
                    pairConv[b, a] = conv;
                }
            }

            // Apply effects at boundary pixels
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    int plateA = plateIds[idx];

                    // Check 8 neighbours for a different plate
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = (x + DX8[d] + w) % w;
                        int ny = y + DY8[d];
                        if (ny < 0 || ny >= h)
                            continue;

                        int plateB = plateIds[ny * w + nx];
                        if (plateB == plateA)
                            continue;

                        // Found a boundary with plateB
                        float conv = pairConv[plateA, plateB];
                        float localEffect;

                        if (conv > 0) // Convergent boundary — uplift
                        {
                            localEffect = conv;
                        }
                        else // Divergent boundary — subsidence
                        {
                            localEffect = conv * 0.5f;
                        }

                        // Vary effect along boundary using noise for natural look
                        Vector3D pos = PixelToSphere(x, y, w, h);
                        double variation = 0.7 + 0.6 * noise.Noise3D(
                            pos.x * 8.0, pos.y * 8.0, pos.z * 8.0);
                        localEffect *= (float)variation;

                        // Ridged noise enhances mountain chains at convergent boundaries
                        if (conv > 0)
                        {
                            double ridge = noise.Ridged3D(
                                pos.x * 6.0, pos.y * 6.0, pos.z * 6.0,
                                4, 0.5, 2.0);
                            localEffect += (float)(ridge * 1600.0);

                            // 地壳均衡：陆壳（厚而轻）碰撞时隆起更强烈，洋壳俯冲则以海沟为主
                            double cont = continental != null ? continental[idx] : 1.0;
                            localEffect *= (float)(0.5 + 0.9 * cont);
                        }

                        // Accumulate (take the strongest effect)
                        if (Math.Abs(localEffect) > Math.Abs(effect[idx]))
                            effect[idx] = localEffect;

                        break; // Only need one different-plate neighbour
                    }
                }
            }

            return effect;
        }

        /// <summary>
        /// Separable Gaussian blur with x-direction wraparound.
        /// Spreads boundary effects smoothly over ~3*sigma pixels.
        /// </summary>
        private static float[] GaussianBlur(int w, int h, float[] src, double sigma)
        {
            int radius = (int)Math.Ceiling(sigma * 3.0);
            if (radius < 1) radius = 1;

            // Build 1D Gaussian kernel
            double[] kernel = new double[radius * 2 + 1];
            double sum = 0.0;
            for (int i = -radius; i <= radius; i++)
            {
                double val = Math.Exp(-(double)(i * i) / (2.0 * sigma * sigma));
                kernel[i + radius] = val;
                sum += val;
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;

            float[] temp = new float[w * h];
            float[] output = new float[w * h];

            // Horizontal pass (x wraps)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double val = 0.0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int nx = (x + k + w) % w;
                        val += src[y * w + nx] * kernel[k + radius];
                    }
                    temp[y * w + x] = (float)val;
                }
            }

            // Vertical pass (y clamps)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double val = 0.0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int ny = y + k;
                        if (ny < 0) ny = 0;
                        if (ny >= h) ny = h - 1;
                        val += temp[ny * w + x] * kernel[k + radius];
                    }
                    output[y * w + x] = (float)val;
                }
            }

            return output;
        }

        /// <summary>
        /// Ensure top row (north pole) and bottom row (south pole) are uniform.
        /// All pixels in these rows represent the same point on the sphere.
        /// </summary>
        private static void EnsurePoleUniformity(int w, int h, float[] heightField)
        {
            // North pole (y = 0): average then set all
            float northAvg = 0f;
            for (int x = 0; x < w; x++)
                northAvg += heightField[x];
            northAvg /= w;
            for (int x = 0; x < w; x++)
                heightField[x] = northAvg;

            // South pole (y = h-1)
            float southAvg = 0f;
            int lastRow = (h - 1) * w;
            for (int x = 0; x < w; x++)
                southAvg += heightField[lastRow + x];
            southAvg /= w;
            for (int x = 0; x < w; x++)
                heightField[lastRow + x] = southAvg;
        }
    }
}
