using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>自然地名的种类</summary>
    public enum NamedFeatureKind
    {
        River = 0,
        Lake = 1,
        Mountain = 2,
        Bay = 3,
        Island = 4
    }

    /// <summary>交通线类型</summary>
    public enum RouteKind
    {
        Land = 0,
        River = 1,
        Sea = 2
    }

    /// <summary>一个聚落（村/镇/城/都）</summary>
    [Serializable]
    public class SettlementData
    {
        public string name = "";
        public int level = 1;               // 1 村 2 镇 3 城 4 都
        public int population;
        public int rank;                    // 人口排名，1 = 最大
        public int nationIndex = -1;
        public string style = "";           // 命名风格包
        public bool isCapital;
        public bool isCoastal;
        public float u, v;                  // 等距圆柱 UV，用于在地球上定位
        public int cellX, cellY;
        public List<string> features = new List<string>();
    }

    /// <summary>一个国家 / 势力</summary>
    [Serializable]
    public class NationData
    {
        public string name = "";
        public string polity = "";          // 政体（王国/帝国/…）
        public int capitalIndex = -1;       // settlements 下标
        public int settlementCount;
        public long totalPopulation;
        public float r, g, b;               // 显示颜色
    }

    /// <summary>一条交通线（陆路 / 河运 / 海运）</summary>
    [Serializable]
    public class RouteData
    {
        public int kind;                    // RouteKind
        public int fromIndex, toIndex;      // 聚落下标
        public long traffic;                // 联系强度（引力模型）
        public List<Vector2> points = new List<Vector2>();   // UV 折线
    }

    /// <summary>一个被命名的自然要素（河流/湖泊/山脉/海湾/岛屿）</summary>
    [Serializable]
    public class NamedFeatureData
    {
        public string name = "";
        public int kind;                    // NamedFeatureKind
        public float u, v;
        public float importance;            // 相对规模 0-1
    }

    /// <summary>
    /// 人文图层：聚落、国家、交通线与自然地名。
    /// 与地图一起保存为 "&lt;地图名&gt;_human.json"，载入地图时叠加显示。
    /// </summary>
    [Serializable]
    public class HumanGeographyData
    {
        public int version = 1;
        public int seed;
        public long totalPopulation;
        public float mapAspect = 2f;        // 宽/高，便于在球面上定位
        public List<SettlementData> settlements = new List<SettlementData>();
        public List<NationData> nations = new List<NationData>();
        public List<RouteData> routes = new List<RouteData>();
        public List<NamedFeatureData> features = new List<NamedFeatureData>();

        /// <summary>势力范围栅格（粗网格），每格 1 字节：0 = 海洋/无主，n = 第 n 个国家（从 1 开始）</summary>
        public int territoryWidth;
        public int territoryHeight;
        public string territoryBase64 = "";

        public byte[] GetTerritory()
        {
            if (string.IsNullOrEmpty(territoryBase64))
                return null;
            try
            {
                return Convert.FromBase64String(territoryBase64);
            }
            catch
            {
                return null;
            }
        }

        public void SetTerritory(byte[] territory, int tw, int th)
        {
            territoryWidth = tw;
            territoryHeight = th;
            territoryBase64 = territory == null ? "" : Convert.ToBase64String(territory);
        }

        public bool HasContent => settlements != null && settlements.Count > 0;

        public int CountOfLevel(int level)
        {
            int c = 0;
            if (settlements == null) return 0;
            for (int i = 0; i < settlements.Count; i++)
                if (settlements[i].level == level)
                    c++;
            return c;
        }

        // ── 持久化 ──

        public static string GetPath(string mapBaseName)
        {
            return Path.Combine(Application.streamingAssetsPath, mapBaseName + "_human.json");
        }

        public string ToJson()
        {
            return JsonUtility.ToJson(this, true);
        }

        public static void Save(string mapBaseName, HumanGeographyData data)
        {
            if (string.IsNullOrEmpty(mapBaseName) || data == null)
                return;

            string path = GetPath(mapBaseName);
            try
            {
                File.WriteAllText(path, data.ToJson());
                Debug.Log($"Saved human geography: {path} " +
                          $"({data.settlements.Count} 聚落 / {data.nations.Count} 国 / {data.routes.Count} 条交通线)");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save human geography '{path}': {e.Message}");
            }
        }

        public static HumanGeographyData Load(string mapBaseName)
        {
            if (string.IsNullOrEmpty(mapBaseName))
                return null;

            string path = GetPath(mapBaseName);
            if (!File.Exists(path))
                return null;

            try
            {
                var data = JsonUtility.FromJson<HumanGeographyData>(File.ReadAllText(path));
                if (data == null || data.settlements == null)
                    return null;

                if (data.nations == null) data.nations = new List<NationData>();
                if (data.routes == null) data.routes = new List<RouteData>();
                if (data.features == null) data.features = new List<NamedFeatureData>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to read human geography '{path}': {e.Message}");
                return null;
            }
        }
    }

    /// <summary>写进地图属性（&lt;地图名&gt;_meta.json）的人文信息摘要</summary>
    [Serializable]
    public class HumanMapSummary
    {
        public bool generated;
        public int settlements;
        public int villages;
        public int towns;
        public int cities;
        public int capitals;
        public int nations;
        public int routes;
        public int features;
        public long totalPopulation;

        public static HumanMapSummary From(HumanGeographyData data)
        {
            if (data == null || !data.HasContent)
                return new HumanMapSummary();

            return new HumanMapSummary
            {
                generated = true,
                settlements = data.settlements.Count,
                villages = data.CountOfLevel(1),
                towns = data.CountOfLevel(2),
                cities = data.CountOfLevel(3),
                capitals = data.CountOfLevel(4),
                nations = data.nations.Count,
                routes = data.routes.Count,
                features = data.features.Count,
                totalPopulation = data.totalPopulation
            };
        }
    }
}
