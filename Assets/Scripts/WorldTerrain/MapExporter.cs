using System;
using System.IO;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>可导出的图层。</summary>
    public enum MapLayer
    {
        Terrain,
        Height,
        Plates
    }

    /// <summary>
    /// 生成结果 → 贴图 / PNG 的**流式导出器**。
    ///
    /// 设计要点（为什么不再一次性生成整幅 <c>Color32[]</c>）：
    ///   - 8192×4096 时整幅 Color32 是 128 MB，三张就是 384 MB —— 与生成峰值叠加
    ///   - 同时还要再拷进 Texture2D、再编码成 PNG，等于同一份像素存在 3 份
    ///
    /// 这里按**纬度条带**（默认 256 行）渲染：一条带的缓冲只有
    /// <c>256 × 8192 × 4 B = 8 MB</c>，逐带 <c>SetPixels32</c> 进贴图，
    /// 贴图编码后可选地 <c>Apply(false, true)</c> 释放 CPU 侧副本。
    /// </summary>
    public static class MapExporter
    {
        /// <summary>每次渲染的行数。</summary>
        public const int StripRows = 256;

        private static Color32[] _strip;

        /// <summary>取（必要时分配）条带缓冲。导出是串行的，复用一块即可。</summary>
        private static Color32[] GetStrip(int width, int rows)
        {
            int need = width * rows;
            // 只增不减：连续多次导出时避免反复分配大数组
            if (_strip == null || _strip.Length < need)
                _strip = new Color32[need];
            return _strip;
        }

        /// <summary>
        /// 把某一层渲染成一张可显示的贴图。
        /// 渲染完不保留 CPU 侧像素（<c>Apply(false, true)</c>）：8192 档每张省 128 MB。
        /// 需要编码 PNG 时请改用 <see cref="EncodePng"/>（它先编码再释放）。
        /// </summary>
        public static Texture2D RenderTexture(TerrainContext ctx, MapLayer layer)
        {
            Texture2D tex = RenderTextureReadable(ctx, layer);
            if (tex != null)
                tex.Apply(false, true);
            return tex;
        }

        /// <summary>渲染成贴图但**保留** CPU 侧像素（可继续 EncodeToPNG）。</summary>
        public static Texture2D RenderTextureReadable(TerrainContext ctx, MapLayer layer)
        {
            if (ctx == null)
                return null;

            int w = ctx.Width;
            int h = ctx.Height;
            if (w <= 0 || h <= 0)
                return null;

            bool linear = layer == MapLayer.Height;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);

            float reliefScale = layer == MapLayer.Terrain
                ? TerrainMapRenderer.ComputeReliefScale(w, h, ctx.HeightField)
                : 1f;

            int rows = Math.Min(StripRows, h);
            Color32[] strip = GetStrip(w, rows);

            for (int y0 = 0; y0 < h; y0 += rows)
            {
                int count = Math.Min(rows, h - y0);
                RenderRows(ctx, layer, reliefScale, y0, y0 + count, strip);

                // 行映射：图像行 y ↔ 贴图行 y（两者都是「第 0 行 = 北极」）。
                //
                // 这不是可随意选的约定，而是被着色器钉死的：
                //   EarthSurfaceLitShader 里 uv.y = 1 - (lat/π + 0.5)，北极(+90°) → uv.y = 0，
                //   而 Unity 的 uv.y = 0 是贴图第 0 行 —— 所以北极必须在第 0 行。
                // EncodeToPNG / 导入时 Unity 会各翻一次，写出的 PNG 虽是「南极朝上」，
                // 但读回来（DecodeTexture）又翻正，往返一致。
                //
                // 曾经写成 h - y0 - count 并逐条带翻转，结果是每条带就地镜像 ——
                // 表现为贴图/星球上每 256 行一道硬缝（2.5.0–2.7.0 的回归）。
                tex.SetPixels32(0, y0, w, count, strip);
            }

            tex.Apply(false, false);
            return tex;
        }

        /// <summary>把某一层渲染并按 PNG 写盘。返回是否成功。</summary>
        public static bool WritePng(TerrainContext ctx, MapLayer layer, string path)
        {
            Texture2D tex = RenderTextureReadable(ctx, layer);
            if (tex == null)
                return false;

            try
            {
                return WriteTexturePng(tex, path);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>把贴图编码成 PNG 写盘。调用后贴图的 CPU 像素被释放。</summary>
        public static bool WriteTexturePng(Texture2D tex, string path)
        {
            if (tex == null || string.IsNullOrEmpty(path))
                return false;

            try
            {
                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(path, png);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"MapExporter: 写 PNG 失败 {path}：{e.Message}");
                return false;
            }
        }

        /// <summary>按行带渲染一层到 <paramref name="dest"/>（从 <paramref name="destOffset"/> 起）。</summary>
        public static void RenderRows(TerrainContext ctx, MapLayer layer, float reliefScale,
            int y0, int y1, Color32[] dest, int destOffset = 0)
        {
            switch (layer)
            {
                case MapLayer.Height:
                    TerrainMapRenderer.RenderHeightRows(
                        ctx.Width, ctx.Height, ctx.HeightField, y0, y1, dest, destOffset);
                    break;

                case MapLayer.Plates:
                    TerrainMapRenderer.RenderPlateRows(
                        ctx.Width, ctx.Height, ctx.PlateIds, true, y0, y1, dest, destOffset);
                    break;

                default:
                    TerrainMapRenderer.RenderTerrainRows(
                        ctx.Width, ctx.Height, ctx.HeightField, ctx.TerrainTypes,
                        ctx.Hydrology, ctx.Noise, reliefScale, y0, y1, dest, destOffset);
                    break;
            }
        }
    }

    /// <summary>生成时「写哪些文件」的开关。地形图始终写（否则地图列表里加载不回来）。</summary>
    [Serializable]
    public struct MapExportOptions
    {
        /// <summary>写灰度高度图（地形凹凸用）。</summary>
        public bool height;

        /// <summary>写板块分界叠加图。</summary>
        public bool plates;

        /// <summary>写人文 JSON（聚落 / 国家 / 交通 / 地名）。</summary>
        public bool human;

        public static MapExportOptions All => new MapExportOptions
        {
            height = true,
            plates = true,
            human = true
        };
    }
}
