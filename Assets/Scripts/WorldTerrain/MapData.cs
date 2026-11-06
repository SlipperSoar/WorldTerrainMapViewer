using System;

namespace WorldTerrain
{
    /// <summary>
    /// **生成器与查看器之间的数据契约**。
    ///
    /// 一张地图"是什么"只由这个类描述：
    ///   - 五张图（彩色地形图 / 灰度高度图 / 板块分界叠加图 /
    ///     RGBA 地表数据图 / RGBA 细节图）的 PNG 字节
    ///   - 地图属性（地轴倾角、水面占比、种子、生成时间）
    ///   - 人文数据（聚落 / 国家 / 交通线 / 自然地名），可为空
    ///   - 气候数据（湿度场），从人文数据里取
    ///
    /// 故意**不放**：
    ///   - <c>Texture2D</c> 之类的 Unity 对象 —— 契约保持纯数据，便于离线校验台使用
    ///   - 标记 / 线条等用户标注 —— 它们是浏览期的用户数据，不属于地图内容
    ///   - 落盘格式细节（JSON 结构、文件名规则）—— 那些在 <c>MapStore</c> 里
    ///
    /// 于是「只生成地形不生成人文」只是 <see cref="human"/> 为 null；
    /// 「外部导入符合格式的地图」只需要构造一个 <see cref="MapData"/> 再交给查看器。
    /// </summary>
    public sealed class MapData
    {
        /// <summary>地图名（不含扩展名），也是各配套文件的公共前缀。</summary>
        public string baseName = "";

        // ── 地图属性 ──

        /// <summary>地轴倾角（度）。查看器据此设置季节光照与冰盖。</summary>
        public float axialTilt = 23.5f;

        /// <summary>生成时的水面（海洋）占比，0-1。</summary>
        public float waterCoverage = 0.5f;

        /// <summary>生成时使用的随机种子。</summary>
        public int seed;

        /// <summary>星球半径（km）。查看器据此换算显示尺寸与镜头距离。</summary>
        public float planetRadiusKm = PlanetProfile.ReferenceRadiusKm;

        /// <summary>是否逆向自转。查看器据此决定时间流转时太阳的转动方向。</summary>
        public bool rotationRetrograde;

        // V3.2.0 起所有 PNG 强制上北下南（第一行 = 北极），不再有 pngNorthUp 标记；
        // 载入时无条件翻转一次（Unity LoadImage 行序约定）。

        /// <summary>
        /// 只有配方、没有 PNG（2.9.0 起的常态）。查看器需要按配方重建后才能显示。
        /// V3.2.0 起这也是「列表里的地图」唯一合法的形态。
        /// </summary>
        public bool hasRecipe;

        /// <summary>该地图的整型配方（hasRecipe 为 true 时有效）。</summary>
        public MapRecipe recipe;

        /// <summary>生成时间（ISO 8601 字符串，空表示未知）。</summary>
        public string generatedAt = "";

        // ── 图像内容（PNG 字节，可为 null 表示该图层不存在）──

        /// <summary>彩色地形图 PNG。</summary>
        public byte[] terrainPng;

        /// <summary>灰度高度图 PNG（供地形凹凸位移使用）。</summary>
        public byte[] heightPng;

        /// <summary>板块分界叠加图 PNG（含透明度）。</summary>
        public byte[] plateOverlayPng;

        /// <summary>RGBA 地表数据图 PNG（R=高度/G=水体分类/B=气温/A=湿度）。步骤 1 数据契约。</summary>
        public byte[] surfacePng;

        /// <summary>RGBA 细节图 PNG（R/G=法线/B=AO/A=粗糙度）。步骤 1 数据契约。</summary>
        public byte[] detailPng;

        // ── 渲染参数（3.1 起写进 _meta.json，供着色器读取）──
        // V3.2.0 起这些字段由有效 meta 保证存在；导入的单张 PNG 走另一条路径，
        // 用缺省值（步骤 2 的辉光不依赖它们）。

        /// <summary>海平面（米）。海平面标定后高度场以 0 为海平面，故恒为 0。</summary>
        public float seaLevelMeters;

        /// <summary>赤道 / 中纬 / 极地雪线（米）。</summary>
        public float snowLineEquatorMeters;
        public float snowLineMidMeters;
        public float snowLinePoleMeters;

        /// <summary>潮汐振幅（米）与周期（地球日）。</summary>
        public float tideAmplitudeMeters;
        public float tidePeriodDays;

        // ── 人文 / 气候 ──

        /// <summary>人文数据；null 表示这张地图没有生成人文。</summary>
        public HumanGeographyData human;

        // ── 便捷查询 ──

        /// <summary>是否有彩色地形图。</summary>
        public bool HasTerrain => terrainPng != null && terrainPng.Length > 0;

        /// <summary>是否有高度图（没有则不做地形凹凸）。</summary>
        public bool HasHeight => heightPng != null && heightPng.Length > 0;

        /// <summary>是否有板块分界叠加图。</summary>
        public bool HasPlateOverlay => plateOverlayPng != null && plateOverlayPng.Length > 0;

        /// <summary>是否有 RGBA 地表数据图（步骤 2 的独立水面 pass 依赖它）。</summary>
        public bool HasSurface => surfacePng != null && surfacePng.Length > 0;

        /// <summary>是否有 RGBA 细节图（步骤 2 的法线贴图依赖它）。</summary>
        public bool HasDetail => detailPng != null && detailPng.Length > 0;

        /// <summary>是否有人文数据（聚落等内容）。</summary>
        public bool HasHuman => human != null && human.HasContent;

        /// <summary>是否有云层用的气候（湿度）数据。</summary>
        public bool HasClimate => human != null && human.GetMoistureField() != null;

        /// <summary>地图宽度（像素）。优先用元数据，缺失时返回 0（由贴图决定）。</summary>
        public int width;

        /// <summary>地图高度（像素）。</summary>
        public int height;

        /// <summary>横向像素数 / 竖向像素数（等距圆柱图一般为 2）。</summary>
        public float Aspect => height > 0 ? (float)width / height : 2f;

        /// <summary>把契约里的渲染参数整理成着色器用的结构。</summary>
        public SurfaceRenderParams RenderParams => new SurfaceRenderParams
        {
            seaLevelMeters = seaLevelMeters,
            snowLineEquatorMeters = snowLineEquatorMeters,
            snowLineMidMeters = snowLineMidMeters,
            snowLinePoleMeters = snowLinePoleMeters,
            tideAmplitudeMeters = tideAmplitudeMeters,
            tidePeriodDays = tidePeriodDays
        };

        /// <summary>
        /// 校验契约是否可用。V3.2.0 起合法形态有两种：
        ///   - **有配方**（列表里的常态，只有 _meta.json、没有 PNG）→ 由查看器按配方重建；
        ///   - **有彩色地形图**（导入的单张 PNG，或用户导出的整包）。
        /// 两者都没有就拒绝（这正是不再支持的「只有 PNG、没有配方 meta」老地图）。
        /// </summary>
        public bool Validate(out string reason)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                reason = "缺少地图名 baseName";
                return false;
            }

            if (!hasRecipe && !HasTerrain)
            {
                reason = "既没有配方也没有彩色地形图（V3.2 起不再支持无配方老地图）";
                return false;
            }

            reason = null;
            return true;
        }

        public override string ToString()
        {
            return $"{baseName}（{width}×{height}，倾角 {axialTilt:F1}°，" +
                   $"地形 {(HasTerrain ? "有" : "无")}，高度 {(HasHeight ? "有" : "无")}，" +
                   $"人文 {(HasHuman ? "有" : "无")}，气候 {(HasClimate ? "有" : "无")}）";
        }
    }
}
