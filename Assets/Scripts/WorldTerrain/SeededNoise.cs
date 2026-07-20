using System;

namespace WorldTerrain
{
    /// <summary>
    /// Seedable, thread-safe 3D value noise with fractal Brownian motion.
    /// Uses System.Math only — safe for background threads.
    /// </summary>
    public class SeededNoise
    {
        private readonly int seed;

        public SeededNoise(int seed)
        {
            this.seed = seed != 0 ? seed : 1;
        }

        // ── Integer hash → [0, 1) ──
        private double Hash3D(int x, int y, int z)
        {
            long h = seed;
            h = (h * 374761393L + x * 668265263L) & 0x7FFFFFFFL;
            h = (h * 374761393L + y * 668265263L) & 0x7FFFFFFFL;
            h = (h * 374761393L + z * 668265263L) & 0x7FFFFFFFL;
            h = ((h ^ (h >> 13)) * 1274126177L) & 0x7FFFFFFFL;
            return (h & 0xFFFFL) / 65536.0;
        }

        private static double Smooth(double t)
        {
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// 3D value noise with trilinear interpolation and smoothstep.
        /// Returns a value in [0, 1].
        /// </summary>
        public double Noise3D(double x, double y, double z)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            int iz = (int)Math.Floor(z);

            double fx = x - ix;
            double fy = y - iy;
            double fz = z - iz;

            double sx = Smooth(fx);
            double sy = Smooth(fy);
            double sz = Smooth(fz);

            // 8 corner hash values
            double v000 = Hash3D(ix, iy, iz);
            double v100 = Hash3D(ix + 1, iy, iz);
            double v010 = Hash3D(ix, iy + 1, iz);
            double v110 = Hash3D(ix + 1, iy + 1, iz);
            double v001 = Hash3D(ix, iy, iz + 1);
            double v101 = Hash3D(ix + 1, iy, iz + 1);
            double v011 = Hash3D(ix, iy + 1, iz + 1);
            double v111 = Hash3D(ix + 1, iy + 1, iz + 1);

            // Trilinear interpolation
            double x00 = Lerp(v000, v100, sx);
            double x10 = Lerp(v010, v110, sx);
            double x01 = Lerp(v001, v101, sx);
            double x11 = Lerp(v011, v111, sx);

            double y0 = Lerp(x00, x10, sy);
            double y1 = Lerp(x01, x11, sy);

            return Lerp(y0, y1, sz);
        }

        /// <summary>
        /// Fractal Brownian Motion (multi-octave noise).
        /// Returns a value approximately in [0, 1].
        /// </summary>
        public double Fbm3D(
            double x, double y, double z,
            int octaves = 6, double persistence = 0.5, double lacunarity = 2.0)
        {
            double total = 0.0;
            double amplitude = 1.0;
            double frequency = 1.0;
            double maxValue = 0.0;

            for (int i = 0; i < octaves; i++)
            {
                total += Noise3D(x * frequency, y * frequency, z * frequency) * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            if (maxValue <= 0.0)
                return 0.0;

            return total / maxValue;
        }

        /// <summary>
        /// Ridged multifractal — produces sharp ridges suitable for mountain ranges.
        /// Returns a value in [0, 1].
        /// </summary>
        public double Ridged3D(
            double x, double y, double z,
            int octaves = 6, double persistence = 0.5, double lacunarity = 2.0)
        {
            double total = 0.0;
            double amplitude = 1.0;
            double frequency = 1.0;
            double maxValue = 0.0;

            for (int i = 0; i < octaves; i++)
            {
                double n = Noise3D(x * frequency, y * frequency, z * frequency);
                n = 1.0 - Math.Abs(n * 2.0 - 1.0);     // ridge: peaks at n=0.5
                n = n * n;                              // sharpen
                total += n * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            if (maxValue <= 0.0)
                return 0.0;

            return total / maxValue;
        }
    }
}
