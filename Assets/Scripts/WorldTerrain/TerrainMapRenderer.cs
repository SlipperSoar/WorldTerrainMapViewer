using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// Renders the height field and terrain types into color arrays.
    /// Step 7: Continuous hypsometric tinting for non-ice terrain.
    /// Step 8: Grayscale heightmap.
    /// </summary>
    public static class TerrainMapRenderer
    {
        /// <summary>
        /// Step 8: Render a grayscale heightmap.
        /// </summary>
        public static Color[] RenderHeightMap(int width, int height, float[] heightField)
        {
            Color[] colors = new Color[width * height];
            float minH = -10000f;
            float maxH = 10000f;
            float range = maxH - minH;

            for (int i = 0; i < width * height; i++)
            {
                float t = (heightField[i] - minH) / range;
                t = Math.Max(0f, Math.Min(1f, t));
                colors[i] = new Color(t, t, t, 1f);
            }

            return colors;
        }

        /// <summary>
        /// Step 7: Render the color terrain map using continuous elevation mapping.
        /// Ocean depth uses noise-varied gradients; land uses meter-based thresholds.
        /// </summary>
        public static Color[] RenderTerrainMap(
            int width, int height, float[] heightField,
            TerrainType[] terrainTypes, HydrologyData hydrology,
            SeededNoise noise)
        {
            Color[] colors = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    colors[i] = GetTerrainColor(
                        heightField[i], terrainTypes[i],
                        hydrology.riverWidth[i],
                        hydrology.estuaryMask[i],
                        x, y, width, height, noise);
                }
            }

            return colors;
        }

        /// <summary>
        /// Render the tectonic plate division map. Each plate gets a distinct
        /// color; plate boundaries (earthquake belts) are highlighted in
        /// light yellow. When transparent=true, plate interiors have low
        /// alpha so the underlying terrain is visible through the overlay.
        /// </summary>
        public static Color[] RenderPlateMap(
            int width, int height, int[] plateIds, bool transparent)
        {
            Color[] colors = new Color[width * height];

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
                        colors[idx] = boundaryColor;
                    }
                    else
                    {
                        Color c = palette[plate];
                        colors[idx] = new Color(c.r, c.g, c.b, interiorAlpha);
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

        // ── Land color stops (elevation in meters) ──
        // Orange/brown appears at 1500m so mountains are clearly visible

        private struct LandStop
        {
            public double elev;
            public double r, g, b;
        }

        private static readonly LandStop[] LAND_STOPS =
        {
            new LandStop { elev =    0, r = 0.40, g = 0.65, b = 0.35 },  // Shore green
            new LandStop { elev =  300, r = 0.52, g = 0.70, b = 0.32 },  // Light green
            new LandStop { elev =  800, r = 0.65, g = 0.65, b = 0.30 },  // Yellow-green
            new LandStop { elev = 1500, r = 0.78, g = 0.58, b = 0.25 },  // Orange-tan
            new LandStop { elev = 2500, r = 0.72, g = 0.48, b = 0.20 },  // Brown
            new LandStop { elev = 4000, r = 0.58, g = 0.35, b = 0.15 },  // Dark brown
            new LandStop { elev = 5000, r = 0.80, g = 0.80, b = 0.82 },  // Snow
            new LandStop { elev =10000, r = 0.95, g = 0.95, b = 0.96 },  // Pure snow
        };

        /// <summary>
        /// Continuous land elevation-to-color mapping using meter-based thresholds.
        /// Orange/brown appears at 1500m so mountains are clearly visible.
        /// </summary>
        private static Color LandElevationToColor(float elevation)
        {
            double elev = (double)elevation;

            for (int i = 0; i < LAND_STOPS.Length - 1; i++)
            {
                if (elev <= LAND_STOPS[i + 1].elev)
                {
                    var a = LAND_STOPS[i];
                    var b = LAND_STOPS[i + 1];
                    double t = (elev - a.elev) / (b.elev - a.elev);
                    t = SmoothStep(t);

                    return new Color(
                        (float)(a.r + (b.r - a.r) * t),
                        (float)(a.g + (b.g - a.g) * t),
                        (float)(a.b + (b.b - a.b) * t),
                        1f);
                }
            }

            var last = LAND_STOPS[LAND_STOPS.Length - 1];
            return new Color((float)last.r, (float)last.g, (float)last.b, 1f);
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
            bool isEstuary,
            int x, int y, int w, int h, SeededNoise noise)
        {
            // Glacier: continuous gray-white based on elevation
            if (type == TerrainType.Glacier)
            {
                double t = Math.Max(0.0, Math.Min(1.0, elevation / 4000.0));
                double v = 0.75 + t * 0.22;
                return new Color((float)v, (float)(v + 0.01), (float)(v + 0.02), 1f);
            }

            // Estuary (river mouth): use ocean depth color, no river blue overlay
            if (type == TerrainType.River && isEstuary)
                return OceanDepthToColor(elevation, x, y, w, h, noise);

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

            // Lake: blue
            if (type == TerrainType.Lake)
                return new Color(0.24f, 0.55f, 0.71f, 1f);

            // Ocean: noise-varied depth gradient
            if (elevation <= 0f)
                return OceanDepthToColor(elevation, x, y, w, h, noise);

            // Land: meter-based continuous mapping
            return LandElevationToColor(elevation);
        }
    }
}
