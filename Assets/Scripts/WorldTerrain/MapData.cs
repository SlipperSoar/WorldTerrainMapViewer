using System;

namespace WorldTerrain
{
    /// <summary>
    /// **生成器与查看器之间的数据契约**。
    ///
    /// 一张地图"是什么"只由这个类描述：
    ///   - 三张图（彩色地形图 / 灰度高度图 / 板块分界叠加图）的 PNG 字节
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

        /// <summary>生成时间（ISO 8601 字符串，空表示未知）。</summary>
        public string generatedAt = "";

        // ── 图像内容（PNG 字节，可为 null 表示该图层不存在）──

        /// <summary>彩色地形图 PNG。</summary>
        public byte[] terrainPng;

        /// <summary>灰度高度图 PNG（供地形凹凸位移使用）。</summary>
        public byte[] heightPng;

        /// <summary>板块分界叠加图 PNG（含透明度）。</summary>
        public byte[] plateOverlayPng;

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

        /// <summary>
        /// 校验契约是否可用（查看器至少需要一张彩色地形图）。返回 false 时
        /// <paramref name="reason"/> 说明缺什么。
        /// </summary>
        public bool Validate(out string reason)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                reason = "缺少地图名 baseName";
                return false;
            }

            if (!HasTerrain)
            {
                reason = "缺少彩色地形图 terrainPng";
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
