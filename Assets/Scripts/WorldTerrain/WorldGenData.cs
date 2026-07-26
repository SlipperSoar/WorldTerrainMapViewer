using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// Lightweight 3D vector using doubles, thread-safe (no UnityEngine dependency).
    /// </summary>
    public struct Vector3D
    {
        public double x, y, z;

        public Vector3D(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public double Dot(Vector3D o)
        {
            return x * o.x + y * o.y + z * o.z;
        }

        public double LengthSquared => Dot(this);

        public double Length => Math.Sqrt(LengthSquared);

        public Vector3D Normalized
        {
            get
            {
                double len = Length;
                if (len < 1e-12)
                    return new Vector3D(0, 0, 0);
                return new Vector3D(x / len, y / len, z / len);
            }
        }

        public static Vector3D operator -(Vector3D a, Vector3D b)
            => new Vector3D(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3D operator +(Vector3D a, Vector3D b)
            => new Vector3D(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3D operator *(Vector3D a, double s)
            => new Vector3D(a.x * s, a.y * s, a.z * s);

        public override string ToString()
            => $"({x:F4}, {y:F4}, {z:F4})";
    }

    /// <summary>
    /// Tectonic plate metadata. Plates are purely spatial divisions — they do
    /// not determine land/sea; that emerges from the continuous height field.
    /// </summary>
    public struct PlateInfo
    {
        public Vector3D center;
        public Vector3D velocity;
        public float driftSpeed;
    }

    /// <summary>
    /// Generation configuration.
    /// </summary>
    public struct WorldGenConfig
    {
        public int seed;
        public int width;
        public int height;
        public int minPlates;
        public int maxPlates;
        public float maxElevation;
        public float minElevation;
        public float waterCoverage;  // 0-1: fraction of surface that should be ocean
    }

    /// <summary>
    /// Terrain classification types for the final map.
    /// </summary>
    public enum TerrainType
    {
        DeepOcean,
        Ocean,
        ShallowOcean,
        Coast,
        Grassland,
        Forest,
        Hills,
        Highland,
        Plateau,
        Mountain,
        HighMountain,
        SnowPeak,
        Desert,
        Gobi,
        Tundra,
        Rainforest,
        Glacier,
        Lake,
        River
    }

    /// <summary>
    /// River and lake data produced by the hydrology pass.
    /// </summary>
    public struct HydrologyData
    {
        public bool[] riverMask;
        public bool[] lakeMask;
        public float[] riverWidth;
        public bool[] estuaryMask;
    }

    /// <summary>
    /// Complete result of a world generation run.
    /// </summary>
    public class WorldGenResult
    {
        public int width;
        public int height;
        public float[] heightField;
        public int[] plateIds;
        public TerrainType[] terrainTypes;
        public Color[] heightColors;
        public Color[] terrainColors;
        public Color[] plateOverlayColors;
    }
}
