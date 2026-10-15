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
        public float axialTilt;      // 地轴倾角（黄赤交角），0-90 度；地图属性

        /// <summary>
        /// false（默认）：在单位球面的等面积网格上生成地形，再投影成等距圆柱高度图（推荐）；
        /// true：沿用旧的「直接在矩形网格上生成」路径，便于对比与回退。
        /// </summary>
        public bool planarGeneration;
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
        River,
        SeaIce,
        /// <summary>季节性（间歇性）河流：集水区气候干旱，仅雨季有水</summary>
        SeasonalRiver,
        /// <summary>河口三角洲的分流河道与三角洲平原</summary>
        Delta
    }

    /// <summary>
    /// 水文生成结果：河道、湖泊、河宽与各类水体掩膜。
    /// </summary>
    public struct HydrologyData
    {
        /// <summary>河道掩膜（含加粗后的干流）</summary>
        public bool[] riverMask;

        /// <summary>湖泊掩膜（被填平的洼地）</summary>
        public bool[] lakeMask;

        /// <summary>河宽（像素），合流变宽、分流变窄</summary>
        public float[] riverWidth;

        /// <summary>入海口（河流注入海洋的那一格）</summary>
        public bool[] estuaryMask;

        /// <summary>季节性河流掩膜（集水区平均湿度低于气候干旱阈值）</summary>
        public bool[] seasonalMask;

        /// <summary>三角洲分流河道掩膜（浅海中的扇形河道）</summary>
        public bool[] deltaMask;

        /// <summary>有效流量（赤道等效格数），用于宽度与调试</summary>
        public float[] discharge;

        /// <summary>湖泊水深（米），非湖泊为 0；由填洼后的水面高程减原高程得到</summary>
        public float[] waterDepth;

        /// <summary>河道的集水区平均湿度（0-1），用于判定季节性河流</summary>
        public float[] catchmentMoisture;
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

        /// <summary>本次生成使用的地图属性：地轴倾角（度）。</summary>
        public float axialTilt;

        /// <summary>本次生成使用的地图属性：水面占比（0-1）。</summary>
        public float waterCoverage;

        /// <summary>人文图层：聚落、国家、交通线与自然地名。</summary>
        public HumanGeographyData human;
    }
}
