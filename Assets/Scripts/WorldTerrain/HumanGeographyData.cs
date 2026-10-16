using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        public string id = "";              // 稳定编号：导出命名请求 / 导入地名时按它匹配
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

        // ── 交给 AI 参考的地理/气候背景（导出命名请求时一并给出）──
        public float lat, lon;              // 纬度 / 经度（度，东经为正）
        public float elevation;             // 海拔（米）
        public float moisture;              // 湿度 0-1
        public float summerTemp;            // 最热月平均气温（℃）
        public bool nearRiver, nearLake, onCoast;
        public bool mountainous, snowy, desert, forest, steppe, tundra;
        public bool riverConfluence, delta, riverMouth, mountainPass;
        public bool hasIron, hasCopper, hasGold, hasSalt;
    }

    /// <summary>一个国家 / 势力</summary>
    [Serializable]
    public class NationData
    {
        public string id = "";
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
        public string id = "";
        public string name = "";
        public int kind;                    // NamedFeatureKind
        public float u, v;
        public float lat, lon;              // 纬度 / 经度（度）
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

        // ── 云层用的湿度场 ──
        //
        // 云层预览需要「气候场」数据来决定哪里多云。生成时会算出湿度场
        // （ClimateModel.ComputeMoistureField），但它没有单独落盘 —— 这里把降采样后的
        // 湿度场（每格 1 字节）一并存进本文件，复用已有的 base64 机制，不新增文件。

        /// <summary>云层湿度场的宽高与数据（等距圆柱投影，每格 1 字节 = 0-255 的湿度）。</summary>
        public int moistureWidth;
        public int moistureHeight;
        public string moistureBase64 = "";

        /// <summary>取湿度场字节（无数据时返回 null）。</summary>
        public byte[] GetMoistureField()
        {
            if (string.IsNullOrEmpty(moistureBase64))
                return null;
            try
            {
                return Convert.FromBase64String(moistureBase64);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存降采样后的湿度场（供云层预览使用）。</summary>
        public void SetMoistureField(byte[] field, int width, int height)
        {
            if (field == null || width <= 0 || height <= 0 || field.Length < width * height)
            {
                moistureWidth = 0;
                moistureHeight = 0;
                moistureBase64 = "";
                return;
            }

            moistureWidth = width;
            moistureHeight = height;
            moistureBase64 = Convert.ToBase64String(field, 0, width * height);
        }

        /// <summary>
        /// 把任意分辨率的湿度场降采样成云层用的低分辨率字节数组（每格 1 字节 = 0-255）。
        /// 云层是低频效果，缩到 256×128 足够，且能把 JSON 体积控制在几十 KB。
        /// 实际用的宽高通过 <paramref name="outWidth"/> / <paramref name="outHeight"/> 返回，
        /// 避免调用方自己再算一遍导致不一致。
        /// </summary>
        public static byte[] DownsampleMoisture(float[] moisture, int w, int h, int targetW,
            out int outWidth, out int outHeight)
        {
            outWidth = 0;
            outHeight = 0;

            if (moisture == null || w <= 0 || h <= 0 || moisture.Length < w * h)
                return null;

            int tw = Math.Max(8, Math.Min(targetW, w));
            int th = Math.Max(4, tw * h / w);
            outWidth = tw;
            outHeight = th;
            var result = new byte[tw * th];

            for (int ty = 0; ty < th; ty++)
            {
                int y0 = ty * h / th;
                int y1 = Math.Max(y0 + 1, (ty + 1) * h / th);
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = tx * w / tw;
                    int x1 = Math.Max(x0 + 1, (tx + 1) * w / tw);

                    float sum = 0f;
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            sum += moisture[row + x];
                            n++;
                        }
                    }

                    float v = n > 0 ? sum / n : 0f;
                    int b = (int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f);
                    result[ty * tw + tx] = (byte)b;
                }
            }

            return result;
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

        /// <summary>命名请求文件的默认路径（导出后交给 AI 填名，再原样导入）。</summary>
        public static string GetNamingRequestPath(string mapBaseName)
        {
            return Path.Combine(Application.streamingAssetsPath, mapBaseName + "_names.json");
        }

        // ── 导出 / 导入地名（人工 + AI 协作命名）──

        /// <summary>
        /// 生成「命名请求」文本：把每个待命名对象的地理与人文背景连同空的 name 字段一起给出，
        /// 可以直接丢给 AI 让它批量起中文地名。返回写出的文件路径。
        /// </summary>
        public static string ExportNamingRequest(string mapBaseName, HumanGeographyData data,
            string path = null)
        {
            if (data == null)
                return null;

            if (string.IsNullOrEmpty(path))
                path = GetNamingRequestPath(mapBaseName);

            EnsureIds(data);        // 老地图可能没有编号，导出前补上

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"instructions\": \"请为下列每个条目生成一个中文地名，填进它的 name 字段；" +
                          "name 留空或保持原样的条目在导入时会被跳过。命名要求：2-6 个汉字；" +
                          "必须与 features 及地理字段相符（干旱区不要用 港/津/湾，寒冷区不要用热带词，山区可用 关/岭/矿）；" +
                          "level 4=都、3=城、2=镇、1=村，可分别用 京/都/州/府、城/港/州/府、镇/堡/驿/关/渡、村/庄/屯/寨/集；" +
                          "同一个 nation 内部不要重名，风格参考 style 字段。改好后在工具里点「导入地名」。\",");
            sb.AppendLine($"  \"map\": \"{Escape(mapBaseName)}\",");
            sb.AppendLine($"  \"settlementCount\": {data.settlements.Count},");
            sb.AppendLine($"  \"featureCount\": {data.features.Count},");
            sb.AppendLine($"  \"nationCount\": {data.nations.Count},");
            sb.AppendLine("  \"settlements\": [");

            for (int i = 0; i < data.settlements.Count; i++)
            {
                var s = data.settlements[i];
                string nation = s.nationIndex >= 0 && s.nationIndex < data.nations.Count
                    ? data.nations[s.nationIndex].name : "";
                sb.Append("    {");
                sb.Append($"\"id\": \"{Escape(s.id)}\", \"name\": \"{Escape(s.name)}\", ");
                sb.Append($"\"level\": {s.level}, \"population\": {s.population}, ");
                sb.Append($"\"style\": \"{Escape(s.style)}\", \"nation\": \"{Escape(nation)}\", ");
                sb.Append($"\"lat\": {s.lat:F1}, \"lon\": {s.lon:F1}, \"elevation\": {s.elevation:F0}, ");
                sb.Append($"\"moisture\": {s.moisture:F2}, \"summerTemp\": {s.summerTemp:F1}, ");
                sb.Append($"\"features\": [{JoinStrings(s.features)}], ");
                sb.Append($"\"nearRiver\": {Bool(s.nearRiver)}, \"nearLake\": {Bool(s.nearLake)}, ");
                sb.Append($"\"onCoast\": {Bool(s.onCoast)}, \"riverConfluence\": {Bool(s.riverConfluence)}, ");
                sb.Append($"\"delta\": {Bool(s.delta)}, \"riverMouth\": {Bool(s.riverMouth)}, ");
                sb.Append($"\"mountainous\": {Bool(s.mountainous)}, \"mountainPass\": {Bool(s.mountainPass)}, ");
                sb.Append($"\"snowy\": {Bool(s.snowy)}, \"desert\": {Bool(s.desert)}, ");
                sb.Append($"\"forest\": {Bool(s.forest)}, \"steppe\": {Bool(s.steppe)}, \"tundra\": {Bool(s.tundra)}, ");
                sb.Append($"\"hasIron\": {Bool(s.hasIron)}, \"hasCopper\": {Bool(s.hasCopper)}, ");
                sb.Append($"\"hasGold\": {Bool(s.hasGold)}, \"hasSalt\": {Bool(s.hasSalt)}, ");
                sb.Append($"\"isCapital\": {Bool(s.isCapital)}");
                sb.Append(i == data.settlements.Count - 1 ? "}" : "},");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            sb.AppendLine("  \"features\": [");
            for (int i = 0; i < data.features.Count; i++)
            {
                var f = data.features[i];
                sb.Append("    {");
                sb.Append($"\"id\": \"{Escape(f.id)}\", \"name\": \"{Escape(f.name)}\", ");
                sb.Append($"\"kind\": \"{FeatureKindName(f.kind)}\", ");
                sb.Append($"\"lat\": {f.lat:F1}, \"lon\": {f.lon:F1}, \"importance\": {f.importance:F2}");
                sb.Append(i == data.features.Count - 1 ? "}" : "},");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            sb.AppendLine("  \"nations\": [");
            for (int i = 0; i < data.nations.Count; i++)
            {
                var n = data.nations[i];
                string capital = n.capitalIndex >= 0 && n.capitalIndex < data.settlements.Count
                    ? data.settlements[n.capitalIndex].name : "";
                sb.Append("    {");
                sb.Append($"\"id\": \"{Escape(n.id)}\", \"name\": \"{Escape(n.name)}\", ");
                sb.Append($"\"polity\": \"{Escape(n.polity)}\", \"capital\": \"{Escape(capital)}\", ");
                sb.Append($"\"settlements\": {n.settlementCount}, \"population\": {n.totalPopulation}");
                sb.Append(i == data.nations.Count - 1 ? "}" : "},");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            try
            {
                File.WriteAllText(path, sb.ToString());
                Debug.Log($"已导出命名请求：{path}");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"导出命名请求失败：{e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 补齐缺失的编号：老地图（生成时还没有 id 字段）载入后 id 为空，
        /// 这里按顺序回填 s001/f001/n001…，保证导出的命名请求带得上 id、导入也能对上号。
        /// </summary>
        public static void EnsureIds(HumanGeographyData data)
        {
            if (data == null)
                return;

            if (data.settlements != null)
                for (int i = 0; i < data.settlements.Count; i++)
                    if (string.IsNullOrEmpty(data.settlements[i].id))
                        data.settlements[i].id = $"s{i + 1:D3}";

            if (data.features != null)
                for (int i = 0; i < data.features.Count; i++)
                    if (string.IsNullOrEmpty(data.features[i].id))
                        data.features[i].id = $"f{i + 1:D3}";

            if (data.nations != null)
                for (int i = 0; i < data.nations.Count; i++)
                    if (string.IsNullOrEmpty(data.nations[i].id))
                        data.nations[i].id = $"n{i + 1:D3}";
        }

        /// <summary>导入结果：便于界面告诉用户到底发生了什么。</summary>
        public struct NameImportResult
        {
            public bool fileExists;      // 文件是否存在
            public int pairs;            // 解析出的 id→name 条数
            public int applied;          // 实际改动的条目
            public int unchanged;        // 解析到但与当前同名（无需改动）
            public int unknown;          // 解析到但 id 在地图里找不到
            public string path;          // 实际读取的路径

            public string Describe()
            {
                if (!fileExists)
                    return $"没有找到 {System.IO.Path.GetFileName(path)}，请先导出、AI 改完后覆盖同名文件";

                if (pairs == 0)
                    return $"没能从 {System.IO.Path.GetFileName(path)} 解析出地名（需要 id 与 name 成对出现）";

                if (applied == 0)
                    return $"解析到 {pairs} 条，但都与当前地名相同，无需改动";

                return $"已应用 {applied} 个地名（解析 {pairs} 条，同名 {unchanged}，未匹配 {unknown}）";
            }
        }

        /// <summary>
        /// 从命名请求文件（或任意含 "id"/"name" 的文本）导入地名。
        /// 解析刻意做得宽松：即使 AI 把 JSON 包在 ``` 代码块里、加了说明文字、
        /// 改成「编号: 名字」的扁平映射，也能识别。
        /// </summary>
        public static NameImportResult ApplyNamesFromFile(string path, HumanGeographyData data)
        {
            var result = new NameImportResult { path = path };

            if (data == null || string.IsNullOrEmpty(path))
                return result;

            result.fileExists = File.Exists(path);
            if (!result.fileExists)
                return result;

            try
            {
                return ApplyNames(File.ReadAllText(path), data, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"导入地名失败：{e.Message}");
                result.fileExists = false;
                return result;
            }
        }

        /// <summary>从文本中解析 id → name 并套用到聚落/自然要素/国家上。</summary>
        public static NameImportResult ApplyNames(string text, HumanGeographyData data,
            string path = null)
        {
            var result = new NameImportResult { fileExists = true, path = path };
            if (string.IsNullOrEmpty(text) || data == null)
                return result;

            EnsureIds(data);

            // 首选：按区段（settlements / features / nations）分别解析。
            // 段内优先用 id 对应；id 为空（老文件）时退回「按出现顺序」对应；
            // 分区段做兜底，就不会把某个国家的名字串到聚落上。
            var sEntries = ExtractSectionEntries(text, "settlements");
            var fEntries = ExtractSectionEntries(text, "features");
            var nEntries = ExtractSectionEntries(text, "nations");
            if (sEntries.Count > 0 || fEntries.Count > 0 || nEntries.Count > 0)
            {
                int p2 = 0, a2 = 0, u2 = 0, un2 = 0;
                ApplyEntries(sEntries, data.settlements.Count,
                    i => data.settlements[i].id, i => data.settlements[i].name,
                    (i, n) => data.settlements[i].name = n,
                    ref p2, ref a2, ref u2, ref un2);
                ApplyEntries(fEntries, data.features.Count,
                    i => data.features[i].id, i => data.features[i].name,
                    (i, n) => data.features[i].name = n,
                    ref p2, ref a2, ref u2, ref un2);
                ApplyEntries(nEntries, data.nations.Count,
                    i => data.nations[i].id, i => data.nations[i].name,
                    (i, n) => data.nations[i].name = n,
                    ref p2, ref a2, ref u2, ref un2);

                result.pairs = p2;
                result.applied = a2;
                result.unchanged = u2;
                result.unknown = un2;
                return result;
            }

            var map = new Dictionary<string, string>();

            // 1) 常规格式：每个条目对象里有 "id" 与 "name"
            var pairs = ExtractIdNamePairs(text);
            for (int i = 0; i < pairs.Count; i++)
            {
                string id = pairs[i].Key;
                string name = pairs[i].Value;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                    map[id] = name.Trim();
            }

            // 2) 兜底格式：编号 → 名字 的扁平映射，例如 { "s001": "白河城", ... }
            if (map.Count == 0)
                ExtractFlatIdNameMap(text, data, map);

            result.pairs = map.Count;
            if (map.Count == 0)
                return result;

            int unchanged = 0, unknown = 0;
            var known = new HashSet<string>();

            for (int i = 0; i < data.settlements.Count; i++)
            {
                var s = data.settlements[i];
                known.Add(s.id);
                if (!map.TryGetValue(s.id, out string n))
                    continue;
                if (n == s.name) unchanged++;
                else { s.name = n; result.applied++; }
            }
            for (int i = 0; i < data.features.Count; i++)
            {
                var f = data.features[i];
                known.Add(f.id);
                if (!map.TryGetValue(f.id, out string n))
                    continue;
                if (n == f.name) unchanged++;
                else { f.name = n; result.applied++; }
            }
            for (int i = 0; i < data.nations.Count; i++)
            {
                var na = data.nations[i];
                known.Add(na.id);
                if (!map.TryGetValue(na.id, out string n))
                    continue;
                if (n == na.name) unchanged++;
                else { na.name = n; result.applied++; }
            }

            foreach (var kv in map)
                if (!known.Contains(kv.Key))
                    unknown++;

            result.unchanged = unchanged;
            result.unknown = unknown;
            return result;
        }

        /// <summary>
        /// 兜底解析：「id 风格的键 : 字符串值」的扁平映射，
        /// 例如 { "s001": "白河城", "n001": "白河王国" }（AI 有时会改成这种更紧凑的格式）。
        /// </summary>
        private static void ExtractFlatIdNameMap(string text, HumanGeographyData data,
            Dictionary<string, string> map)
        {
            var known = new HashSet<string>();
            for (int i = 0; i < data.settlements.Count; i++) known.Add(data.settlements[i].id);
            for (int i = 0; i < data.features.Count; i++) known.Add(data.features[i].id);
            for (int i = 0; i < data.nations.Count; i++) known.Add(data.nations[i].id);

            int i2 = 0;
            while (i2 < text.Length)
            {
                int q1 = text.IndexOf('"', i2);
                if (q1 < 0)
                    break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0)
                    break;

                string key = text.Substring(q1 + 1, q2 - q1 - 1);
                i2 = q2 + 1;

                if (!known.Contains(key))
                    continue;

                int colon = text.IndexOf(':', i2);
                if (colon < 0)
                    break;
                int v1 = text.IndexOf('"', colon + 1);
                if (v1 < 0)
                    break;

                var sb = new StringBuilder();
                int vEnd = text.Length;
                for (int k = v1 + 1; k < text.Length; k++)
                {
                    char c = text[k];
                    if (c == '\\' && k + 1 < text.Length) { sb.Append(text[++k]); continue; }
                    if (c == '"') { vEnd = k; break; }
                    sb.Append(c);
                }

                string value = sb.ToString().Trim();
                if (value.Length > 0)
                    map[key] = value;

                // 跳到值的右引号之后，否则下一次会把这个引号当成键的起始引号
                i2 = vEnd + 1;
            }
        }

        /// <summary>
        /// 宽松解析：逐个抓取大括号块（忽略代码围栏与说明文字），
        /// 在每个块里找第一个 "id" 与第一个 "name" 的字符串值。
        /// </summary>
        private static List<KeyValuePair<string, string>> ExtractIdNamePairs(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf('{', i);
                if (open < 0)
                    break;

                int depth = 0;
                int close = -1;
                bool inString = false;
                for (int k = open; k < text.Length; k++)
                {
                    char c = text[k];
                    if (inString)
                    {
                        if (c == '\\') { k++; continue; }
                        if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') { inString = true; continue; }
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { close = k; break; }
                    }
                }

                if (close < 0)
                    break;

                string block = text.Substring(open, close - open + 1);
                string id = ExtractStringValue(block, "id");
                string name = ExtractStringValue(block, "name");

                // 只接受「自己这一层」的 id/name：避免把嵌套对象的值当成父对象的
                if (!string.IsNullOrEmpty(id) && name != null)
                    result.Add(new KeyValuePair<string, string>(id, name));

                // 从 open+1 继续扫描，这样嵌套的对象也会被逐个取到
                // （否则只会拿到最外层那个块里的第一条）
                i = open + 1;
            }

            return result;
        }

        /// <summary>从一段文本里取 "key": "value" 的字符串值（找不到返回 null）。</summary>
        private static string ExtractStringValue(string block, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = block.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
                return null;

            int colon = block.IndexOf(':', idx + pattern.Length);
            if (colon < 0)
                return null;

            int q1 = block.IndexOf('"', colon + 1);
            if (q1 < 0)
                return null;

            var sb = new StringBuilder();
            for (int k = q1 + 1; k < block.Length; k++)
            {
                char c = block[k];
                if (c == '\\' && k + 1 < block.Length)
                {
                    sb.Append(block[++k]);
                    continue;
                }
                if (c == '"')
                    return sb.ToString();
                sb.Append(c);
            }
            return null;
        }

        /// <summary>按「id 优先、缺 id 时按顺序」把一批条目的名字套到某个列表上。</summary>
        private static void ApplyEntries(List<KeyValuePair<string, string>> entries, int count,
            Func<int, string> idGet, Func<int, string> nameGet, Action<int, string> nameSet,
            ref int pairs, ref int applied, ref int unchanged, ref int unknown)
        {
            if (count <= 0 || entries.Count == 0)
                return;

            var byId = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                string id = idGet(i);
                if (!string.IsNullOrEmpty(id))
                    byId[id] = i;
            }

            for (int k = 0; k < entries.Count; k++)
            {
                string name = entries[k].Value;
                if (string.IsNullOrEmpty(name))
                    continue;

                pairs++;
                string eid = entries[k].Key;
                int idx;
                if (!string.IsNullOrEmpty(eid))
                {
                    if (!byId.TryGetValue(eid, out idx)) { unknown++; continue; }
                }
                else
                {
                    idx = k;                       // 没有编号：按出现顺序对应
                }

                if (idx < 0 || idx >= count) { unknown++; continue; }
                if (nameGet(idx) == name) { unchanged++; continue; }
                nameSet(idx, name);
                applied++;
            }
        }

        /// <summary>取出某个区段（数组）里每个对象的 id 与 name，按出现顺序。</summary>
        private static List<KeyValuePair<string, string>> ExtractSectionEntries(string text, string key)
        {
            var list = new List<KeyValuePair<string, string>>();

            int k = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0)
                return list;

            int arr = text.IndexOf('[', k);
            if (arr < 0)
                return list;

            int end = MatchPair(text, arr, '[', ']');
            if (end < 0)
                return list;

            int p = arr;
            while (true)
            {
                int open = text.IndexOf('{', p);
                if (open < 0 || open > end)
                    break;

                int close = MatchPair(text, open, '{', '}');
                if (close < 0 || close > end)
                    break;

                string block = text.Substring(open, close - open + 1);
                list.Add(new KeyValuePair<string, string>(
                    ExtractStringValue(block, "id") ?? "",
                    ExtractStringValue(block, "name") ?? ""));

                p = close + 1;
            }

            return list;
        }

        /// <summary>从 open 处的括号开始找配对的右括号（忽略字符串里的括号）。</summary>
        private static int MatchPair(string text, int open, char openCh, char closeCh)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == openCh) depth++;
                else if (c == closeCh)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Bool(bool b) => b ? "true" : "false";

        private static string JoinStrings(List<string> items)
        {
            if (items == null || items.Count == 0)
                return "";
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Escape(items[i])).Append('"');
            }
            return sb.ToString();
        }

        private static string FeatureKindName(int kind)
        {
            switch (kind)
            {
                case 0: return "River";
                case 1: return "Lake";
                case 2: return "Mountain";
                case 3: return "Bay";
                default: return "Island";
            }
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

                if (data.settlements == null) data.settlements = new List<SettlementData>();
                if (data.nations == null) data.nations = new List<NationData>();
                if (data.routes == null) data.routes = new List<RouteData>();
                if (data.features == null) data.features = new List<NamedFeatureData>();
                EnsureIds(data);        // 老地图没有编号：载入时补齐
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
