using System;
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
    /// 两条输出各走各的行序，互不迁就：
    ///   - **贴图**：第 0 行必须是北极 —— 着色器里 <c>uv.y = 0</c> 在北极，而 uv.y=0 就是贴图第 0 行
    ///   - **PNG 文件**：第一行必须是图像顶部（北极）—— 给人看、给别的软件读
    ///
    /// 旧实现走 <c>EncodeToPNG</c>，只能跟着贴图行序走，写出的文件必然南北颠倒。
    /// 现在 PNG 由 <see cref="PngStreamWriter"/> 按渲染顺序直接写扫描线，
    /// 「上北下南」与「着色器正确」可以同时成立，还省掉一张编码专用贴图。
    ///
    /// 内存：只保留一条带（默认 256 行，8192 档 8 MB）+ 压缩后的 IDAT。
    /// </summary>
    public static class MapExporter
    {
        /// <summary>每次渲染的行数。</summary>
        public const int StripRows = 256;

        /// <summary>条带缓冲（复用，避免每条带重新分配）。</summary>
        private static Color32[] _strip;
        private static byte[] _rowBytes;

        private static Color32[] GetStrip(int width, int rows)
        {
            int need = width * rows;
            if (_strip == null || _strip.Length < need)
                _strip = new Color32[need];
            return _strip;
        }

        private static byte[] GetRowBytes(int width)
        {
            int need = width * 4;
            if (_rowBytes == null || _rowBytes.Length < need)
                _rowBytes = new byte[need];
            return _rowBytes;
        }

        /// <summary>
        /// 一次性产出**贴图 + PNG 文件**（生成流程用这个）：
        /// 同一条带既写进贴图（第 0 行 = 北极），又写进 PNG（第一行 = 北极）。
        /// </summary>
        /// <param name="pngPath">为空时只建贴图，不写文件。</param>
        public static Texture2D ExportLayer(TerrainContext ctx, MapLayer layer, string pngPath)
        {
            if (ctx == null)
                return null;

            int w = ctx.Width, h = ctx.Height;
            if (w <= 0 || h <= 0)
                return null;

            bool linear = layer == MapLayer.Height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);

            float reliefScale = layer == MapLayer.Terrain
                ? TerrainMapRenderer.ComputeReliefScale(w, h, ctx.HeightField)
                : 1f;

            bool writePng = !string.IsNullOrEmpty(pngPath);
            PngStreamWriter writer = writePng ? new PngStreamWriter(w, h) : null;
            byte[] rowBytes = writePng ? GetRowBytes(w) : null;

            int rows = Math.Min(StripRows, h);
            Color32[] strip = GetStrip(w, rows);

            try
            {
                for (int y0 = 0; y0 < h; y0 += rows)
                {
                    int count = Math.Min(rows, h - y0);
                    RenderRows(ctx, layer, reliefScale, y0, y0 + count, strip);

                    // 贴图：图像行 y ↔ 贴图行 y（北极在第 0 行，给着色器用）
                    tex.SetPixels32(0, y0, w, count, strip);

                    // PNG：按渲染顺序自北向南逐行写
                    if (writer != null)
                        WriteStripRowsPng(writer, strip, w, count, rowBytes);
                }

                if (writer != null && writer.Finish(pngPath) < 0)
                    Debug.LogError($"MapExporter: PNG 行数不匹配，未写文件 {pngPath}");
            }
            finally
            {
                writer?.Dispose();
            }

            tex.Apply(false, true);   // 释放 CPU 侧像素（8192 档每张 128 MB）
            return tex;
        }

        /// <summary>只写 PNG、不建贴图（验证台 / 探针用）。</summary>
        public static bool WritePng(TerrainContext ctx, MapLayer layer, string path)
        {
            if (ctx == null || string.IsNullOrEmpty(path))
                return false;

            int w = ctx.Width, h = ctx.Height;
            float reliefScale = layer == MapLayer.Terrain
                ? TerrainMapRenderer.ComputeReliefScale(w, h, ctx.HeightField)
                : 1f;

            int rows = Math.Min(StripRows, h);
            Color32[] strip = GetStrip(w, rows);
            byte[] rowBytes = GetRowBytes(w);
            var writer = new PngStreamWriter(w, h);

            try
            {
                for (int y0 = 0; y0 < h; y0 += rows)
                {
                    int count = Math.Min(rows, h - y0);
                    RenderRows(ctx, layer, reliefScale, y0, y0 + count, strip);
                    WriteStripRowsPng(writer, strip, w, count, rowBytes);
                }

                return writer.Finish(path) > 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"MapExporter: 写 PNG 失败 {path}：{e.Message}");
                return false;
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>把条带里的若干行按 RGBA 写进 PNG（行序保持不变）。</summary>
        private static void WriteStripRowsPng(PngStreamWriter writer, Color32[] strip,
            int width, int count, byte[] rowBytes)
        {
            for (int r = 0; r < count; r++)
            {
                int src = r * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = strip[src + x];
                    int d = x * 4;
                    rowBytes[d] = c.r;
                    rowBytes[d + 1] = c.g;
                    rowBytes[d + 2] = c.b;
                    rowBytes[d + 3] = c.a;
                }
                writer.WriteRow(rowBytes);
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
