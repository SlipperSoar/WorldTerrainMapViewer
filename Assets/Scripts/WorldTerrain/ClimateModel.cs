using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 气候场：到海距离与湿度。水文生成（季节性河流、沙漠沿途渗漏）与
    /// 地形分类（沙漠/苔原/雨林）共用同一套判定，保证两者一致。
    /// 仅使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class ClimateModel
    {
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>水汽在大陆上的衰减尺度（格）：约 90 格 ≈ 上千公里（赤道处）。</summary>
        private const double ContinentalScale = 90.0;

        /// <summary>
        /// 水汽衰减尺度（公里）——球面几何修正用。
        /// 赤道处 90 格 × (40075 km / 地图宽度) ≈ 1800 km，因此这个取值保持原有赤道行为不变，
        /// 只修正纬度依赖（等距圆柱投影下一格的实际东西向距离随 cos(lat) 收缩）。
        /// </summary>
        private const double ContinentalScaleKm = 1800.0;

        /// <summary>地球周长（公里），用于把格数换算成实际距离。</summary>
        private const double EarthCircumferenceKm = 40075.0;

        /// <summary>地形抬升的降水尺度（米）：上升这么多米，水汽大部分被拦下。</summary>
        private const double OrographicScale = 900.0;

        /// <summary>经度接缝抹平：每行越过末端后继续推进的格数（≈2 倍水汽衰减尺度）。</summary>
        private const int WrapExtraCells = 180;

        // ── 风带经验公式的参数（见「行星地图生成优化建议」推算）──

        /// <summary>热赤道迁移系数 k：热赤道纬度 φ_T = k·δ_eff。</summary>
        public const double ThermalEquatorFactor = 0.55;

        /// <summary>热惯性滞后（天）：大气与海洋对太阳辐射的响应滞后约 25 天。</summary>
        public const double ThermalLagDays = 25.0;

        /// <summary>春分对应的日序（用于由日序换算太阳黄经）。</summary>
        public const double SpringEquinoxDay = 80.0;

        /// <summary>回归年长度（天）。</summary>
        public const double TropicalYear = 365.25;

        /// <summary>热赤道饱和上限（度）：倾角很大时抑制过猛的季节摆动。</summary>
        public const double ThermalEquatorSaturation = 30.0;

        /// <summary>风带强度季节调制系数 β。</summary>
        public const double WindStrengthBeta = 0.4;

        /// <summary>年平均风带的基准纬度（副高 / 西风 / 极地东风）。</summary>
        private const double BaseSubtropical = 30.0;
        private const double BaseWesterly = 45.0;
        private const double BasePolar = 65.0;

        /// <summary>
        /// 太阳赤纬 δ（度）：δ = arcsin(sin ε · sin λ)，
        /// λ 为由日序换算的太阳黄经（春分为 0°，夏至 90°）。
        /// </summary>
        public static double SolarDeclination(double axialTiltDeg, double dayOfYear)
        {
            double eps = axialTiltDeg * Math.PI / 180.0;
            double lambda = SolarLongitude(dayOfYear);
            return Math.Asin(Math.Sin(eps) * Math.Sin(lambda)) * 180.0 / Math.PI;
        }

        /// <summary>太阳黄经 λ（弧度）：春分为 0，夏至 π/2。</summary>
        public static double SolarLongitude(double dayOfYear)
        {
            return 2.0 * Math.PI * (dayOfYear - SpringEquinoxDay) / TropicalYear;
        }

        /// <summary>考虑热惯性滞后后的有效赤纬 δ_eff（度）。</summary>
        public static double EffectiveDeclination(double axialTiltDeg, double dayOfYear)
        {
            double eps = axialTiltDeg * Math.PI / 180.0;
            double lambdaEff = SolarLongitude(dayOfYear)
                             - 2.0 * Math.PI * ThermalLagDays / TropicalYear;
            return Math.Asin(Math.Sin(eps) * Math.Sin(lambdaEff)) * 180.0 / Math.PI;
        }

        /// <summary>
        /// 热赤道纬度 φ_T（度）：跟随滞后后的太阳赤纬，但迁移幅度更小，
        /// 并对大倾角做 tanh 饱和以免季节摆动过猛。
        /// </summary>
        public static double ThermalEquatorLatitude(double axialTiltDeg, double dayOfYear)
        {
            double raw = ThermalEquatorFactor * EffectiveDeclination(axialTiltDeg, dayOfYear);
            return ThermalEquatorSaturation * Math.Tanh(raw / ThermalEquatorSaturation);
        }

        /// <summary>
        /// 各风带中心纬度（度）：φ_i = 基准纬度 + w_i · φ_T。
        /// 南北半球同向迁移（北半球夏季时南半球各带也整体北移）。
        /// </summary>
        public static void WindBandCenters(double axialTiltDeg, double dayOfYear,
            out double subtropicalNorth, out double westerlyNorth, out double polarNorth,
            out double subtropicalSouth, out double westerlySouth, out double polarSouth)
        {
            double phiT = ThermalEquatorLatitude(axialTiltDeg, dayOfYear);
            subtropicalNorth = BaseSubtropical + 0.50 * phiT;
            westerlyNorth = BaseWesterly + 0.35 * phiT;
            polarNorth = BasePolar + 0.15 * phiT;
            subtropicalSouth = -BaseSubtropical + 0.50 * phiT;
            westerlySouth = -BaseWesterly + 0.35 * phiT;
            polarSouth = -BasePolar + 0.15 * phiT;
        }

        /// <summary>
        /// 半球风带强度因子：冬半球经向温差大、环流更强。
        /// S_N = 1 + β·(-δ_eff/ε)，S_S = 1 + β·(δ_eff/ε)。
        /// </summary>
        public static void WindStrength(double axialTiltDeg, double dayOfYear,
            out double north, out double south)
        {
            if (axialTiltDeg < 1e-3)
            {
                north = 1.0;
                south = 1.0;
                return;
            }

            double ratio = EffectiveDeclination(axialTiltDeg, dayOfYear) / axialTiltDeg;
            north = 1.0 + WindStrengthBeta * (-ratio);
            south = 1.0 + WindStrengthBeta * ratio;
        }

        /// <summary>高斯响应。</summary>
        private static double Gaussian(double x, double center, double sigma)
        {
            double t = (x - center) / sigma;
            return Math.Exp(-t * t);
        }

        /// <summary>
        /// 某一季节、某一纬度的水汽供给（0-1）：
        /// ITCZ 与西风带、极锋为上升湿润区，副热带高压与极地高压为下沉干燥区，
        /// 各带按当季中心纬度做高斯叠加，并按半球强度因子加权。
        /// </summary>
        public static double SeasonalMoistureSource(double latDeg, double axialTiltDeg, double dayOfYear)
        {
            WindBandCenters(axialTiltDeg, dayOfYear,
                out double subN, out double westN, out double polN,
                out double subS, out double westS, out double polS);
            WindStrength(axialTiltDeg, dayOfYear, out double strengthN, out double strengthS);
            double phiT = ThermalEquatorLatitude(axialTiltDeg, dayOfYear);

            // 上升湿润带
            double wet = Gaussian(latDeg, phiT, 8.0) * 0.95
                       + Gaussian(latDeg, westN, 10.0) * 0.55 * strengthN
                       + Gaussian(latDeg, westS, 10.0) * 0.55 * strengthS
                       + Gaussian(latDeg, polN, 9.0) * 0.26
                       + Gaussian(latDeg, polS, 9.0) * 0.26;

            // 下沉干燥带
            double dry = Gaussian(latDeg, subN, 9.0) * 0.62
                       + Gaussian(latDeg, subS, 9.0) * 0.62
                       + Gaussian(latDeg, polN, 14.0) * 0.20
                       + Gaussian(latDeg, polS, 14.0) * 0.20;

            double v = 0.12 + wet - dry;
            return v < 0.04 ? 0.04 : (v > 1.0 ? 1.0 : v);
        }

        /// <summary>
        /// 全年平均的水汽供给剖面（按地图行）。
        /// 倾角越大，各风带一年中摆得越远，平均剖面里的湿/干带越宽
        /// ——这正是地轴倾角对气候带宽度的影响。
        /// </summary>
        public static float[] AnnualMoistureProfile(int height, double axialTiltDeg, int samples = 24)
        {
            var profile = new float[height];
            if (samples < 4)
                samples = 4;

            for (int y = 0; y < height; y++)
            {
                double latDeg = 90.0 - (double)y / (height - 1) * 180.0;
                double sum = 0.0;
                for (int s = 0; s < samples; s++)
                    sum += SeasonalMoistureSource(latDeg, axialTiltDeg, TropicalYear * s / samples);
                profile[y] = (float)(sum / samples);
            }

            return profile;
        }

        /// <summary>某纬度的全年平均水汽供给（不建表，用于零散查询）。</summary>
        public static double AnnualMoistureAt(double latDeg, double axialTiltDeg, int samples = 16)
        {
            double sum = 0.0;
            for (int s = 0; s < samples; s++)
                sum += SeasonalMoistureSource(latDeg, axialTiltDeg, TropicalYear * s / samples);
            return sum / samples;
        }

        /// <summary>
        /// 海面可蒸发水汽（0-1）：赤道辐合带最湿润、副热带干旱带最干、
        /// 西风带较湿、极地干燥。默认按地球倾角取全年平均。
        /// </summary>
        public static double SeaSourceMoisture(double absLat, double axialTiltDeg = 23.44)
        {
            return AnnualMoistureAt(absLat, axialTiltDeg);
        }

        /// <summary>
        /// 盛行风带的风向（单位向量；x 向东为正，y 向南为正，与图像行方向一致）：
        /// 信风（0-30°）吹向赤道、西风带（30-60°）吹向极地、极地东风（60-90°）吹向极地锋。
        /// 三条带之间用平滑过渡（sigmoid）衔接，而不是在 30°/60° 硬切换 ——
        /// 否则风带边界上会出现一条「风向突变」的接缝。
        /// </summary>
        public static void WindAt(double latDeg, out double dx, out double dy)
        {
            double absLat = Math.Abs(latDeg);
            double sign = latDeg >= 0.0 ? 1.0 : -1.0;

            double toWesterly = Sigmoid((absLat - 30.0) * 0.45);   // 0 = 信风，1 = 西风
            double toPolar = Sigmoid((absLat - 60.0) * 0.45);      // 0 = 西风，1 = 极地东风

            double dxTrade = -1.0, dxWest = 1.0, dxPolar = -1.0;
            double dyTrade = 0.35, dyWest = -0.45, dyPolar = 0.40;

            double dxMid = dxTrade + (dxWest - dxTrade) * toWesterly;
            double dyMid = dyTrade + (dyWest - dyTrade) * toWesterly;

            dx = dxMid + (dxPolar - dxMid) * toPolar;
            dy = (dyMid + (dyPolar - dyMid) * toPolar) * sign;
        }

        private static double Sigmoid(double t)
        {
            if (t > 12.0) return 1.0;
            if (t < -12.0) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-t));
        }

        /// <summary>
        /// 计算整张地图的湿度场（0-1）：
        /// 海面按「风带经验公式」的全年平均水汽供给（与地轴倾角有关）蒸发，
        /// 随后按盛行风带向下风方向输送 —— 内陆逐渐变干（大陆性），
        /// 遇到山坡则迎风坡降水、背风坡形成雨影。
        /// </summary>
        /// <param name="axialTilt">地轴倾角（度）：决定各风带一年中摆动的幅度，从而决定气候带宽度</param>
        public static float[] ComputeMoistureField(int width, int height, float[] heightField,
            SeededNoise noise, double axialTilt = 23.44, Action<float> onProgress = null)
        {
            int w = width, h = height, n = w * h;
            float[] field = new float[n];

            // 风带中心随季节迁移 → 全年平均后得到「气候带宽度」，倾角越大带越宽
            onProgress?.Invoke(0.02f);
            float[] annualProfile = AnnualMoistureProfile(h, axialTilt);

            // 预置：海面与陆地都先取该纬度的全年平均水汽，保证任何下风采样都有效
            double[] rowCosLat = new double[h];
            double[] rowSinLat = new double[h];
            for (int y = 0; y < h; y++)
            {
                double lat = Math.PI * 0.5 - (double)y / (h - 1) * Math.PI;
                rowCosLat[y] = Math.Cos(lat);
                rowSinLat[y] = Math.Sin(lat);
            }

            // 逐行填充初值（每行只写自己那一行）；后面的带内推进仍是串行的，
            // 因为下风行必须读到上风行的结果
            Parallelism.ForRows(h, y =>
            {
                float src = (float)annualProfile[y];
                int row = y * w;
                for (int x = 0; x < w; x++)
                    field[row + x] = src;
            });

            double[] colCosLon = new double[w];
            double[] colSinLon = new double[w];
            for (int x = 0; x < w; x++)
            {
                double lon = (double)x / (w - 1) * 2.0 * Math.PI - Math.PI;
                colCosLon[x] = Math.Cos(lon);
                colSinLon[x] = Math.Sin(lon);
            }

            // 三个环流带 × 南北半球，各自按自己的风向决定行进顺序
            double[] bandEdges = { 0.0, 30.0, 60.0, 90.0 };
            int done = 0;
            for (int b = 0; b < 3; b++)
            {
                for (int hemi = 0; hemi < 2; hemi++)
                {
                    double sign = hemi == 0 ? 1.0 : -1.0;
                    double latLo = bandEdges[b];
                    double latHi = bandEdges[b + 1];

                    int y0, y1;
                    if (hemi == 0)
                    {
                        y0 = (int)Math.Floor((90.0 - latHi) / 180.0 * (h - 1));
                        y1 = (int)Math.Ceiling((90.0 - latLo) / 180.0 * (h - 1)) + 1;
                    }
                    else
                    {
                        y0 = (int)Math.Floor((90.0 + latLo) / 180.0 * (h - 1));
                        y1 = (int)Math.Ceiling((90.0 + latHi) / 180.0 * (h - 1)) + 1;
                    }
                    if (y0 < 0) y0 = 0;
                    if (y1 > h) y1 = h;
                    if (y1 <= y0)
                        continue;

                    // 取该带中点的风向
                    WindAt(sign * (latLo + latHi) * 0.5, out double dx, out double dy);
                    MarchMoistureBand(w, h, heightField, field, y0, y1, dx, dy,
                        noise, rowCosLat, rowSinLat, colCosLon, colSinLon);

                    done++;
                    onProgress?.Invoke(done / 6f);
                }
            }

            // 极点是一整行同一个点：把极圈行取平均，避免出现横向条纹
            AveragePoleRows(w, h, field);

            return field;
        }

        /// <summary>双线性采样（x 环绕、y 钳制），用于上风点插值。</summary>
        private static double SampleField(float[] field, int w, int h, double sx, double sy)
        {
            int x0 = (int)Math.Floor(sx);
            int y0 = (int)Math.Floor(sy);
            double fx = sx - x0;
            double fy = sy - y0;

            int xA = ((x0 % w) + w) % w;
            int xB = (((x0 + 1) % w) + w) % w;
            int yA = y0 < 0 ? 0 : (y0 >= h ? h - 1 : y0);
            int yB = y0 + 1 < 0 ? 0 : (y0 + 1 >= h ? h - 1 : y0 + 1);

            double v00 = field[yA * w + xA];
            double v10 = field[yA * w + xB];
            double v01 = field[yB * w + xA];
            double v11 = field[yB * w + xB];

            return v00 * (1.0 - fx) * (1.0 - fy) + v10 * fx * (1.0 - fy)
                 + v01 * (1.0 - fx) * fy + v11 * fx * fy;
        }

        /// <summary>极点行（纬度 ±90°）在球面上是同一个点，取经向平均以消除条纹接缝。</summary>
        private static void AveragePoleRows(int w, int h, float[] field)
        {
            if (h < 2)
                return;

            double north = 0.0, south = 0.0;
            for (int x = 0; x < w; x++)
            {
                north += field[x];
                south += field[(h - 1) * w + x];
            }
            north /= w;
            south /= w;
            for (int x = 0; x < w; x++)
            {
                field[x] = (float)north;
                field[(h - 1) * w + x] = (float)south;
            }
        }

        /// <summary>
        /// 在一条环流带内按风向顺序推进水汽。
        /// 风向逐行取值（风带之间是平滑过渡），行序由该带中点的经向分量决定。
        /// </summary>
        private static void MarchMoistureBand(int w, int h, float[] heightField, float[] field,
            int yStart, int yEnd, double dx, double dy, SeededNoise noise,
            double[] rowCosLat, double[] rowSinLat, double[] colCosLon, double[] colSinLon)
        {
            // 行序：让「上风侧」的行先被处理（经向分量在带内基本同向）
            bool yForward = dy >= 0.0;

            int yFrom = yForward ? yStart : yEnd - 1;
            int yTo = yForward ? yEnd : yStart - 1;
            int yStep = yForward ? 1 : -1;

            for (int y = yFrom; y != yTo; y += yStep)
            {
                // 逐行取风向：风带之间平滑过渡，避免边界处出现方向突变的接缝
                double latDeg = 90.0 - (double)y / (h - 1) * 180.0;
                WindAt(latDeg, out double rowDx, out double rowDy);

                double stepLen = Math.Sqrt(rowDx * rowDx + rowDy * rowDy);
                if (stepLen < 1e-6)
                {
                    rowDx = 1.0;
                    rowDy = 0.0;
                    stepLen = 1.0;
                }

                // 球面几何：等距圆柱投影下一格的实际东西向距离随纬度按 cos(lat) 收缩，经向基本不变。
                // 按公里计算衰减，才不会把高纬的大陆性严重低估
                // （原来每一格都衰减同样多，等于把极区当成赤道尺度的大陆）。
                double cosLat = Math.Cos(latDeg * Math.PI / 180.0);
                if (cosLat < 0.02)
                    cosLat = 0.02;

                double cellKmX = EarthCircumferenceKm * cosLat / w;
                double cellKmY = EarthCircumferenceKm * 0.5 / h;
                double stepKm = Math.Sqrt(rowDx * rowDx * cellKmX * cellKmX
                                        + rowDy * rowDy * cellKmY * cellKmY);
                double inlandRetention = Math.Exp(-stepKm / ContinentalScaleKm);

                bool xForward = rowDx >= 0.0;
                int xFrom = xForward ? 0 : w - 1;
                int xStep = xForward ? 1 : -1;

                // 多走 WrapExtraCells 格：越过本行末端后回到行首继续推进，
                // 把「经度接缝」（也就是 0°/180° 那条线）上的突变抹平
                int total = w + WrapExtraCells;
                for (int t = 0; t < total; t++)
                {
                    int x = ((xFrom + xStep * t) % w + w) % w;
                    int i = y * w + x;
                    if (heightField[i] <= 0f)
                        continue;                        // 海面保持纬向蒸发值

                    // 上风点用双线性采样（x 环绕、y 钳制）：
                    // 若把经向位移取整，|dy|<0.5 时上风点会永远落回本行，
                    // 各纬行就退化成互不相干的 1D 推进，行与行之间出现硬接缝。
                    double sx = x - rowDx;
                    double sy = y - rowDy;

                    double m = SampleField(field, w, h, sx, sy) * inlandRetention;

                    // 迎风坡降水：抬升越猛，水汽被拦下越多；背风坡因此干旱（雨影）
                    float dh = heightField[i] - (float)SampleField(heightField, w, h, sx, sy);
                    if (dh > 0f)
                    {
                        double rainout = dh / OrographicScale;
                        if (rainout > 0.85) rainout = 0.85;
                        m *= 1.0 - rainout;
                    }

                    var pos = new Vector3D(rowCosLat[y] * colCosLon[x], rowSinLat[y],
                                           rowCosLat[y] * colSinLon[x]);
                    double variation =
                        (noise.Noise3D(pos.x * 4.0, pos.y * 4.0, pos.z * 4.0) - 0.5) * 0.18;

                    double v = m + variation;
                    field[i] = (float)(v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v));
                }
            }
        }

        /// <summary>湿度低于此值的地区，河流为季节性（间歇性）河流。</summary>
        public const double AridMoisture = 0.32;

        /// <summary>半干旱上限：集水区湿度低于此值时才可能成为季节性河流。</summary>
        public const double SemiAridMoisture = 0.42;

        /// <summary>湿度低于此值的地区，河流沿途强烈渗漏蒸发、可能中途消失。</summary>
        public const double VeryAridMoisture = 0.22;

        /// <summary>
        /// BFS 计算每格到海洋的距离（格数，陆地为正）。x 方向环绕、y 方向钳制。
        /// </summary>
        public static int[] SeaDistance(int width, int height, float[] heightField)
        {
            int w = width, h = height;
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

                for (int k = 0; k < 8; k++)
                {
                    int ny = y + DY8[k];
                    if (ny < 0 || ny >= h)
                        continue;

                    int nIdx = ny * w + ((x + DX8[k] + w) % w);
                    if (dist[nIdx] < 0)
                    {
                        dist[nIdx] = d + 1;
                        queue.Enqueue(nIdx);
                    }
                }
            }

            for (int i = 0; i < dist.Length; i++)
                if (dist[i] < 0)
                    dist[i] = 9999;

            return dist;
        }

        /// <summary>
        /// 湿度（0-1）：由风带经验公式给出的纬向水汽、距海距离（大陆性）与噪声共同决定。
        /// 这是不预先计算整场时的零散回退实现；批量生成请用 <see cref="ComputeMoistureField"/>。
        /// </summary>
        /// <param name="axialTilt">地轴倾角（度），决定气候带宽度</param>
        public static double Moisture(double absLat, int seaDist, SeededNoise noise, Vector3D pos,
            double axialTilt = 23.44)
        {
            // 纬向基础湿度：风带中心随季节迁移，取全年平均
            return MoistureFromBase(SeaSourceMoisture(absLat, axialTilt), seaDist, noise, pos);
        }

        /// <summary>
        /// 由「已经算好的纬向基础湿度」推湿度：只叠加大陆性与噪声。
        /// 批量生成时按纬度预计算 base，可以避免逐格重复求解风带经验公式
        /// （那里面有全年采样，逐格调用会让整张图慢好几秒）。
        /// </summary>
        public static double MoistureFromBase(double baseMoisture, int seaDist,
            SeededNoise noise, Vector3D pos)
        {
            // 大陆性：距海越远越干燥
            double continentality = Math.Exp(-seaDist / 70.0);

            // 区域噪声变化
            double noiseFactor = (noise.Noise3D(pos.x * 4.0, pos.y * 4.0, pos.z * 4.0) - 0.5) * 0.30;

            double moisture = baseMoisture * (0.25 + 0.75 * continentality) + noiseFactor;
            return moisture < 0.0 ? 0.0 : (moisture > 1.0 ? 1.0 : moisture);
        }
    }
}
