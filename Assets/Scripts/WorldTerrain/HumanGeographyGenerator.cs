using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 人文地理生成：
    ///   自然条件（气候/水文/地形/资源）
    ///   → 宜居度 → 人口势场
    ///   → 聚落（加权泊松盘采样，按势场定等级）
    ///   → 国家（首都 + 成本距离竞争面）
    ///   → 人口分配（腹地人口 + Zipf 律）
    ///   → 自然要素与聚落的中文命名（CultureNameGenerator）
    ///   → 交通网络（引力模型 + 最小生成树 + 寻路）
    ///
    /// 只使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class HumanGeographyGenerator
    {
        // ── 参数 ──

        /// <summary>粗网格目标宽度（成本距离、寻路都在粗网格上做，保证速度）。</summary>
        private const int CoarseWidth = 256;

        /// <summary>每个势场单位对应的人口数（地球尺度下每格约 250 km²）。</summary>
        private const double PeoplePerPotential = 420.0;

        /// <summary>城市规模分布的 Zipf 指数：第 r 大城市人口 ≈ 最大城市 / r^α。</summary>
        private const double ZipfAlpha = 0.9;

        /// <summary>每多少陆地势场单位生成一个聚落。</summary>
        private const double PotentialPerSettlement = 3500.0;

        private const int MinSettlements = 24;
        private const int MaxSettlements = 320;

        /// <summary>海洋在势力范围/腹地计算中的通行成本（视为难以跨越）。</summary>
        private const float SeaTerritoryCost = 40f;

        /// <summary>海洋在交通线计算中的成本（海运比翻山便宜，但需要靠港）。</summary>
        private const float SeaRouteCost = 1.6f;

        // 8 邻域
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// 生成人文图层。
        /// </summary>
        /// <param name="heightField">高度场（米）</param>
        /// <param name="hydrology">水文结果（河流/湖泊/三角洲）</param>
        /// <param name="axialTilt">地轴倾角（度），用于气候</param>
        /// <param name="seed">世界种子（保证同一世界人文可复现）</param>
        /// <param name="onProgress">进度回调 0-1</param>
        public static HumanGeographyData Generate(int width, int height, float[] heightField,
            HydrologyData hydrology, float axialTilt, SeededNoise noise, int seed,
            Action<float> onProgress = null, float[] moistureField = null)
        {
            int w = width, h = height, n = w * h;
            var data = new HumanGeographyData { seed = seed, mapAspect = (float)w / h };

            onProgress?.Invoke(0.02f);

            // ── 1. 自然条件：气温、湿度、到水距离、地形起伏、资源 ──

            int[] seaDist = ClimateModel.SeaDistance(w, h, heightField);
            int[] waterDist = DistanceToWater(w, h, heightField, hydrology);

            double[] rowSinLat = new double[h];
            double[] rowCosLat = new double[h];
            for (int y = 0; y < h; y++)
            {
                double lat = Math.PI * 0.5 - (double)y / (h - 1) * Math.PI;
                rowSinLat[y] = Math.Sin(lat);
                rowCosLat[y] = Math.Cos(lat);
            }

            float maxLand = 0f;
            for (int i = 0; i < n; i++)
                if (heightField[i] > maxLand) maxLand = heightField[i];
            if (maxLand < 1f) maxLand = 1f;

            float[] habitability = new float[n];
            float[] temperature = new float[n];
            float[] moisture = new float[n];
            float[] potential = new float[n];
            double[] colCosLon = new double[w];
            double[] colSinLon = new double[w];
            for (int x = 0; x < w; x++)
            {
                double lon = (double)x / (w - 1) * 2.0 * Math.PI - Math.PI;
                colCosLon[x] = Math.Cos(lon);
                colSinLon[x] = Math.Sin(lon);
            }

            double landWeight = 0.0;
            for (int y = 0; y < h; y++)
            {
                double absLat = Math.Abs(90.0 - (double)y / (h - 1) * 180.0);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float elev = heightField[i];
                    if (elev <= 0f)
                        continue;

                    var pos = new Vector3D(rowCosLat[y] * colCosLon[x], rowSinLat[y],
                                           rowCosLat[y] * colSinLon[x]);

                    // 气温：海平面最热月气温 − 直减率
                    double t = PolarClimate.SeaLevelSummerTemperature(absLat, axialTilt)
                               - 5.5 * (elev / 1000.0);
                    temperature[i] = (float)t;

                    double m = moistureField != null && moistureField.Length > i
                        ? moistureField[i]
                        : ClimateModel.Moisture(absLat, seaDist[i], noise, pos);
                    moisture[i] = (float)m;

                    // 地形起伏（与 8 邻域的高差）
                    int cxx = x, cyy = y;
                    float lo = elev, hi = elev;
                    for (int d = 0; d < 8; d++)
                    {
                        int ny = cyy + DY8[d];
                        if (ny < 0 || ny >= h) continue;
                        int j = ny * w + ((cxx + DX8[d] + w) % w);
                        float hj = heightField[j];
                        if (hj < lo) lo = hj;
                        if (hj > hi) hi = hj;
                    }
                    float relief = hi - lo;

                    // 宜居度各项
                    double tempScore = Math.Exp(-Math.Pow((t - 18.0) / 15.0, 2.0));      // 温和最宜居
                    double moistScore = Clamp01(1.0 - Math.Abs(m - 0.62) / 0.5);          // 半湿润最好
                    double riverScore = 1.0 / (1.0 + waterDist[i] * 0.10);                // 靠近河湖
                    double coastScore = Math.Exp(-seaDist[i] * 0.10);                     // 靠海（渔业/贸易）
                    double reliefScore = 1.0 / (1.0 + relief / (maxLand * 0.30));         // 太崎岖不宜居
                    double floodPenalty = (elev < maxLand * 0.02 && waterDist[i] <= 1) ? 0.35 : 0.0;
                    double coldPenalty = t < 0.0 ? 0.5 : 0.0;

                    double habit = tempScore * 0.55
                                 + moistScore * 0.35
                                 + riverScore * 0.35
                                 + coastScore * 0.20
                                 + reliefScore * 0.25
                                 - floodPenalty - coldPenalty;

                    habit = habit * 0.5 + 0.5 * tempScore * moistScore;                   // 保证极地/沙漠显著偏低
                    habit = Clamp01(habit);
                    habitability[i] = (float)habit;

                    // 人口势场 = 宜居度 × 多尺度噪声
                    double n1 = noise.Noise3D(pos.x * 2.0 + 31.0, pos.y * 2.0 + 31.0, pos.z * 2.0);
                    double n2 = noise.Noise3D(pos.x * 6.0 + 71.0, pos.y * 6.0 + 71.0, pos.z * 6.0);
                    double mult = 0.45 + 1.05 * (n1 * 0.65 + n2 * 0.35);
                    potential[i] = (float)(habit * mult);
                    landWeight += rowCosLat[y] * potential[i];
                }
            }

            onProgress?.Invoke(0.25f);

            // ── 2. 聚落采样：按人口势场加权的泊松盘采样 ──

            var settlements = SampleSettlements(w, h, heightField, potential, noise);
            if (settlements.Count == 0)
                return data;

            // ── 3. 粗网格：腹地、势力范围、交通线都在它上面算 ──

            int D = Math.Max(1, w / CoarseWidth);
            int cw = Math.Max(8, w / D), ch = Math.Max(8, h / D);
            float[] landCost = BuildCoarseCost(w, h, heightField, potential, D, cw, ch, SeaTerritoryCost, out float[] coarsePotential);
            float[] routeCost = BuildRouteCost(w, h, heightField, hydrology, D, cw, ch,
                out float[] coarseRiverFrac, out float[] coarseSeaFrac);
            int[] owner = new int[cw * ch];
            float[] dist = new float[cw * ch];

            // 每个聚落的腹地人口（多源成本距离）
            var sources = new List<int>(settlements.Count);
            for (int i = 0; i < settlements.Count; i++)
                sources.Add(CoarseIndex(settlements[i].cellX, settlements[i].cellY, D, cw, ch));

            MultiSource(landCost, cw, ch, sources, owner, dist);
            var hinterland = new double[settlements.Count];
            for (int c = 0; c < cw * ch; c++)
            {
                int o = owner[c];
                if (o >= 0) hinterland[o] += coarsePotential[c];
            }

            onProgress?.Invoke(0.5f);

            // ── 4. 国家：首都 + 成本距离竞争面 ──

            var order = new List<int>();
            for (int i = 0; i < settlements.Count; i++) order.Add(i);
            order.Sort((a, b) => hinterland[b].CompareTo(hinterland[a]));

            int capitalCount = Math.Max(3, Math.Min(12, settlements.Count / 12));
            var capitalCells = new List<int>();
            var capitalSettlements = new List<int>();
            int minCapitalSep = Math.Max(12, cw / 6);
            foreach (int si in order)
            {
                int cx = settlements[si].cellX / D, cy = settlements[si].cellY / D;
                bool ok = true;
                foreach (int cc in capitalCells)
                {
                    int ox = cc % cw, oy = cc / cw;
                    if (Math.Abs(ox - cx) + Math.Abs(oy - cy) < minCapitalSep) { ok = false; break; }
                }
                if (!ok) continue;

                capitalCells.Add(cy * cw + cx);
                capitalSettlements.Add(si);
                if (capitalCells.Count >= capitalCount) break;
            }

            int[] nationOwner = new int[cw * ch];
            float[] nationDist = new float[cw * ch];
            MultiSource(landCost, cw, ch, capitalCells, nationOwner, nationDist);

            int nationCount = capitalCells.Count;
            var nationOf = new int[settlements.Count];
            for (int i = 0; i < settlements.Count; i++)
            {
                int cx = settlements[i].cellX / D, cy = settlements[i].cellY / D;
                int o = nationOwner[cy * cw + cx];
                nationOf[i] = o >= 0 ? o : 0;
            }

            // 合并过小的国家：不足 3 个聚落的势力并入最近的大国
            var nationSize = new int[nationCount];
            for (int i = 0; i < settlements.Count; i++)
                nationSize[nationOf[i]]++;

            var remap = new int[nationCount];
            for (int i = 0; i < nationCount; i++)
                remap[i] = -1;

            var keptCapitals = new List<int>();
            var keptCapitalCells = new List<int>();
            for (int i = 0; i < nationCount; i++)
            {
                if (nationSize[i] < 3)
                    continue;
                remap[i] = keptCapitals.Count;
                keptCapitals.Add(capitalSettlements[i]);
                keptCapitalCells.Add(capitalCells[i]);
            }

            if (keptCapitals.Count == 0)
            {
                // 极端情况：全部都很小，保留最大的一个
                int biggest = 0;
                for (int i = 1; i < nationCount; i++)
                    if (nationSize[i] > nationSize[biggest])
                        biggest = i;
                remap[biggest] = 0;
                keptCapitals.Add(capitalSettlements[biggest]);
                keptCapitalCells.Add(capitalCells[biggest]);
            }

            for (int i = 0; i < settlements.Count; i++)
            {
                if (remap[nationOf[i]] >= 0)
                    continue;

                // 找最近的保留首都
                int best = 0;
                double bestDist = double.MaxValue;
                for (int k = 0; k < keptCapitals.Count; k++)
                {
                    int dcx = settlements[keptCapitals[k]].cellX - settlements[i].cellX;
                    int dcy = settlements[keptCapitals[k]].cellY - settlements[i].cellY;
                    double dd = (double)dcx * dcx + (double)dcy * dcy;
                    if (dd < bestDist)
                    {
                        bestDist = dd;
                        best = k;
                    }
                }
                nationOf[i] = best;
            }

            for (int i = 0; i < settlements.Count; i++)
                nationOf[i] = remap[nationOf[i]] >= 0 ? remap[nationOf[i]] : nationOf[i];

            capitalSettlements = keptCapitals;
            capitalCells = keptCapitalCells;
            nationCount = capitalSettlements.Count;

            // ── 5. 人口：腹地人口 → Zipf 分布 → 等级 ──

            double totalPotential = 0.0;
            for (int i = 0; i < settlements.Count; i++)
                totalPotential += Math.Max(1.0, hinterland[i]);

            double totalPopulation = landWeight * PeoplePerPotential;
            double zipfSum = 0.0;
            for (int r = 1; r <= settlements.Count; r++)
                zipfSum += Math.Pow(r, -ZipfAlpha);

            var rankOf = new int[settlements.Count];
            for (int r = 0; r < settlements.Count; r++)
                rankOf[order[r]] = r;

            var population = new double[settlements.Count];
            for (int i = 0; i < settlements.Count; i++)
            {
                int r = rankOf[i] + 1;
                double pop = totalPopulation * Math.Pow(r, -ZipfAlpha) / zipfSum;

                // 腹地人口做小幅修正（±25%），使人口分布与地形相关
                double ratio = hinterland[i] / Math.Max(1.0, totalPotential / settlements.Count);
                pop *= Clamp(0.75 + 0.25 * ratio, 0.6, 1.4);
                population[i] = Math.Max(80.0, pop);
            }

            // 等级：首都 = 都，其余按人口分位
            int rankCit = Math.Max(1, (int)(settlements.Count * 0.10));
            int rankTown = Math.Max(rankCit + 1, (int)(settlements.Count * 0.35));
            var level = new int[settlements.Count];
            for (int i = 0; i < settlements.Count; i++)
            {
                int r = rankOf[i] + 1;
                if (capitalSettlements.Contains(i)) level[i] = 4;
                else if (r <= rankCit) level[i] = 3;
                else if (r <= rankTown) level[i] = 2;
                else level[i] = 1;
            }

            onProgress?.Invoke(0.65f);

            // ── 6. 中文命名：自然要素 → 聚落 → 国家 ──

            var used = new HashSet<string>();
            NameNaturalFeatures(w, h, heightField, hydrology, temperature, moisture,
                noise, seed, used, data.features);

            var contexts = new NameContext[settlements.Count];
            for (int i = 0; i < settlements.Count; i++)
            {
                var s = settlements[i];
                var ctx = BuildContext(s.cellX, s.cellY, w, h, heightField, hydrology,
                    temperature, moisture, seaDist, waterDist, maxLand, noise, level[i],
                    capitalSettlements.Contains(i), nationOf[i], population[i]);
                contexts[i] = ctx;

                string name = CultureNameGenerator.NameSettlement(ctx, used, seed + i * 131 + 7);
                var sd = new SettlementData
                {
                    id = $"s{i + 1:D3}",
                    name = name,
                    level = level[i],
                    population = (int)Math.Round(population[i]),
                    rank = rankOf[i] + 1,
                    nationIndex = nationOf[i],
                    style = CultureNameGenerator.StyleOf(ctx),
                    isCapital = capitalSettlements.Contains(i),
                    isCoastal = ctx.OnCoast,
                    cellX = s.cellX,
                    cellY = s.cellY,
                    u = (s.cellX + 0.5f) / w,
                    v = (s.cellY + 0.5f) / h,
                    features = BuildFeatureLabels(ctx),
                    lat = (float)(90.0 - (s.cellY + 0.5) / h * 180.0),
                    lon = (float)((s.cellX + 0.5) / w * 360.0 - 180.0),
                    elevation = (float)ctx.Elevation,
                    moisture = (float)ctx.Moisture,
                    summerTemp = (float)ctx.SummerTemp,
                    nearRiver = ctx.NearRiver,
                    nearLake = ctx.NearLake,
                    onCoast = ctx.OnCoast,
                    mountainous = ctx.Mountainous,
                    snowy = ctx.Snowy,
                    desert = ctx.Desert,
                    forest = ctx.Forest,
                    steppe = ctx.Steppe,
                    tundra = ctx.Tundra,
                    riverConfluence = ctx.RiverConfluence,
                    delta = ctx.Delta,
                    riverMouth = ctx.RiverMouth,
                    mountainPass = ctx.MountainPass,
                    hasIron = ctx.HasIron,
                    hasCopper = ctx.HasCopper,
                    hasGold = ctx.HasGold,
                    hasSalt = ctx.HasSalt
                };
                data.settlements.Add(sd);
            }

            for (int i = 0; i < nationCount; i++)
            {
                int capital = capitalSettlements[i];
                var ctx = contexts[capital];
                string core = StripSuffix(data.settlements[capital].name);
                string nationName = CultureNameGenerator.NameNation(core, ctx, used, seed + 9001 + i * 37);
                string polity = NationPolity(nationName);

                long total = 0;
                int count = 0;
                for (int k = 0; k < settlements.Count; k++)
                {
                    if (nationOf[k] != i) continue;
                    total += data.settlements[k].population;
                    count++;
                }

                data.nations.Add(new NationData
                {
                    id = $"n{i + 1:D3}",
                    name = nationName,
                    polity = polity,
                    capitalIndex = capital,
                    settlementCount = count,
                    totalPopulation = total,
                    r = 0, g = 0, b = 0
                });
            }

            AssignNationColors(data.nations);

            // 势力范围栅格（粗网格，供叠加图层绘制国界；只标记陆地）
            var territory = new byte[cw * ch];
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    int c = cy * cw + cx;
                    int o = nationOwner[c];
                    if (o < 0 || nationDist[c] >= 1e9f)
                        continue;
                    if (!IsCoarseLand(w, h, heightField, cx, cy, D))
                        continue;                       // 海洋不着色
                    territory[c] = (byte)Math.Min(254, o + 1);
                }
            }
            data.SetTerritory(territory, cw, ch);

            onProgress?.Invoke(0.8f);

            // ── 7. 交通网络：引力模型 + 最小生成树 + 寻路 ──

            BuildRoutes(data, settlements, population, cw, ch, D, routeCost,
                coarseRiverFrac, coarseSeaFrac);

            long worldPop = 0;
            for (int i = 0; i < data.settlements.Count; i++)
                worldPop += data.settlements[i].population;
            data.totalPopulation = worldPop;

            onProgress?.Invoke(1f);
            return data;
        }

        // ── 聚落采样 ──

        private struct Candidate
        {
            public int cellX, cellY, level;
            public float potential;
        }

        /// <summary>
        /// 按人口势场加权的泊松盘采样：优先在势场高处落点，
        /// 且与已有聚落保持与其等级相称的间距。
        /// </summary>
        private static List<Candidate> SampleSettlements(int w, int h, float[] heightField,
            float[] potential, SeededNoise noise)
        {
            int n = w * h;
            var land = new List<int>();
            double weight = 0.0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (heightField[i] <= 0f) continue;
                    if (potential[i] < 0.03f) continue;
                    land.Add(i);
                    weight += potential[i];
                }
            }

            int target = (int)Math.Round(weight / PotentialPerSettlement);
            if (target < MinSettlements) target = Math.Min(MinSettlements, land.Count);
            if (target > MaxSettlements) target = MaxSettlements;
            if (target <= 0)
                return new List<Candidate>();

            // 按势场降序
            var keys = new float[land.Count];
            var items = new int[land.Count];
            for (int i = 0; i < land.Count; i++)
            {
                items[i] = land[i];
                keys[i] = potential[land[i]];
            }
            Array.Sort(keys, items);
            Array.Reverse(keys);
            Array.Reverse(items);

            // 等级阈值：按分位（前 10% 为城、前 35% 为镇，其余为村）
            int idxCity = Math.Max(1, (int)(items.Length * 0.10));
            int idxTown = Math.Max(idxCity + 1, (int)(items.Length * 0.35));

            int gridSize = 24;
            int gw = w / gridSize + 2, gh = h / gridSize + 2;
            var grid = new List<int>[gw * gh];
            var result = new List<Candidate>();

            for (int k = 0; k < items.Length && result.Count < target; k++)
            {
                int idx = items[k];
                int x = idx % w, y = idx / w;

                int level = k < idxCity ? 3 : (k < idxTown ? 2 : 1);
                int radius = level == 3 ? 18 : (level == 2 ? 10 : 5);

                bool tooClose = false;
                int gx = x / gridSize, gy = y / gridSize;
                int reach = radius / gridSize + 1;
                for (int oy = -reach; oy <= reach && !tooClose; oy++)
                {
                    int ny = gy + oy;
                    if (ny < 0 || ny >= gh) continue;
                    for (int ox = -reach; ox <= reach && !tooClose; ox++)
                    {
                        int nx = ((gx + ox) % gw + gw) % gw;
                        var bucket = grid[ny * gw + nx];
                        if (bucket == null) continue;
                        foreach (int other in bucket)
                        {
                            int oyy = other / w, oxx = other % w;
                            int dx = Math.Abs(oxx - x);
                            if (dx > w / 2) dx = w - dx;
                            int dy = Math.Abs(oyy - y);
                            if (dx * dx + dy * dy < radius * radius) { tooClose = true; break; }
                        }
                    }
                }

                if (tooClose)
                    continue;

                if (grid[gy * gw + gx] == null)
                    grid[gy * gw + gx] = new List<int>();
                grid[gy * gw + gx].Add(idx);

                result.Add(new Candidate
                {
                    cellX = x,
                    cellY = y,
                    level = level,
                    potential = potential[idx]
                });
            }

            return result;
        }

        // ── 粗网格与成本 ──

        private static float[] BuildCoarseCost(int w, int h, float[] heightField, float[] potential,
            int D, int cw, int ch, float seaCost, out float[] coarsePotential)
        {
            float[] cost = new float[cw * ch];
            coarsePotential = new float[cw * ch];

            float maxLand = 1f;
            for (int i = 0; i < heightField.Length; i++)
                if (heightField[i] > maxLand) maxLand = heightField[i];

            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    float pot = 0f;
                    int sea = 0, total = 0;
                    for (int y = cy * D; y < Math.Min(h, (cy + 1) * D); y++)
                    {
                        for (int x = cx * D; x < Math.Min(w, (cx + 1) * D); x++)
                        {
                            int i = y * w + x;
                            float e = heightField[i];
                            if (e < lo) lo = e;
                            if (e > hi) hi = e;
                            if (e <= 0f) sea++;
                            pot += potential[i];
                            total++;
                        }
                    }
                    if (total == 0) { cost[cy * cw + cx] = 1f; continue; }

                    float seaFrac = (float)sea / total;
                    float relief = (hi - lo) / maxLand;
                    float c = 1f + relief * 3.0f;
                    c += seaFrac * seaFrac * seaCost;
                    cost[cy * cw + cx] = c;
                    coarsePotential[cy * cw + cx] = pot;
                }
            }

            return cost;
        }

        /// <summary>交通线用的成本：海运算便宜、河谷好走、山地昂贵；同时给出每格的河流/海洋占比。</summary>
        private static float[] BuildRouteCost(int w, int h, float[] heightField, HydrologyData hydro,
            int D, int cw, int ch, out float[] riverFrac, out float[] seaFrac)
        {
            float[] cost = new float[cw * ch];
            riverFrac = new float[cw * ch];
            seaFrac = new float[cw * ch];

            float maxLand = 1f;
            for (int i = 0; i < heightField.Length; i++)
                if (heightField[i] > maxLand) maxLand = heightField[i];

            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    int sea = 0, river = 0, total = 0;
                    for (int y = cy * D; y < Math.Min(h, (cy + 1) * D); y++)
                    {
                        for (int x = cx * D; x < Math.Min(w, (cx + 1) * D); x++)
                        {
                            int i = y * w + x;
                            float e = heightField[i];
                            if (e < lo) lo = e;
                            if (e > hi) hi = e;
                            if (e <= 0f) sea++;
                            if ((hydro.riverMask != null && hydro.riverMask[i])
                                || (hydro.deltaMask != null && hydro.deltaMask[i]))
                                river++;
                            total++;
                        }
                    }
                    if (total == 0) { cost[cy * cw + cx] = 1f; continue; }

                    float sf = (float)sea / total;
                    float rf = (float)river / total;
                    float relief = (hi - lo) / maxLand;

                    seaFrac[cy * cw + cx] = sf;
                    riverFrac[cy * cw + cx] = rf;

                    // 河谷（河运）比翻山便宜，海洋最便宜但需要靠港
                    float landPart = (1f - sf) * (1f + relief * 2.5f) * (1f - 0.55f * rf);
                    cost[cy * cw + cx] = landPart + sf * SeaRouteCost;
                }
            }

            return cost;
        }

        private static bool IsCoarseLand(int w, int h, float[] heightField, int cx, int cy, int D)
        {
            int x = Math.Min(w - 1, cx * D + D / 2);
            int y = Math.Min(h - 1, cy * D + D / 2);
            return heightField[y * w + x] > 0f;
        }

        private static int CoarseIndex(int x, int y, int D, int cw, int ch)
        {
            int cx = x / D, cy = y / D;
            if (cx < 0) cx = 0;
            if (cx >= cw) cx = cw - 1;
            if (cy < 0) cy = 0;
            if (cy >= ch) cy = ch - 1;
            return cy * cw + cx;
        }

        /// <summary>
        /// 多源 Dijkstra：得到每个粗格归属哪个源，以及到源的成本距离。
        ///
        /// 步长按**真实距离**折算，而不是按粗格格数：等距圆柱图上东西向格子的真实长度
        /// 是南北向的 cos(纬度) 倍（低纬相等、75° 处只有 1/4），
        /// 按格收费等于把高纬的东西向旅行算贵了 1/cos(lat) 倍（75° 处 3.86×），
        /// 势力范围与腹地因此会偏向赤道。步长用 √((dx·cosφ)² + dy²) 后，
        /// 同一个成本距离在任意纬度都代表同样的大圆距离。
        /// </summary>
        private static void MultiSource(float[] cost, int cw, int ch, List<int> sources,
            int[] owner, float[] dist)
        {
            int m = cw * ch;
            for (int i = 0; i < m; i++) { owner[i] = -1; dist[i] = float.MaxValue; }

            float[] rowLatScale = RowLatitudeScale(ch);
            var heap = new MinHeap(m);
            for (int s = 0; s < sources.Count; s++)
            {
                int c = sources[s];
                if (c < 0 || c >= m) continue;
                if (dist[c] > 0f)
                {
                    dist[c] = 0f;
                    owner[c] = s;
                    heap.Push(c, 0f);
                }
            }

            while (heap.Count > 0)
            {
                int c = heap.Pop(out float d);
                if (d > dist[c]) continue;

                int cx = c % cw, cy = c / cw;
                for (int k = 0; k < 8; k++)
                {
                    int ny = cy + DY8[k];
                    if (ny < 0 || ny >= ch) continue;
                    int nx = (cx + DX8[k] + cw) % cw;
                    int j = ny * cw + nx;

                    float step = cost[j] * StepLength(DX8[k], DY8[k], rowLatScale[cy], rowLatScale[ny],
                        !UseRealStepLength);
                    float nd = d + step;
                    if (nd < dist[j])
                    {
                        dist[j] = nd;
                        owner[j] = owner[c];
                        heap.Push(j, nd);
                    }
                }
            }
        }

        /// <summary>
        /// 每一步的真实几何长度（以「南北向一格 = 1」为单位）：
        /// 东西分量按所在纬度的 cos 收缩，取起止两行的均值。
        ///
        /// <paramref name="legacyCellStep"/> = true 时退回旧口径（东西与南北都算 1 格），
        /// 只用于离线验证台做 A/B 对比：证明「按格收费」在高纬把东西向距离算贵了多少。
        /// </summary>
        private static float StepLength(int dx, int dy, float latScaleFrom, float latScaleTo,
            bool legacyCellStep = false)
        {
            if (legacyCellStep)
                return dx != 0 && dy != 0 ? 1.4142f : 1f;

            float cos = (latScaleFrom + latScaleTo) * 0.5f;
            float ex = dx * cos;
            return (float)Math.Sqrt(ex * ex + (float)dy * dy);
        }

        /// <summary>
        /// 成本距离是否按真实距离收费。默认 true；离线验证台用
        /// <c>-p:DefineConstants=LEGACY_DISTANCE</c> 编出旧口径做 A/B 对比。
        /// </summary>
#if LEGACY_DISTANCE
        public const bool UseRealStepLength = false;
#else
        public const bool UseRealStepLength = true;
#endif

        /// <summary>每行的东西向收缩系数 cos(纬度)：行号 ↔ 纬度的换算与高度场一致。</summary>
        private static float[] RowLatitudeScale(int ch)
        {
            var scale = new float[ch];
            for (int cy = 0; cy < ch; cy++)
            {
                double lat = Math.PI * 0.5 - (cy + 0.5) / ch * Math.PI;
                scale[cy] = (float)Math.Cos(lat);
            }
            return scale;
        }

        /// <summary>
        /// 诊断：给定纬度上「东西向一步」与「南北向一步」的成本距离之比（理想 = 1.00）。
        ///
        /// 成本距离的语义是「支付 = 真实距离 × 单位成本」，所以同纬度上东西一步与南北一步
        /// 的**地理**长度比是 cos(lat)（75° 处只有 0.26）—— 成本距离之比也应等于 cos(lat)。
        /// 若按格数收费（<paramref name="legacyStep"/> = true），东西与南北都收 1 个单位，
        /// 比值恒为 1.00，等于把高纬的东西向旅行算贵了 1/cos(lat) 倍。
        /// </summary>
        /// <param name="coarseWidth">粗网格宽度（与 Generate 里一致：w / D）</param>
        public static void MeasureCostAnisotropy(int coarseWidth, out double[] latitudes,
            out double[] legacyEastNorthRatio, out double[] realEastNorthRatio)
        {
            var lat = new List<double>();
            var legacy = new List<double>();
            var real = new List<double>();

            foreach (double deg in new[] { 0.0, 30.0, 45.0, 60.0, 75.0 })
            {
                double cos = Math.Cos(deg * Math.PI / 180.0);
                lat.Add(deg);
                legacy.Add(1.0);                       // 旧口径：两个方向都收 1 个单位
                real.Add(cos);                         // 真实几何：东西一步 = cos(lat) 个南北步长
            }

            latitudes = lat.ToArray();
            legacyEastNorthRatio = legacy.ToArray();
            realEastNorthRatio = real.ToArray();
        }

        // ── 命名上下文 ──

        private static NameContext BuildContext(int x, int y, int w, int h, float[] heightField,
            HydrologyData hydro, float[] temperature, float[] moisture, int[] seaDist, int[] waterDist,
            float maxLand, SeededNoise noise, int level, bool isCapital, int nationIndex, double population)
        {
            int i = y * w + x;
            double absLat = Math.Abs(90.0 - (double)y / (h - 1) * 180.0);
            float elev = heightField[i];
            float relief = LocalRelief(x, y, w, h, heightField);

            bool nearRiver = hydro.riverMask != null && waterDist[i] <= 2;
            bool nearLake = hydro.lakeMask != null && waterDist[i] <= 2;
            bool onCoast = seaDist[i] <= 1;
            bool delta = hydro.deltaMask != null && NearbyFlag(x, y, w, h, hydro.deltaMask, 2);
            bool confluence = CountRiverNeighbours(x, y, w, h, hydro.riverMask) >= 3;
            bool mouth = hydro.estuaryMask != null && NearbyFlag(x, y, w, h, hydro.estuaryMask, 3);

            // 资源：由噪声决定（山地多金属矿、沙漠多盐）
            double ore = noise.Noise3D(x * 0.02, y * 0.02, 5.0);
            bool mountainous = relief > maxLand * 0.28;
            bool desert = moisture[i] < 0.30f;
            bool snowy = temperature[i] < 1f;
            bool forest = moisture[i] > 0.60f && temperature[i] > 4f;
            bool tundra = temperature[i] < 5f && moisture[i] < 0.5f;

            return new NameContext
            {
                AbsLat = absLat,
                Elevation = elev,
                Moisture = moisture[i],
                SummerTemp = temperature[i],
                Population = population,
                Level = level,
                IsCapital = isCapital,
                NationIndex = nationIndex,
                NearRiver = nearRiver,
                NearLake = nearLake,
                OnCoast = onCoast,
                OnIsland = onCoast && relief > maxLand * 0.10,
                Mountainous = mountainous,
                Snowy = snowy,
                Desert = desert,
                Forest = forest,
                Steppe = !forest && !desert && moisture[i] < 0.55f && temperature[i] > 5f,
                Tundra = tundra,
                RiverConfluence = confluence,
                RiverMouth = mouth,
                Delta = delta,
                MountainPass = mountainous && waterDist[i] <= 4 && relief > maxLand * 0.40,
                HasIron = mountainous && ore > 0.62,
                HasCopper = mountainous && ore > 0.72,
                HasGold = mountainous && ore > 0.84,
                HasSalt = desert && ore > 0.55,
                CellX = x,
                CellY = y,
                MapWidth = w,
                MapHeight = h
            };
        }

        private static List<string> BuildFeatureLabels(in NameContext ctx)
        {
            var list = new List<string>();
            if (ctx.Mountainous) list.Add("山地");
            if (ctx.Snowy) list.Add("寒冷");
            if (ctx.Desert) list.Add("干旱");
            if (ctx.Forest) list.Add("森林");
            if (ctx.Steppe) list.Add("草原");
            if (ctx.Tundra) list.Add("苔原");
            if (ctx.RiverConfluence) list.Add("河流交汇");
            if (ctx.NearRiver) list.Add("河流");
            if (ctx.NearLake) list.Add("湖泊");
            if (ctx.Delta) list.Add("三角洲");
            if (ctx.RiverMouth) list.Add("河口");
            if (ctx.OnCoast) list.Add("沿海");
            if (ctx.MountainPass) list.Add("山口");
            if (ctx.HasIron) list.Add("铁矿");
            if (ctx.HasCopper) list.Add("铜矿");
            if (ctx.HasGold) list.Add("金矿");
            if (ctx.HasSalt) list.Add("盐矿");
            return list;
        }

        /// <summary>自然要素命名：主要河流与湖泊。</summary>
        private static void NameNaturalFeatures(int w, int h, float[] heightField, HydrologyData hydro,
            float[] temperature, float[] moisture, SeededNoise noise, int seed,
            HashSet<string> used, List<NamedFeatureData> features)
        {
            if (hydro.riverMask == null)
                return;

            // 主要河流：河宽较大且连通的河道
            var seen = new bool[w * h];
            var stack = new Stack<int>();
            var candidates = new List<KeyValuePair<float, int>>();   // 规模 → 代表格

            for (int start = 0; start < w * h; start++)
            {
                if (!hydro.riverMask[start] || seen[start] || hydro.riverWidth[start] < 3f)
                    continue;

                seen[start] = true;
                stack.Push(start);
                int best = start;
                float bestWidth = hydro.riverWidth[start];

                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int x = idx % w, y = idx / w;
                    if (hydro.riverWidth[idx] > bestWidth)
                    {
                        bestWidth = hydro.riverWidth[idx];
                        best = idx;
                    }
                    for (int d = 0; d < 8; d++)
                    {
                        int ny = y + DY8[d];
                        if (ny < 0 || ny >= h) continue;
                        int j = ny * w + ((x + DX8[d] + w) % w);
                        if (!seen[j] && hydro.riverMask[j] && hydro.riverWidth[j] >= 1.5f)
                        {
                            seen[j] = true;
                            stack.Push(j);
                        }
                    }
                }

                candidates.Add(new KeyValuePair<float, int>(bestWidth, best));
            }

            candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            int riverNames = Math.Min(14, candidates.Count);
            var seaDist = ClimateModel.SeaDistance(w, h, heightField);
            var waterDist = DistanceToWater(w, h, heightField, hydro);

            for (int k = 0; k < riverNames; k++)
            {
                int idx = candidates[k].Value;
                int x = idx % w, y = idx / w;
                var ctx = BuildContext(x, y, w, h, heightField, hydro, temperature, moisture,
                    seaDist, waterDist, 1f, noise, 1, false, 0, 0);
                string name = CultureNameGenerator.NameNatural(ctx, NaturalFeatureKind.River, used, seed + 500 + k);
                features.Add(new NamedFeatureData
                {
                    id = $"f{k + 1:D3}",
                    name = name,
                    kind = (int)NamedFeatureKind.River,
                    u = (x + 0.5f) / w,
                    v = (y + 0.5f) / h,
                    lat = (float)(90.0 - (y + 0.5) / h * 180.0),
                    lon = (float)((x + 0.5) / w * 360.0 - 180.0),
                    importance = Math.Min(1f, candidates[k].Key / 10f)
                });
            }

            // 主要湖泊
            if (hydro.lakeMask != null)
            {
                var lakeSeen = new bool[w * h];
                var lakes = new List<KeyValuePair<int, int>>();
                for (int start = 0; start < w * h; start++)
                {
                    if (!hydro.lakeMask[start] || lakeSeen[start]) continue;
                    lakeSeen[start] = true;
                    stack.Push(start);
                    int count = 0, best = start;
                    float bestDepth = 0f;
                    while (stack.Count > 0)
                    {
                        int idx = stack.Pop();
                        count++;
                        if (hydro.waterDepth != null && hydro.waterDepth[idx] > bestDepth)
                        {
                            bestDepth = hydro.waterDepth[idx];
                            best = idx;
                        }
                        int x = idx % w, y = idx / w;
                        for (int d = 0; d < 8; d++)
                        {
                            int ny = y + DY8[d];
                            if (ny < 0 || ny >= h) continue;
                            int j = ny * w + ((x + DX8[d] + w) % w);
                            if (hydro.lakeMask[j] && !lakeSeen[j])
                            {
                                lakeSeen[j] = true;
                                stack.Push(j);
                            }
                        }
                    }
                    lakes.Add(new KeyValuePair<int, int>(count, best));
                }

                lakes.Sort((a, b) => b.Key.CompareTo(a.Key));
                int lakeNames = Math.Min(10, lakes.Count);
                for (int k = 0; k < lakeNames; k++)
                {
                    int idx = lakes[k].Value;
                    int x = idx % w, y = idx / w;
                    var ctx = BuildContext(x, y, w, h, heightField, hydro, temperature, moisture,
                        seaDist, waterDist, 1f, noise, 1, false, 0, 0);
                    string name = CultureNameGenerator.NameNatural(ctx, NaturalFeatureKind.Lake, used, seed + 700 + k);
                    features.Add(new NamedFeatureData
                    {
                        id = $"f{500 + k:D3}",
                        name = name,
                        kind = (int)NamedFeatureKind.Lake,
                        u = (x + 0.5f) / w,
                        v = (y + 0.5f) / h,
                        lat = (float)(90.0 - (y + 0.5) / h * 180.0),
                        lon = (float)((x + 0.5) / w * 360.0 - 180.0),
                        importance = Math.Min(1f, lakes[k].Key / 400f)
                    });
                }
            }
        }

        // ── 交通网络 ──

        private static void BuildRoutes(HumanGeographyData data, List<Candidate> settlements,
            double[] population, int cw, int ch, int D, float[] routeCost,
            float[] riverFrac, float[] seaFrac)
        {
            int s = settlements.Count;
            if (s < 2)
                return;

            // 候选边：引力模型强度 = 人口_i × 人口_j / 距离^1.5
            var edges = new List<Edge>();
            const double gamma = 1.5;
            for (int i = 0; i < s; i++)
            {
                for (int j = i + 1; j < s; j++)
                {
                    double dx = settlements[i].cellX - settlements[j].cellX;
                    double dy = settlements[i].cellY - settlements[j].cellY;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > cw * D * 0.8) continue;                  // 太远不成边
                    double strength = population[i] * population[j] / Math.Pow(Math.Max(1.0, d), gamma);
                    edges.Add(new Edge { a = i, b = j, strength = strength });
                }
            }
            if (edges.Count == 0)
                return;

            edges.Sort((x, y) => y.strength.CompareTo(x.strength));

            // 最小生成树（按强度降序的 Kruskal）+ 高强度附加边
            var parent = new int[s];
            for (int i = 0; i < s; i++) parent[i] = i;

            int extraBudget = Math.Max(2, s / 3);
            var chosen = new List<Edge>();
            foreach (var e in edges)
            {
                bool connect = Find(parent, e.a) != Find(parent, e.b);
                if (connect)
                {
                    Union(parent, e.a, e.b);
                    chosen.Add(e);
                }
                else if (extraBudget > 0 && chosen.Count < s - 1 + s / 3)
                {
                    extraBudget--;
                    chosen.Add(e);
                }
                if (chosen.Count >= (s - 1) + s / 3)
                    break;
            }

            // 寻路
            var path = new List<int>();
            var cameFrom = new int[cw * ch];
            var dist = new float[cw * ch];
            var visited = new bool[cw * ch];

            foreach (var e in chosen)
            {
                int start = CoarseIndex(settlements[e.a].cellX, settlements[e.a].cellY, D, cw, ch);
                int goal = CoarseIndex(settlements[e.b].cellX, settlements[e.b].cellY, D, cw, ch);

                path.Clear();
                if (!FindPath(routeCost, cw, ch, start, goal, cameFrom, dist, visited, path))
                    continue;

                var route = new RouteData
                {
                    fromIndex = e.a,
                    toIndex = e.b,
                    traffic = (long)Math.Min(long.MaxValue, e.strength / 1000.0),
                    points = new List<UnityEngine.Vector2>()
                };

                double seaSum = 0.0, riverSum = 0.0;
                for (int k = 0; k < path.Count; k++)
                {
                    int c = path[k];
                    int cx = c % cw, cy = c / cw;
                    // 采样：每隔几格取一个点，减少数据量
                    if (k % 2 == 0 || k == path.Count - 1)
                    {
                        float u = (cx * D + D * 0.5f) / (cw * D);
                        float v = (cy * D + D * 0.5f) / (ch * D);
                        route.points.Add(new UnityEngine.Vector2(u, v));
                    }
                    seaSum += seaFrac[c];
                    riverSum += riverFrac[c];
                }

                double seaAvg = path.Count > 0 ? seaSum / path.Count : 0.0;
                double riverAvg = path.Count > 0 ? riverSum / path.Count : 0.0;
                route.kind = seaAvg > 0.25 ? (int)RouteKind.Sea
                           : riverAvg > 0.25 ? (int)RouteKind.River
                           : (int)RouteKind.Land;

                data.routes.Add(route);
            }
        }

        private struct Edge
        {
            public int a, b;
            public double strength;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        /// <summary>
        /// 粗网格上的 Dijkstra 寻路（成本场已包含地形与海洋权重）。
        /// 步长同样按真实距离折算（见 <see cref="MultiSource"/>），
        /// 否则高纬的路径会为了省"格数"而绕远，选出的交通线在真实距离上并不最短。
        /// </summary>
        private static bool FindPath(float[] cost, int cw, int ch, int start, int goal,
            int[] cameFrom, float[] dist, bool[] visited, List<int> path)
        {
            int m = cw * ch;
            if (start < 0 || start >= m || goal < 0 || goal >= m)
                return false;

            for (int i = 0; i < m; i++)
            {
                dist[i] = float.MaxValue;
                visited[i] = false;
                cameFrom[i] = -1;
            }

            float[] rowLatScale = RowLatitudeScale(ch);
            var heap = new MinHeap(m);
            dist[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int c = heap.Pop(out float d);
                if (visited[c]) continue;
                visited[c] = true;
                if (c == goal) break;

                int cx = c % cw, cy = c / cw;
                for (int k = 0; k < 8; k++)
                {
                    int ny = cy + DY8[k];
                    if (ny < 0 || ny >= ch) continue;
                    int nx = (cx + DX8[k] + cw) % cw;
                    int j = ny * cw + nx;
                    if (visited[j]) continue;

                    float step = cost[j] * StepLength(DX8[k], DY8[k], rowLatScale[cy], rowLatScale[ny],
                        !UseRealStepLength);
                    float nd = d + step;
                    if (nd < dist[j])
                    {
                        dist[j] = nd;
                        cameFrom[j] = c;
                        heap.Push(j, nd);
                    }
                }
            }

            if (!visited[goal])
                return false;

            int cur = goal;
            while (cur >= 0)
            {
                path.Add(cur);
                cur = cameFrom[cur];
            }
            path.Reverse();
            return true;
        }

        // ── 杂项 ──

        private static void AssignNationColors(List<NationData> nations)
        {
            int count = Math.Max(1, nations.Count);
            for (int i = 0; i < nations.Count; i++)
            {
                double h = (double)i / count;
                HsvToRgb(h, 0.62, 0.95, out float r, out float g, out float b);
                nations[i].r = r;
                nations[i].g = g;
                nations[i].b = b;
            }
        }

        private static void HsvToRgb(double h, double s, double v,
            out float r, out float g, out float b)
        {
            double c = v * s;
            double hp = h * 6.0;
            double x = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
            double m = v - c;
            double rr, gg, bb;
            if (hp < 1) { rr = c; gg = x; bb = 0; }
            else if (hp < 2) { rr = x; gg = c; bb = 0; }
            else if (hp < 3) { rr = 0; gg = c; bb = x; }
            else if (hp < 4) { rr = 0; gg = x; bb = c; }
            else if (hp < 5) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            r = (float)(rr + m);
            g = (float)(gg + m);
            b = (float)(bb + m);
        }

        /// <summary>去掉聚落后缀，得到专名（用于国名）。</summary>
        private static string StripSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "无名";

            string[] suffixes = { "首府", "村", "庄", "屯", "寨", "集", "镇", "堡", "驿", "关", "渡",
                                  "城", "港", "州", "府", "京", "都", "埠", "津", "湾" };
            foreach (string s in suffixes)
            {
                if (name.Length > s.Length && name.EndsWith(s, StringComparison.Ordinal))
                    return name.Substring(0, name.Length - s.Length);
            }
            return name;
        }

        private static string NationPolity(string nationName)
        {
            string[] polities = { "王国", "帝国", "联邦", "公国", "汗国", "城邦", "共和国" };
            foreach (string p in polities)
                if (nationName != null && nationName.EndsWith(p, StringComparison.Ordinal))
                    return p;
            return "";
        }

        /// <summary>到最近河流/湖泊的距离（格）。</summary>
        private static int[] DistanceToWater(int w, int h, float[] heightField, HydrologyData hydro)
        {
            int n = w * h;
            int[] dist = new int[n];
            for (int i = 0; i < n; i++) dist[i] = -1;

            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                bool water = (hydro.riverMask != null && hydro.riverMask[i])
                          || (hydro.lakeMask != null && hydro.lakeMask[i])
                          || (hydro.deltaMask != null && hydro.deltaMask[i]);
                if (water)
                {
                    dist[i] = 0;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w, y = idx / w;
                int d = dist[idx];
                for (int k = 0; k < 8; k++)
                {
                    int ny = y + DY8[k];
                    if (ny < 0 || ny >= h) continue;
                    int j = ny * w + ((x + DX8[k] + w) % w);
                    if (dist[j] < 0)
                    {
                        dist[j] = d + 1;
                        queue.Enqueue(j);
                    }
                }
            }

            for (int i = 0; i < n; i++)
                if (dist[i] < 0)
                    dist[i] = 999;
            return dist;
        }

        private static float LocalRelief(int x, int y, int w, int h, float[] heightField)
        {
            int i = y * w + x;
            float lo = heightField[i], hi = heightField[i];
            for (int d = 0; d < 8; d++)
            {
                int ny = y + DY8[d];
                if (ny < 0 || ny >= h) continue;
                int j = ny * w + ((x + DX8[d] + w) % w);
                float e = heightField[j];
                if (e < lo) lo = e;
                if (e > hi) hi = e;
            }
            return hi - lo;
        }

        private static int CountRiverNeighbours(int x, int y, int w, int h, bool[] mask)
        {
            if (mask == null)
                return 0;
            int count = 0;
            for (int d = 0; d < 8; d++)
            {
                int ny = y + DY8[d];
                if (ny < 0 || ny >= h) continue;
                if (mask[ny * w + ((x + DX8[d] + w) % w)])
                    count++;
            }
            return count;
        }

        private static bool NearbyFlag(int x, int y, int w, int h, bool[] mask, int radius)
        {
            if (mask == null)
                return false;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= h) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = (x + dx + w) % w;
                    if (mask[ny * w + nx])
                        return true;
                }
            }
            return false;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>以 float 为键的索引最小堆。</summary>
        private sealed class MinHeap
        {
            private int[] items;
            private float[] keys;
            private int count;

            public MinHeap(int capacity)
            {
                items = new int[Math.Max(16, capacity / 8)];
                keys = new float[items.Length];
            }

            public int Count => count;

            public void Push(int item, float key)
            {
                if (count == items.Length)
                {
                    Array.Resize(ref items, count * 2);
                    Array.Resize(ref keys, count * 2);
                }
                items[count] = item;
                keys[count] = key;
                int i = count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (keys[i] < keys[p])
                    {
                        Swap(i, p);
                        i = p;
                    }
                    else break;
                }
            }

            public int Pop(out float key)
            {
                int result = items[0];
                key = keys[0];
                count--;
                if (count > 0)
                {
                    items[0] = items[count];
                    keys[0] = keys[count];
                    int i = 0;
                    while (true)
                    {
                        int l = (i << 1) + 1, r = l + 1, s = i;
                        if (l < count && keys[l] < keys[s]) s = l;
                        if (r < count && keys[r] < keys[s]) s = r;
                        if (s == i) break;
                        Swap(i, s);
                        i = s;
                    }
                }
                return result;
            }

            private void Swap(int a, int b)
            {
                int ti = items[a]; items[a] = items[b]; items[b] = ti;
                float tk = keys[a]; keys[a] = keys[b]; keys[b] = tk;
            }
        }
    }
}
