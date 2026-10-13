using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>聚落等级</summary>
    public enum SettlementLevel { Village = 1, Town = 2, City = 3, Capital = 4 }

    /// <summary>命名所依据的地理/人文特征</summary>
    public struct NameContext
    {
        public double AbsLat;          // |纬度|，度
        public double Elevation;       // 海拔，米
        public double Moisture;        // 湿度 0-1
        public double SummerTemp;      // 最热月平均气温，摄氏度
        public double Population;      // 人口
        public int Level;              // SettlementLevel 的整数值：1 村 2 镇 3 城 4 都
        public bool IsCapital;
        public int NationIndex;
        public bool NearRiver, NearLake, OnCoast, OnIsland;
        public bool Mountainous, Snowy, Desert, Forest, Steppe, Tundra;
        public bool RiverConfluence, RiverMouth, Delta, MountainPass;
        public bool HasIron, HasCopper, HasGold, HasSalt;
        public int CellX, CellY, MapWidth, MapHeight;
    }

    /// <summary>自然要素种类</summary>
    public enum NaturalFeatureKind { River, Lake, Mountain, Bay, Island }

    /// <summary>
    /// 中文地名生成器（纯 C#，后台线程安全）。
    ///
    /// 设计要点：
    /// 1. 无静态可变状态，不使用 UnityEngine / DateTime / System.Random；
    ///    随机数全部由 (seedKey, 格网坐标, 候选序号, 尝试序号) 经整数哈希推导，
    ///    因此同一输入在任何线程、任何时刻都得到完全相同的名字。
    /// 2. 词库按「风格包」组织，每个风格包含 修饰 / 自然物 / 聚落后缀 三类词，
    ///    再加一份跨风格共享的方位词、规模词、数字词与山/河/湖/海通名。
    /// 3. 去重时同时比较「完整名字」「专名（去后缀）」「同音归一化形式」，
    ///    并避免相邻重字、后缀堆叠、通名堆叠与语义禁忌。
    /// </summary>
    public static class CultureNameGenerator
    {
        // ═══════════════════════════════════════════════════════════
        //  一、风格包（修饰 / 自然物 / 后缀）
        // ═══════════════════════════════════════════════════════════

        /// <summary>风格包编号（固定顺序，用于确定性取模）。</summary>
        private const int StyleNorth = 0;   // 北境寒地
        private const int StyleFarm = 1;    // 中原农耕
        private const int StyleIsle = 2;    // 海岛贸易
        private const int StyleDesert = 3;  // 沙漠游牧
        private const int StyleWater = 4;   // 南方水乡
        private const int StyleMine = 5;    // 山地矿镇
        private const int StyleCount = 6;

        private static readonly string[] StyleNames =
        {
            "北境寒地", "中原农耕", "海岛贸易", "沙漠游牧", "南方水乡", "山地矿镇"
        };

        private static readonly string[][] StyleModifiers =
        {
            new[] { "霜", "雪", "冰", "黑", "白", "铁", "寒", "北", "灰" },              // 北境寒地
            new[] { "金", "玉", "青", "安", "平", "兴", "富", "稻", "麦" },              // 中原农耕
            new[] { "蓝", "碧", "白", "珠", "贝", "帆", "盐", "海" },                    // 海岛贸易
            new[] { "沙", "月", "风", "金", "赤", "干", "荒" },                          // 沙漠游牧
            new[] { "青", "碧", "柳", "荷", "雨", "烟", "石", "白" },                    // 南方水乡
            new[] { "铁", "铜", "金", "银", "黑", "赤", "石", "岩" }                     // 山地矿镇
        };

        private static readonly string[][] StyleNatures =
        {
            new[] { "狼", "松", "石", "峰", "谷", "川", "原", "林" },                    // 北境寒地
            new[] { "河", "江", "湖", "山", "岭", "原", "林", "桥" },                    // 中原农耕
            new[] { "港", "湾", "岛", "礁", "潮", "沙", "鱼" },                          // 海岛贸易
            new[] { "泉", "洲", "丘", "漠", "石", "驼", "盐" },                          // 沙漠游牧
            new[] { "河", "湖", "桥", "溪", "塘", "浦", "湾" },                          // 南方水乡
            new[] { "峰", "岭", "谷", "关", "矿", "石", "崖" }                           // 山地矿镇
        };

        private static readonly string[][] StyleSuffixes =
        {
            new[] { "村", "寨", "堡", "关", "镇", "城" },                                // 北境寒地
            new[] { "村", "庄", "镇", "城", "州", "府" },                                // 中原农耕
            new[] { "港", "埠", "镇", "城" },                                            // 海岛贸易
            new[] { "驿", "堡", "寨", "镇", "城" },                                      // 沙漠游牧
            new[] { "村", "镇", "渡", "桥", "城" },                                      // 南方水乡
            new[] { "寨", "堡", "关", "镇", "城" }                                       // 山地矿镇
        };

        // ═══════════════════════════════════════════════════════════
        //  二、共享词表
        // ═══════════════════════════════════════════════════════════

        /// <summary>方位词，除「中」外都可用于冲突避让。</summary>
        private const string DirectionChars = "东西南北上下新旧中";

        /// <summary>规模词（少量用于大河、大城市）。</summary>
        private const string MagnitudeChars = "大小长高千万";

        /// <summary>冲突避让用的数字变体。</summary>
        private const string NumberChars = "一二三四五六七八九十";

        /// <summary>跨地域通名：山、河、湖、海等大类字。</summary>
        private const string GenericChars = "山河江湖峰岭岳川溪岛湾海屿石泉";

        /// <summary>国名政体用字（王国、帝国、联邦、公国、汗国、城邦、共和国）。</summary>
        private const string PolityChars = "王国帝联邦公汗共和";

        /// <summary>允许出现在名字里的全部字符（校验用，不含同音易混字）。</summary>
        private const string AllowedChars =
            "霜雪冰黑白铁寒北灰狼松石峰谷川原林" +
            "金玉青安平兴富稻麦河江湖山岭桥" +
            "蓝碧珠贝帆盐海港湾岛礁潮沙鱼" +
            "月风赤干荒泉洲丘漠驼" +
            "柳荷雨烟塘浦" +
            "铜银岩矿崖关" +
            "村寨堡镇城庄州府埠驿渡屯" +
            "市集商口塞渊泊潭池澳岳桥京都" +
            DirectionChars + MagnitudeChars + NumberChars + GenericChars + PolityChars;

        // ═══════════════════════════════════════════════════════════
        //  三、禁忌与同音表
        // ═══════════════════════════════════════════════════════════

        /// <summary>全局禁忌：语义上互相矛盾的组合（如「山」+「海」）。</summary>
        private static readonly string[] ForbiddenPairs =
        {
            "山河", "河山", "山江", "江山", "山峰", "峰山", "山岭", "岭山", "山崖", "崖山",
            "河湖", "湖河", "河海", "海河", "江河", "江江",
            "港港", "湾湾", "岛岛", "海海", "湖湖",
            "雪霜", "霜雪", "冰雪", "雪冰", "冰霜", "霜冰", "寒冰", "冰寒",
            "沙海", "海沙", "漠海", "海漠"
        };

        /// <summary>每个风格包的额外禁忌字（防止语义冲突：沙漠不出港、极地不出热带词）。</summary>
        private static readonly string[][] StyleForbiddenChars =
        {
            // 北境寒地：不出现港口水系后缀与热带/雨林式组合
            new[] { "港", "埠", "津", "湾", "潮", "帆", "荷", "柳", "雨", "热", "椰", "蕉" },
            // 中原农耕：不出现游牧/海岛/矿镇专属字
            new[] { "漠", "驼", "礁", "帆", "贝", "矿", "崖", "霜" },
            // 海岛贸易：不出现冰雪/沙漠/山地矿镇专属字
            new[] { "霜", "雪", "冰", "寒", "漠", "驼", "矿", "崖", "岭" },
            // 沙漠游牧：不出现港口、水系、冰雪、森林类字
            new[] { "港", "埠", "津", "湾", "潮", "帆", "湖", "溪", "浦", "塘", "霜", "雪", "冰", "狼", "松" },
            // 南方水乡：不出现冰雪/沙漠/矿产专属字
            new[] { "霜", "雪", "冰", "寒", "漠", "驼", "矿", "崖", "狼" },
            // 山地矿镇：不出现港口与海岛类字
            new[] { "港", "埠", "津", "湾", "潮", "帆", "贝", "漠", "驼", "鱼" }
        };

        /// <summary>同音易混字表（用「/」分组；比较前把整名归一化为组内首字）。</summary>
        private const string HomophoneGroupText =
            "白/百/柏|黑/赫|青/清/轻/卿|金/津/京/锦|沙/纱/砂|海/害|石/时/实/识|河/荷/合/禾|" +
            "山/杉/删|林/临/邻|平/萍/苹|玉/雨/宇/羽/瑜|利/力/立|州/洲/舟/周|漠/莫|泉/全/权|" +
            "赤/斥|珠/朱/株|水/税|冈/刚/钢|浦/普/朴|塘/唐/堂|崖/牙/芽|盐/言/严/研|稻/道/到|" +
            "麦/脉|干/甘/肝|荒/黄/皇/煌|贝/杯|帆/翻/凡/番|潮/朝/巢|驼/陀/坨|溪/西/稀/希|" +
            "穗/岁/遂/隧|铁/帖|铜/同/童/桐|银/因/音/阴|北/卑|寒/含/韩|冰/兵|原/元/园/源|" +
            "谷/古/故/顾|峰/风/丰/封|岭/领/令|狼/郎/朗/浪|松/颂/宋|安/岸/暗/鞍|兴/星/行/形|" +
            "富/付/复|蓝/兰/篮/拦|碧/壁/毕|柳/留/流/六|荷/何/贺/喝|雨/语/予/屿|烟/燕/言/沿|" +
            "桥/乔/侨/樵|村/存/寸|镇/阵/振|城/成/诚/承|府/俯/斧/抚|堡/宝/保/报|寨/在/再|" +
            "驿/义/意/易|渡/度/杜/都|庄/装/状/壮|屯/吞|集/极/急/吉|商/上/尚/伤";

        /// <summary>允许连续出现的相同汉字（叠字地名）。</summary>
        private const string RepeatAllowedChars = "";

        /// <summary>允许出现的「后缀」字（用于检测后缀堆叠：白河城港镇）。不含资源通名，资源由后缀组单独判定。</summary>
        private const string SuffixChars = "村寨堡镇城庄州府埠驿渡京都口塞市集商屯";

        /// <summary>资源类通名（矿、铁、盐、石…），与行政/交通后缀不得混用。</summary>
        private const string ResourceChars = "矿铁盐石";

        /// <summary>后缀堆叠时允许出现的后缀字数量上限。</summary>
        private const int MaxSuffixChars = 1;

        /// <summary>后缀组编号：仅取 1~5 位，与专名类别按位与判定兼容。</summary>
        private const int GroupWater = 1;
        private const int GroupMount = 2;
        private const int GroupAdmin = 4;
        private const int GroupTrade = 8;
        private const int GroupResource = 16;
        private const int GroupTransport = 32;
        private const int GroupOther = 64;

        /// <summary>不被允许的通名后缀（语义不合适）。</summary>
        private static readonly string[] SuffixBlacklist = { "郡", "邑", "邦", "疆", "首府" };

        /// <summary>过于笼统、不宜单独充当聚落专名的自然物（潮、漠、岛…）。</summary>
        private const string WeakCoreChars = "潮漠岛港湾礁海洋";

        /// <summary>可作为聚落通名的自然物。</summary>
        private const string GeoNounChars = "山河湖江溪湾港岛洲峰岭谷关丘矿泉崖桥塘浦";
        /// <summary>数量字（用于规模词位置判定）。</summary>
        private const string AmountChars = "千万";

        /// <summary>方位词与规模词（判定专名前缀用）。</summary>
        private const string PrefixChars = "东西南北上下新旧中大小长高千万一二三四五六七八九十";

        // ═══════════════════════════════════════════════════════════
        //  四、预计算静态只读表（构造后不再修改，线程安全）
        // ═══════════════════════════════════════════════════════════

        private static readonly HashSet<char> Allowed;
        private static readonly Dictionary<char, char> Homophone;
        private static readonly HashSet<string>[] StyleTokenSets;
        private static readonly HashSet<string>[] StyleCharSets;

        /// <summary>静态构造：由上面的字符串常量推导出哈希表（只读，无锁访问）。</summary>
        static CultureNameGenerator()
        {
            Allowed = new HashSet<char>();
            foreach (char c in AllowedChars)
            {
                Allowed.Add(c);
            }

            Homophone = new Dictionary<char, char>();
            foreach (string group in HomophoneGroupText.Split('|'))
            {
                if (group.Length == 0)
                {
                    continue;
                }
                char rep = group[0];
                foreach (char c in group)
                {
                    if (!Homophone.ContainsKey(c))
                    {
                        Homophone[c] = rep;
                    }
                }
            }

            StyleTokenSets = new HashSet<string>[StyleCount];
            StyleCharSets = new HashSet<string>[StyleCount];
            for (int s = 0; s < StyleCount; s++)
            {
                HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> chars = new HashSet<string>(StringComparer.Ordinal);
                AddTokens(tokens, chars, StyleModifiers[s]);
                AddTokens(tokens, chars, StyleNatures[s]);
                AddTokens(tokens, chars, StyleSuffixes[s]);

                // 兼容各风格都可以使用的通名、方位词、规模词与数字
                AddTokens(tokens, chars, new[] { DirectionChars, MagnitudeChars, NumberChars, GenericChars });

                StyleTokenSets[s] = tokens;
                StyleCharSets[s] = chars;
            }
        }

        private static void AddTokens(HashSet<string> tokens, HashSet<string> chars, string[] words)
        {
            foreach (string w in words)
            {
                if (w.Length != 0)
                {
                    tokens.Add(w);
                }
                foreach (char c in w)
                {
                    chars.Add(c.ToString());
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  五、确定性随机（整数哈希，无共享状态）
        // ═══════════════════════════════════════════════════════════

        /// <summary>按 (seedKey, a, b, c) 推导一个 32 位无符号随机状态。</summary>
        private static uint HashState(int seedKey, int a, int b, int c)
        {
            unchecked
            {
                uint h = (uint)seedKey * 2246822519u;
                h ^= (uint)a * 3266489917u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)b * 668265263u;
                h = (h << 7) | (h >> 25);
                h ^= (uint)c * 374761393u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return h == 0u ? 0x9E3779B9u : h;
            }
        }

        /// <summary>确定性 [0,1) 随机数（sin-hash，参考文档给法）。</summary>
        private static double Hash01(int v)
        {
            double t = Math.Sin(v * 12.9898) * 43758.5453;
            return t - Math.Floor(t);
        }

        /// <summary>推进内部状态并返回 [0,1)。</summary>
        private static double NextRand(ref uint state)
        {
            unchecked
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return (state & 0xFFFFFFu) / 16777216.0;
            }
        }

        /// <summary>取 [0, n) 的确定性整数。</summary>
        private static int RandInt(ref uint state, int n)
        {
            if (n <= 1)
            {
                return 0;
            }
            int v = (int)(NextRand(ref state) * n);
            if (v < 0)
            {
                v = 0;
            }
            if (v >= n)
            {
                v = n - 1;
            }
            return v;
        }

        /// <summary>由上下文与序号构造随机状态：同格同序号永远同状态。</summary>
        private static uint MakeState(NameContext ctx, int seedKey, int index)
        {
            int x = ctx.CellX;
            int y = ctx.CellY;
            int w = MapW(ctx);
            int h = MapH(ctx);
            int cell = unchecked(((y % h) + h) % h * 4096 + ((x % w) + w) % w);
            int level = LevelOf(ctx);
            int env = (ctx.OnCoast ? 1 : 0)
                      | (ctx.OnIsland ? 2 : 0)
                      | (ctx.NearRiver ? 4 : 0)
                      | (ctx.NearLake ? 8 : 0)
                      | (ctx.Desert ? 16 : 0)
                      | (ctx.Snowy ? 32 : 0)
                      | (ctx.Mountainous ? 64 : 0)
                      | (ctx.MountainPass ? 128 : 0)
                      | (ctx.RiverMouth ? 256 : 0)
                      | (ctx.Delta ? 512 : 0)
                      | (ctx.RiverConfluence ? 1024 : 0)
                      | (ctx.IsCapital ? 2048 : 0);
            return HashState(seedKey, cell, index, level * 100003 + env * 7 + ctx.NationIndex);
        }

        // ═══════════════════════════════════════════════════════════
        //  六、公开 API
        // ═══════════════════════════════════════════════════════════

        /// <summary>该位置属于哪个命名风格包（用于展示与调试），如 "北境寒地"</summary>
        public static string StyleOf(in NameContext ctx)
        {
            return StyleNames[PickStyle(ctx)];
        }

        /// <summary>生成聚落名（自动按等级/功能配后缀），保证与 used 不冲突，并把结果加入 used</summary>
        public static string NameSettlement(in NameContext ctx, ISet<string> used, int seedKey)
        {
            int style = PickStyle(ctx);
            List<string> cores = BuildCores(ctx, style, seedKey, 0);
            List<string> suffixes = BuildSuffixes(ctx, style);
            int level = LevelOf(ctx);

            for (int attempt = 0; attempt < 40; attempt++)
            {
                // 每 8 次尝试换一批专名候选（同一批内部也有多组词），避免专名扎堆
                if (attempt != 0 && attempt % 8 == 0)
                {
                    List<string> more = BuildCores(ctx, style, seedKey, attempt);
                    for (int i = 0; i < more.Count; i++)
                    {
                        if (!cores.Contains(more[i]))
                        {
                            cores.Add(more[i]);
                        }
                    }
                }
                if (cores.Count == 0)
                {
                    break;
                }

                // 方位避让：第 0 次不加方位词，之后逐步引入 上/下/新/东/西/南/北/大/小
                string dir = attempt == 0 ? string.Empty : DirectionFor(attempt, seedKey, ctx);
                bool allowNumber = attempt >= 16;

                string core = cores[(attempt >> 1) % cores.Count];
                string suf = suffixes[attempt % suffixes.Count];
                if (suf.Length == 0)
                {
                    continue;
                }

                string cand = dir + core + suf;
                if (IsAcceptable(cand, core, style, used))
                {
                    Commit(cand, used);
                    return cand;
                }

                if (allowNumber)
                {
                    for (int k = 0; k < NumberChars.Length; k++)
                    {
                        string nc = dir + NumberChars[k] + core + suf;
                        if (IsAcceptable(nc, core, style, used))
                        {
                            Commit(nc, used);
                            return nc;
                        }
                    }
                }
            }

            // 极端情况：词库穷尽，构造长度合规的确定性名字
            string fallback = HardFallback(ctx, seedKey, level, style, string.Empty);
            Commit(fallback, used);
            return fallback;
        }

        /// <summary>生成自然地名（河流/湖泊/山脉/海湾/岛屿），保证与 used 不冲突，并把结果加入 used</summary>
        public static string NameNatural(in NameContext ctx, NaturalFeatureKind kind, ISet<string> used, int seedKey)
        {
            int style = PickStyle(ctx);
            List<string> cores = BuildNaturalCores(ctx, style, kind, seedKey);
            List<string> kinds = BuildKindTerms(kind);

            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (attempt != 0 && attempt % 8 == 0)
                {
                    List<string> more = BuildNaturalCores(ctx, style, kind, seedKey + attempt);
                    for (int i = 0; i < more.Count; i++)
                    {
                        if (!cores.Contains(more[i]))
                        {
                            cores.Add(more[i]);
                        }
                    }
                }
                if (cores.Count == 0)
                {
                    break;
                }

                string dir = attempt == 0 ? string.Empty : DirectionFor(attempt, seedKey, ctx);
                string core = cores[(attempt >> 1) % cores.Count];
                string term = PickKindTerm(core, kinds, attempt);
                if (term.Length == 0)
                {
                    continue;
                }

                string cand = dir + core + term;
                if (IsAcceptable(cand, core, style, used))
                {
                    Commit(cand, used);
                    return cand;
                }
            }

            string fallback = HardFallback(ctx, seedKey, 0, style, GenericOf(kind));
            Commit(fallback, used);
            return fallback;
        }

        /// <summary>按尝试序号挑选与专名搭配不重复用字的通名。</summary>
        private static string PickKindTerm(string core, List<string> kinds, int attempt)
        {
            int start = kinds.Count > 0 ? attempt % kinds.Count : 0;
            for (int i = 0; i < kinds.Count; i++)
            {
                string t = kinds[(start + i) % kinds.Count];
                if (!KindTermFits(core, t) || KindTermAmountClash(core, t))
                {
                    continue;
                }
                return t;
            }
            for (int i = 0; i < kinds.Count; i++)
            {
                string t = kinds[(start + i) % kinds.Count];
                if (!KindTermAmountClash(core, t))
                {
                    return t;
                }
            }
            return kinds.Count > 0 ? kinds[0] : string.Empty;
        }

        /// <summary>生成国名：核心地名 + 政体，如 "白河王国"；保证与 used 不冲突，并把结果加入 used</summary>
        public static string NameNation(string coreName, in NameContext capitalCtx, ISet<string> used, int seedKey)
        {
            int style = PickStyle(capitalCtx);
            List<string> cores = BuildNationCores(coreName, capitalCtx, style, seedKey);
            List<string> polities = BuildPolityOrder(capitalCtx, style, seedKey);
            int fullCount = polities.Count / 2;

            for (int attempt = 0; attempt < 24; attempt++)
            {
                if (cores.Count == 0 || polities.Count == 0)
                {
                    break;
                }

                string core = cores[(attempt >> 1) % cores.Count];
                string polity = attempt % 2 == 0 && fullCount > 0
                    ? polities[(attempt >> 1) % fullCount]
                    : polities[fullCount + (attempt % Math.Max(1, polities.Count - fullCount))];

                string cand = core + polity;
                if (IsAcceptable(cand, core, style, used))
                {
                    Commit(cand, used);
                    return cand;
                }
            }

            // 政体全部冲突：加方位前缀（新中国、北白河王国之类）
            for (int i = 0; i < DirectionChars.Length; i++)
            {
                if (DirectionChars[i] == '中' || cores.Count == 0 || polities.Count == 0)
                {
                    continue;
                }
                string cand = DirectionChars[i] + cores[0] + polities[0];
                if (IsAcceptable(cand, cores[0], style, used))
                {
                    Commit(cand, used);
                    return cand;
                }
            }

            string fallback = HardFallback(capitalCtx, seedKey, 0, style, "国");
            Commit(fallback, used);
            return fallback;
        }

        /// <summary>国名核心：去掉聚落后缀，过短时补足，过长时截断。</summary>
        private static List<string> BuildNationCores(string coreName, NameContext ctx, int style, int seedKey)
        {
            List<string> cores = new List<string>(4);
            string name = coreName ?? string.Empty;
            string core = StripSuffix(name);
            if (core.Length < 2)
            {
                core = name.Length >= 2 ? name.Substring(0, 2) : core;
            }
            if (core.Length == 0)
            {
                core = "中";
            }
            if (core.Length > 3)
            {
                core = core.Substring(0, 3);
            }

            if (core.Length >= 2 && IsValidCore(core, style))
            {
                cores.Add(core);
            }
            else if (core.Length >= 2 && CharsAllowed(core))
            {
                cores.Add(core);   // 核心地名来自外部，放宽风格校验
            }

            List<string> built = BuildCores(ctx, style, seedKey, 7);
            for (int i = 0; i < built.Count && cores.Count < 4; i++)
            {
                if (!cores.Contains(built[i]))
                {
                    cores.Add(built[i]);
                }
            }
            if (cores.Count == 0)
            {
                cores.Add("中");
            }
            return cores;
        }

        // ═══════════════════════════════════════════════════════════
        //  七、风格选择
        // ═══════════════════════════════════════════════════════════

        /// <summary>按气候/地形优先级挑选风格包；任何输入都会得到一个合法风格。</summary>
        private static int PickStyle(NameContext ctx)
        {
            double lat = Safe(ctx.AbsLat, 0.0, 90.0);
            double temp = Safe(ctx.SummerTemp, -60.0, 70.0);
            double moist = Safe(ctx.Moisture, 0.0, 1.0);
            double elev = Safe(ctx.Elevation, 0.0, 12000.0);

            // 1) 沙漠：湿度低且气温高；带 Desert 标记则直接判定
            if (ctx.Desert || (moist < 0.30 && temp > 15.0) || (moist < 0.22 && temp > 8.0))
            {
                return StyleDesert;
            }

            // 2) 北境：冰雪、极地纬度高、夏季过冷
            if (ctx.Snowy || ctx.Tundra || lat >= 55.0 || temp < 5.0)
            {
                return StyleNorth;
            }

            // 3) 山地矿镇：山地 + 矿产或高海拔
            bool ore = ctx.HasIron || ctx.HasCopper || ctx.HasGold || ctx.HasSalt;
            if (ctx.Mountainous && (ore || elev > 1200.0))
            {
                return StyleMine;
            }

            // 4) 海岛贸易：海岛，或河口/三角洲且临海
            if (ctx.OnIsland || (ctx.OnCoast && (ctx.Delta || ctx.RiverMouth)))
            {
                return StyleIsle;
            }

            // 5) 沿海低地：海洋贸易 + 湿润温和 → 海岛贸易；否则农业或水乡
            if (ctx.OnCoast)
            {
                if (moist > 0.45 && temp >= 10.0)
                {
                    return StyleIsle;
                }
                return (ctx.NearRiver || ctx.NearLake) && moist > 0.55 ? StyleWater : StyleFarm;
            }

            // 6) 南方水乡：水网密布 + 温暖湿润
            if ((ctx.NearRiver || ctx.NearLake || ctx.Delta) && moist > 0.50 && temp >= 8.0 && temp <= 30.0)
            {
                return StyleWater;
            }

            // 7) 山地有矿但未达高海拔阈值时也可用矿镇
            if (ctx.Mountainous && (ctx.MountainPass || ore))
            {
                return StyleMine;
            }

            // 8) 其余：平原农业
            return StyleFarm;
        }

        // ═══════════════════════════════════════════════════════════
        //  八、专名（core）生成
        // ═══════════════════════════════════════════════════════════

        /// <summary>生成聚落专名候选（同风格内确定可复现的顺序）。</summary>
        private static List<string> BuildCores(NameContext ctx, int style, int seedKey, int variant)
        {
            string[] mods = StyleModifiers[style];
            string[] nats = StyleNatures[style];
            uint st = MakeState(ctx, seedKey, variant);

            int m0 = RandInt(ref st, mods.Length);
            int n0 = RandInt(ref st, nats.Length);

            string[] seeds =
            {
                mods[m0] + nats[n0],
                mods[RandInt(ref st, mods.Length)] + nats[RandInt(ref st, nats.Length)],
                MagnitudeChars[RandInt(ref st, MagnitudeChars.Length)] + nats[n0],
                MagnitudeChars[RandInt(ref st, MagnitudeChars.Length)] + mods[m0] + nats[n0]
            };

            List<string> pool = new List<string>(24);
            AddCoreCandidates(pool, style, seeds);

            // 全词库稳定遍历，保证候选充足
            List<string> grid = new List<string>(mods.Length * 2 + nats.Length);
            for (int i = 0; i < mods.Length; i++)
            {
                grid.Add(mods[i] + nats[(m0 + i) % nats.Length]);
            }
            for (int i = 0; i < nats.Length; i++)
            {
                grid.Add(mods[(n0 + i) % mods.Length] + nats[i]);
            }
            for (int i = 0; i < nats.Length; i++)
            {
                grid.Add(nats[i] + nats[(n0 + i + 1) % nats.Length]);
            }
            AddCoreCandidates(pool, style, grid);

            // 双自然物（山河、林泉、云溪）
            int a0 = RandInt(ref st, nats.Length);
            int a1 = RandInt(ref st, nats.Length);
            if (a0 != a1)
            {
                string pair = nats[a0] + nats[a1];
                if (IsValidCore(pair, style))
                {
                    pool.Insert(Math.Min(2, pool.Count), pair);
                }
            }

            return pool;
        }

        /// <summary>批量校验并加入专名；词库穷尽时放宽到较弱的候选（保证不返回空表）。</summary>
        private static void AddCoreCandidates(List<string> pool, int style, IList<string> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string c = candidates[i];
                if (!IsValidCore(c, style) || pool.Contains(c))
                {
                    continue;
                }
                // 双通名并列（山河、湖桥）与过于笼统的单字排在后面
                bool weak = !IsStrongCore(c);
                if (weak)
                {
                    pool.Add(c);
                }
                else
                {
                    pool.Insert(Math.Min(6, pool.Count), c);
                }
            }
        }

        /// <summary>生成自然地名专名候选。</summary>
        private static List<string> BuildNaturalCores(NameContext ctx, int style, NaturalFeatureKind kind, int seedKey)
        {
            string[] mods = StyleModifiers[style];
            string[] nats = StyleNatures[style];
            uint st = MakeState(ctx, seedKey, 4000 + (int)kind);

            int m0 = RandInt(ref st, mods.Length);
            int n0 = RandInt(ref st, nats.Length);

            string[] seeds =
            {
                mods[m0] + nats[n0],
                mods[RandInt(ref st, mods.Length)] + nats[RandInt(ref st, nats.Length)],
                MagnitudeChars[RandInt(ref st, MagnitudeChars.Length)] + nats[n0]
            };

            List<string> pool = new List<string>(20);
            AddCoreCandidates(pool, style, seeds);

            // 与要素种类相关的搭配
            string[] kindMates = KindMateChars(kind);
            List<string> mates = new List<string>(kindMates.Length);
            for (int i = 0; i < kindMates.Length; i++)
            {
                mates.Add(mods[(m0 + i) % mods.Length] + kindMates[i]);
            }
            AddCoreCandidates(pool, style, mates);

            List<string> grid = new List<string>(mods.Length + nats.Length);
            for (int i = 0; i < mods.Length; i++)
            {
                grid.Add(mods[i] + nats[(n0 + i) % nats.Length]);
            }
            for (int i = 0; i < nats.Length; i++)
            {
                grid.Add(nats[i + n0 < nats.Length ? i + n0 : i]);
            }
            AddCoreCandidates(pool, style, grid);

            // 双自然物（山河、林泉、云溪）
            int a0 = RandInt(ref st, nats.Length);
            int a1 = RandInt(ref st, nats.Length);
            if (a0 != a1)
            {
                string pair = nats[a0] + nats[a1];
                if (IsValidCore(pair, style) && !pool.Contains(pair))
                {
                    pool.Add(pair);
                }
            }

            return pool;
        }

        /// <summary>自然要素配套通名用字。</summary>
        private static string[] KindMateChars(NaturalFeatureKind kind)
        {
            switch (kind)
            {
                case NaturalFeatureKind.River: return new[] { "川", "溪", "江", "河" };
                case NaturalFeatureKind.Lake: return new[] { "湖", "泉", "渊", "塘" };
                case NaturalFeatureKind.Mountain: return new[] { "峰", "岭", "崖", "谷" };
                case NaturalFeatureKind.Bay: return new[] { "湾", "港", "潮", "浦" };
                case NaturalFeatureKind.Island: return new[] { "岛", "礁", "洲", "屿" };
                default: return new[] { "山", "河", "湖" };
            }
        }

        /// <summary>该要素常用的通名后缀（按优先级）。</summary>
        private static List<string> BuildKindTerms(NaturalFeatureKind kind)
        {
            List<string> l = new List<string>(5);
            switch (kind)
            {
                case NaturalFeatureKind.River:
                    l.Add("河"); l.Add("江"); l.Add("溪"); l.Add("川");
                    break;
                case NaturalFeatureKind.Lake:
                    l.Add("湖"); l.Add("泊"); l.Add("潭"); l.Add("池");
                    break;
                case NaturalFeatureKind.Mountain:
                    l.Add("山"); l.Add("峰"); l.Add("岭"); l.Add("岳");
                    break;
                case NaturalFeatureKind.Bay:
                    l.Add("湾"); l.Add("港"); l.Add("海"); l.Add("口");
                    break;
                case NaturalFeatureKind.Island:
                    l.Add("岛"); l.Add("屿"); l.Add("礁"); l.Add("洲");
                    break;
                default:
                    l.Add("山"); l.Add("河"); l.Add("湖"); l.Add("湾"); l.Add("岛");
                    break;
            }
            return l;
        }

        /// <summary>通名是否与专名重复用字；重复时通名不合适（上碧湖江、青浦河）。</summary>
        private static bool KindTermFits(string core, string term)
        {
            if (core.Length == 0 || term.Length == 0)
            {
                return true;
            }
            for (int i = 0; i < term.Length; i++)
            {
                if (core.IndexOf(term[i]) >= 0)
                {
                    return false;
                }
            }
            // 专名末字是通名时，再叠一个通名也不合适
            return GeoNounChars.IndexOf(core[core.Length - 1]) < 0;
        }

        /// <summary>通名与专名首字同属数量字时也不合适（千峰山、万河谷）。</summary>
        private static bool KindTermAmountClash(string core, string term)
        {
            return core.Length > 0 && term.Length > 0
                   && AmountChars.IndexOf(core[0]) >= 0
                   && AmountChars.IndexOf(term[0]) >= 0;
        }

        /// <summary>是否为较强的专名（双通名并列、单字通名偏弱）。</summary>
        private static bool IsStrongCore(string core)
        {
            int geo = 0;
            for (int i = 0; i < core.Length; i++)
            {
                if (GeoNounChars.IndexOf(core[i]) >= 0)
                {
                    geo++;
                }
            }
            return geo <= 1;
        }

        /// <summary>自然要素对应的通用通名字（回退用）。</summary>
        private static string GenericOf(NaturalFeatureKind kind)
        {
            switch (kind)
            {
                case NaturalFeatureKind.River: return "河";
                case NaturalFeatureKind.Lake: return "湖";
                case NaturalFeatureKind.Mountain: return "山";
                case NaturalFeatureKind.Bay: return "湾";
                case NaturalFeatureKind.Island: return "岛";
                default: return "山";
            }
        }

        /// <summary>专名本身是否合法（长度、字符、风格禁忌、语义禁忌）。</summary>
        private static bool IsValidCore(string core, int style)
        {
            if (core.Length < 2 || core.Length > 3)
            {
                return false;
            }
            if (!CharsAllowed(core) || !ContainsStyleToken(core, style))
            {
                return false;
            }
            if (HasForbiddenChar(core, style) || HasForbiddenPair(core) || HasRepeatedChar(core))
            {
                return false;
            }
            // 过于笼统的通名不宜单独作专名（「金漠驿」「蓝潮镇」之类）
            int last = core.Length - 1;
            if (core.Length <= 2 && WeakCoreChars.IndexOf(core[last]) >= 0)
            {
                return false;
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  九、后缀选择（等级 + 功能）
        // ═══════════════════════════════════════════════════════════

        /// <summary>按等级与功能生成候选聚落后缀，功能性强者优先。</summary>
        private static List<string> BuildSuffixes(NameContext ctx, int style)
        {
            int level = LevelOf(ctx);
            bool capital = ctx.IsCapital || level >= 4;
            double pop = ctx.Population;

            // 收集候选：等级基础后缀 + 功能后缀
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string s in StyleSuffixes[style])
            {
                set.Add(s);
            }
            foreach (string s in LevelSuffixes(level, capital))
            {
                set.Add(s);
            }
            if (ctx.OnCoast || ctx.Delta || ctx.RiverMouth)
            {
                AddAll(set, new[] { "港", "埠", "渡", "津", "湾" });
            }
            if (ctx.MountainPass)
            {
                AddAll(set, new[] { "关", "塞", "口", "隘" });
            }
            if (ctx.HasIron || ctx.HasCopper || ctx.HasGold || ctx.HasSalt)
            {
                AddAll(set, new[] { "矿", "铁", "盐", "石" });
            }
            if (ctx.RiverConfluence || pop >= 20000.0)
            {
                AddAll(set, new[] { "市", "集", "商", "驿" });
            }

            // 打分排序：分数相同时按字序，保证确定性
            List<string> list = new List<string>(set);
            list.Sort((x, y) =>
            {
                int sx = SuffixScore(x, level, capital, ctx, style);
                int sy = SuffixScore(y, level, capital, ctx, style);
                if (sx != sy)
                {
                    return sy - sx;
                }
                return string.CompareOrdinal(x, y);
            });
            return list;
        }

        private static void AddAll(HashSet<string> set, string[] items)
        {
            foreach (string s in items)
            {
                set.Add(s);
            }
        }

        /// <summary>各等级的基础后缀。</summary>
        private static string[] LevelSuffixes(int level, bool capital)
        {
            switch (level)
            {
                case 1:
                    return new[] { "村", "庄", "屯", "寨", "集" };
                case 2:
                    return new[] { "镇", "堡", "驿", "关", "渡" };
                case 3:
                    return capital ? new[] { "京", "都", "城", "州", "府" } : new[] { "城", "州", "府", "港" };
                case 4:
                    return new[] { "京", "都", "城", "州", "府" };
                default:
                    return new[] { "村", "镇", "城" };
            }
        }

        /// <summary>后缀与地理/功能/等级的匹配打分（越大越优先）。</summary>
        private static int SuffixScore(string suf, int level, bool capital, in NameContext ctx, int style)
        {
            int score = 0;

            // 等级匹配：都/城 优先用 京、都、城、州、府
            if (level >= 4)
            {
                score += suf == "京" ? 46 : suf == "都" ? 44 : suf == "城" ? 30 : suf == "州" ? 20 : suf == "府" ? 18 : 0;
            }
            else if (level == 3)
            {
                score += suf == "城" ? 34 : suf == "州" ? 24 : suf == "府" ? 22 : suf == "港" ? 16 : 0;
            }
            else if (level == 2)
            {
                score += suf == "镇" ? 30 : suf == "堡" ? 20 : suf == "驿" ? 18 : suf == "关" ? 14 : suf == "渡" ? 14 : 0;
            }
            else
            {
                score += suf == "村" ? 30 : suf == "庄" ? 20 : suf == "屯" ? 16 : suf == "寨" ? 14 : suf == "集" ? 12 : 0;

                // 村庄不用「市 / 商」这类商业城市后缀，避免「某某村」配「市」
                if (suf == "市") score -= 30;
                else if (suf == "商") score -= 24;
            }

            // 功能匹配
            if (ctx.OnCoast || ctx.Delta || ctx.RiverMouth)
            {
                score += suf == "港" ? 40 : suf == "埠" ? 34 : suf == "渡" ? 18 : suf == "津" ? 16 : suf == "湾" ? 14 : 0;
            }
            if (ctx.MountainPass)
            {
                score += suf == "关" ? 36 : suf == "塞" ? 30 : suf == "口" ? 18 : suf == "隘" ? 16 : 0;
            }
            if (ctx.HasIron || ctx.HasCopper || ctx.HasGold || ctx.HasSalt)
            {
                score += suf == "矿" ? 30 : suf == "铁" ? 24 : suf == "盐" ? 22 : suf == "石" ? 14 : 0;
            }
            if (ctx.RiverConfluence || ctx.Population >= 20000.0)
            {
                score += suf == "市" ? 28 : suf == "商" ? 20 : suf == "集" ? 18 : suf == "驿" ? 16 : 0;
            }
            if (capital)
            {
                score += suf == "京" ? 40 : suf == "都" ? 38 : suf == "州" ? 16 : suf == "府" ? 14 : 0;
            }
            if (ctx.Population >= 100000.0)
            {
                score += suf == "城" ? 10 : suf == "州" ? 6 : suf == "府" ? 6 : 0;
            }

            // 风格倾向
            if (style == StyleDesert)
            {
                score += suf == "驿" ? 20 : suf == "堡" ? 12 : suf == "寨" ? 10 : 0;
            }
            else if (style == StyleNorth)
            {
                score += suf == "堡" ? 12 : suf == "关" ? 12 : suf == "寨" ? 10 : 0;
            }
            else if (style == StyleIsle)
            {
                score += suf == "港" ? 22 : suf == "埠" ? 18 : 0;
            }
            else if (style == StyleWater)
            {
                score += suf == "渡" ? 16 : suf == "桥" ? 14 : suf == "村" ? 8 : 0;
            }
            else if (style == StyleMine)
            {
                score += suf == "寨" ? 10 : suf == "堡" ? 10 : suf == "矿" ? 20 : 0;
            }
            else if (style == StyleFarm)
            {
                score += suf == "庄" ? 12 : suf == "州" ? 8 : suf == "府" ? 8 : 0;
            }

            return score;
        }

        // ═══════════════════════════════════════════════════════════
        //  十、冲突避让与禁忌检查
        // ═══════════════════════════════════════════════════════════

        /// <summary>方位/规模避让词：多次重试时逐步展开（保证确定性）。</summary>
        private static string DirectionFor(int attempt, int seedKey, in NameContext ctx)
        {
            // 常用避让词按固定优先级排列
            string[] pool = { "上", "下", "新", "东", "西", "南", "北", "大", "小", "旧" };
            int idx = (attempt - 1) % pool.Length;
            uint st = HashState(seedKey, ctx.CellX, ctx.CellY, attempt);
            if (NextRand(ref st) < 0.5)
            {
                return pool[idx];
            }
            return pool[(idx + 3) % pool.Length];
        }

        /// <summary>判断候选名是否可用：长度、字符、禁忌、去重、同音近似。</summary>
        private static bool IsAcceptable(string cand, string core, int style, ISet<string> used)
        {
            if (string.IsNullOrEmpty(cand))
            {
                return false;
            }

            if (cand.Length < 2 || cand.Length > 6 || !CharsAllowed(cand))
            {
                return false;
            }
            if (HasRepeatedChar(cand))
            {
                return false;
            }
            if (CountSuffixChars(cand) > MaxSuffixChars)
            {
                return false;
            }
            if (HasBlacklistedSuffix(cand))
            {
                return false;
            }
            if (HasForbiddenPair(cand))
            {
                return false;
            }
            if (HasForbiddenChar(core, style) || HasForbiddenChar(cand, style))
            {
                return false;
            }
            if (!ContainsStyleToken(cand, style))
            {
                return false;
            }
            if (IsUsed(cand, core, used))
            {
                return false;
            }
            return true;
        }

        /// <summary>与 used 完全同名、专名相同或同音归一化相同都算冲突。</summary>
        private static bool IsUsed(string cand, string core, ISet<string> used)
        {
            if (used == null)
            {
                return false;
            }
            if (used.Contains(cand))
            {
                return true;
            }

            string canon = NormalizeHomophones(cand);
            string coreCanon = NormalizeHomophones(core);
            foreach (string u in used)
            {
                if (string.IsNullOrEmpty(u))
                {
                    continue;
                }
                if (u == cand)
                {
                    return true;
                }
                if (StripSuffix(u) == core)
                {
                    return true;
                }
                string nu = NormalizeHomophones(u);
                if (nu == canon || nu == coreCanon)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>记录到 used（可能为 null）。</summary>
        private static void Commit(string name, ISet<string> used)
        {
            if (used != null && !string.IsNullOrEmpty(name))
            {
                used.Add(name);
            }
        }

        /// <summary>极端回退：仍然构造一个长度与字符都合规的名字。</summary>
        private static string HardFallback(NameContext ctx, int seedKey, int level, int style, string tail)
        {
            uint st = HashState(seedKey, ctx.CellX, ctx.CellY, 99991 + (int)ctx.Population);
            string[] mods = StyleModifiers[style];
            string[] nats = StyleNatures[style];
            string mod = mods[RandInt(ref st, mods.Length)];
            string nat = nats[RandInt(ref st, nats.Length)];
            string suf = tail.Length > 0 ? tail : (level >= 3 ? "城" : level == 2 ? "镇" : "村");

            string name = mod + nat + suf;
            if (HasRepeatedChar(name) || HasForbiddenPair(name))
            {
                name = nats[RandInt(ref st, nats.Length)] + suf;
            }
            if (name.Length > 6)
            {
                name = name.Substring(0, 6);
            }
            if (name.Length < 2)
            {
                name = "无名";
            }
            return name;
        }

        // ═══════════════════════════════════════════════════════════
        //  十一、过滤与规范化工具
        // ═══════════════════════════════════════════════════════════

        /// <summary>所有字符都必须在允许字库内。</summary>
        private static bool CharsAllowed(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (!Allowed.Contains(s[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>必须包含至少一个本风格包的字（避免生造跨风格词）。</summary>
        private static bool ContainsStyleToken(string s, int style)
        {
            if (style < 0 || style >= StyleCount)
            {
                return true;
            }
            HashSet<string> set = StyleCharSets[style];
            for (int i = 0; i < s.Length; i++)
            {
                if (set.Contains(s[i].ToString()))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否触发该风格的语义禁忌字。</summary>
        private static bool HasForbiddenChar(string s, int style)
        {
            if (style < 0 || style >= StyleCount)
            {
                return false;
            }
            string[] forbidden = StyleForbiddenChars[style];
            for (int i = 0; i < s.Length; i++)
            {
                for (int j = 0; j < forbidden.Length; j++)
                {
                    if (forbidden[j].Length == 1 && forbidden[j][0] == s[i])
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>是否命中全局禁忌组合。</summary>
        private static bool HasForbiddenPair(string s)
        {
            for (int i = 0; i < ForbiddenPairs.Length; i++)
            {
                if (s.Contains(ForbiddenPairs[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否被禁用的后缀形式。</summary>
        private static bool HasBlacklistedSuffix(string s)
        {
            for (int i = 0; i < SuffixBlacklist.Length; i++)
            {
                if (s.EndsWith(SuffixBlacklist[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否出现连续相同汉字（黑黑山、水水村）。</summary>
        private static bool HasRepeatedChar(string s)
        {
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] == s[i - 1] && RepeatAllowedChars.IndexOf(s[i]) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>统计聚落后缀字数量，用于禁止后缀堆叠（白河城港镇）。</summary>
        private static int CountSuffixChars(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (SuffixChars.IndexOf(s[i]) >= 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>资源通名数量（矿、铁、盐、石 至多一个）。</summary>
        private static int CountResourceChars(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (ResourceChars.IndexOf(s[i]) >= 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>资源后缀不得与行政/交通/水域后缀连用。</summary>
        private static bool HasResourceStack(string s)
        {
            return CountResourceChars(s) > 1
                   || (CountResourceChars(s) > 0 && CountSuffixChars(s) > 0);
        }

        /// <summary>取字符串最后一个字符（空串返回 '\0'）。</summary>
        private static char LastChar(string s)
        {
            return string.IsNullOrEmpty(s) ? '\0' : s[s.Length - 1];
        }

        /// <summary>通名的语义组（专名末尾通名用）。</summary>
        private static int CoreGroup(char c)
        {
            switch (c)
            {
                case '河': case '湖': case '江': case '溪': case '浦': case '塘': case '湾': case '港':
                    return GroupWater;
                case '峰': case '岭': case '谷': case '崖': case '丘': case '山': case '矿': case '石': case '关':
                    return GroupMount;
                case '岛': case '洲': case '礁': case '屿': case '海': case '潮': case '漠':
                    return GroupOther;
                case '原': case '林': case '松': case '狼': case '鱼': case '泉': case '桥':
                    return GroupOther;
                default:
                    return GroupOther;
            }
        }

        /// <summary>聚落后缀的语义类别（单个后缀可能同时属于多类）。</summary>
        private static int SuffixGroup(string suf)
        {
            switch (suf)
            {
                case "港": case "埠": case "津": case "湾": case "渡": case "浦": case "口":
                    return GroupWater | GroupTransport;
                case "关": case "塞": case "隘":
                    return GroupMount | GroupTransport;
                case "矿": case "铁": case "盐": case "石":
                    return GroupResource;
                case "市": case "集": case "商": case "驿":
                    return GroupTrade | GroupTransport;
                case "京": case "都": case "城": case "州": case "府": case "镇":
                    return GroupAdmin;
                case "村": case "庄": case "屯": case "寨": case "堡":
                    return GroupAdmin | GroupTransport;
                case "桥":
                    return GroupWater | GroupTrade;
                default:
                    return GroupOther;
            }
        }

        /// <summary>后缀能否挂在以该通名结尾的专名之后。</summary>
        private static bool FitsBase(string core, string suf)
        {
            char baseCh = LastChar(core);
            if (baseCh == '\0' || string.IsNullOrEmpty(suf) || suf[0] == baseCh)
            {
                return false;
            }
            int allowed;
            switch (CoreGroup(baseCh))
            {
                case GroupWater: allowed = GroupWater | GroupTrade | GroupTransport; break;
                case GroupMount: allowed = GroupMount | GroupResource | GroupTrade | GroupTransport; break;
                default: return true;
            }
            return (SuffixGroup(suf) & allowed) != 0;
        }

        /// <summary>后缀与前缀（方位/数字）之间的重复字。</summary>
        private static bool HasPrefixSuffixClash(string s)
        {
            if (s.Length < 2)
            {
                return false;
            }
            return PrefixChars.IndexOf(s[0]) >= 0 && s[0] == s[s.Length - 1];
        }

        /// <summary>去掉末尾的聚落后缀，得到专名。</summary>
        private static string StripSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            int end = name.Length;
            while (end > 1 && SuffixChars.IndexOf(name[end - 1]) >= 0)
            {
                end--;
            }
            return name.Substring(0, end);
        }

        /// <summary>同音归一化：把易混字替换成组内代表字后再比较。</summary>
        private static string NormalizeHomophones(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            char[] buf = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char rep;
                buf[i] = Homophone.TryGetValue(c, out rep) ? rep : c;
            }
            return new string(buf);
        }

        // ═══════════════════════════════════════════════════════════
        //  十二、国名政体
        // ═══════════════════════════════════════════════════════════

        /// <summary>按风格包与人口规模给政体排序（寒地/游牧偏汗国、公国，贸易偏城邦，农耕偏王国、帝国）。</summary>
        private static List<string> BuildPolityOrder(NameContext ctx, int style, int seedKey)
        {
            double pop = ctx.Population;
            string[] all = { "王国", "帝国", "联邦", "公国", "汗国", "城邦", "共和国" };
            List<string> list = new List<string>(all.Length);
            int[] score = new int[all.Length];

            for (int i = 0; i < all.Length; i++)
            {
                score[i] = 10;
            }

            // 风格加权
            switch (style)
            {
                case StyleNorth:
                    score[0] += 20;  // 王国
                    score[3] += 16;  // 公国
                    score[4] += 12;  // 汗国
                    score[6] -= 6;   // 共和国
                    break;
                case StyleFarm:
                    score[0] += 24;  // 王国
                    score[1] += 18;  // 帝国
                    score[2] += 6;   // 联邦
                    score[4] -= 4;   // 汗国
                    break;
                case StyleIsle:
                    score[5] += 26;  // 城邦
                    score[2] += 16;  // 联邦
                    score[6] += 10;  // 共和国
                    score[1] -= 4;   // 帝国
                    break;
                case StyleDesert:
                    score[4] += 26;  // 汗国
                    score[3] += 14;  // 公国
                    score[0] += 8;   // 王国
                    break;
                case StyleWater:
                    score[0] += 18;  // 王国
                    score[5] += 14;  // 城邦
                    score[2] += 10;  // 联邦
                    break;
                case StyleMine:
                    score[3] += 18;  // 公国
                    score[2] += 14;  // 联邦
                    score[5] += 12;  // 城邦
                    score[1] += 8;   // 帝国
                    break;
            }

            // 人口规模加权
            if (pop >= 500000.0)
            {
                score[1] += 26;  // 帝国
                score[6] += 14;  // 共和国
                score[0] += 10;  // 王国
                score[5] -= 8;   // 城邦
            }
            else if (pop >= 100000.0)
            {
                score[0] += 16;
                score[1] += 10;
                score[2] += 8;
            }
            else if (pop < 30000.0)
            {
                score[3] += 12;  // 公国
                score[5] += 12;  // 城邦
                score[4] += 8;   // 汗国
                score[1] -= 10;  // 帝国
                score[6] -= 6;   // 共和国
            }

            for (int i = 0; i < all.Length; i++)
            {
                list.Add(all[i]);
            }
            // 稳定排序：分数相同按原顺序（原顺序本身就是一种确定性）
            List<int> order = new List<int>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                order.Add(i);
            }
            order.Sort((x, y) =>
            {
                if (score[x] != score[y])
                {
                    return score[y] - score[x];
                }
                return x - y;
            });

            List<string> result = new List<string>(all.Length);
            for (int i = 0; i < order.Count; i++)
            {
                result.Add(all[order[i]]);
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  十三、小工具
        // ═══════════════════════════════════════════════════════════

        /// <summary>调试辅助：返回该上下文的后缀候选顺序（不影响生成结果）。</summary>
        public static List<string> DebugSuffixes(in NameContext ctx)
        {
            return BuildSuffixes(ctx, PickStyle(ctx));
        }

        /// <summary>调试辅助：检查候选名是否可用。</summary>
        public static bool DebugAcceptable(string cand, string core, in NameContext ctx, ISet<string> used)
        {
            return IsAcceptable(cand, core, PickStyle(ctx), used);
        }

        /// <summary>调试辅助：单步重放 NameSettlement 的候选顺序与判定结果。</summary>
        public static List<string> DebugSettlementTrace(in NameContext ctx, ISet<string> used, int seedKey)
        {
            int style = PickStyle(ctx);
            List<string> cores = BuildCores(ctx, style, seedKey, 0);
            List<string> suffixes = BuildSuffixes(ctx, style);
            List<string> trace = new List<string>();
            for (int attempt = 0; attempt < 8 && cores.Count > 0 && suffixes.Count > 0; attempt++)
            {
                string dir = attempt == 0 ? string.Empty : DirectionFor(attempt, seedKey, ctx);
                string core = cores[(attempt >> 1) % cores.Count];
                string suf = suffixes[attempt % suffixes.Count];
                string cand = dir + core + suf;
                trace.Add(attempt + ":" + cand + (IsAcceptable(cand, core, style, used) ? "=OK" : "=NO"));
            }
            return trace;
        }

        private static int LevelOf(NameContext ctx)
        {
            int lv = ctx.Level;
            if (lv < 1)
            {
                lv = 1;
            }
            if (lv > 4)
            {
                lv = 4;
            }
            if (ctx.IsCapital && lv < 4)
            {
                lv = 4;
            }
            return lv;
        }

        private static int MapW(NameContext ctx)
        {
            return ctx.MapWidth > 0 ? ctx.MapWidth : 2048;
        }

        private static int MapH(NameContext ctx)
        {
            return ctx.MapHeight > 0 ? ctx.MapHeight : 1024;
        }

        private static double Safe(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return lo;
            }
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
