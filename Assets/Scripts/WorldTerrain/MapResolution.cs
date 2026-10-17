using System;

namespace WorldTerrain
{
    /// <summary>
    /// 生成分辨率档位。
    ///
    /// 约定：**对外只说宽度**（1024 / 2048 / 4096 / 8192），高度恒为宽度的一半
    /// （等距圆柱图 2:1）。所以「2048」= 2048×1024。
    ///
    /// 档位是离散的（每档翻倍），不是任意值 —— 球面网格按 4 的幂细分，
    /// 只有翻倍才能让「球面格点 / 像素」的粒度比保持稳定。
    /// </summary>
    public static class MapResolution
    {
        /// <summary>可选宽度（像素），从小到大。</summary>
        public static readonly int[] Widths = { 1024, 2048, 4096, 8192 };

        /// <summary>默认宽度：2048×1024。</summary>
        public const int DefaultWidth = 2048;

        /// <summary>宽度最低档。</summary>
        public const int MinWidth = 1024;

        /// <summary>宽度最高档。</summary>
        public const int MaxWidth = 8192;

        /// <summary>高度恒为宽度的一半（等距圆柱 2:1）。</summary>
        public static int HeightFor(int width) => Math.Max(1, width / 2);

        /// <summary>默认宽度在 <see cref="Widths"/> 里的下标。</summary>
        public static int DefaultIndex => IndexOf(DefaultWidth);

        /// <summary>把任意宽度吸附到最近的档位（越界时取最近端点）。</summary>
        public static int Snap(int width)
        {
            int best = Widths[0];
            int bestDelta = int.MaxValue;

            for (int i = 0; i < Widths.Length; i++)
            {
                int delta = Math.Abs(Widths[i] - width);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = Widths[i];
                }
            }

            return best;
        }

        /// <summary>宽度对应的档位下标（会先吸附）。</summary>
        public static int IndexOf(int width)
        {
            int snapped = Snap(width);
            for (int i = 0; i < Widths.Length; i++)
                if (Widths[i] == snapped)
                    return i;
            return 0;
        }

        /// <summary>下标对应的宽度（越界时夹到端点）。</summary>
        public static int WidthAt(int index)
        {
            if (index < 0) index = 0;
            if (index >= Widths.Length) index = Widths.Length - 1;
            return Widths[index];
        }

        /// <summary>单元格总数。</summary>
        public static long CellCount(int width)
        {
            int w = Snap(width);
            return (long)w * HeightFor(w);
        }

        /// <summary>
        /// 粗略估算生成峰值内存（字节）。
        ///
        /// 实测标定（本机 24 逻辑核，含全部 12 个步骤，步骤间 GC 对大图开启）：
        ///   1024 → 62 MB、2048 → 277 MB、4096 → 420 MB、8192 → 1700 MB
        ///
        /// 逐格数组合计约 57 字节/格（高度/湿度/径流/板块/分类各 4，水文各掩膜 25，三张 Color32 各 4），
        /// 再加固定底噪 80 MB。**偏保守**：只用于判断要不要提醒用户，宁可高估。
        ///
        /// 注：三张彩色图从 <c>Color[]</c>（16 B/px）换成 <c>Color32[]</c>（4 B/px）后，
        /// 8192 档的峰值从 4.3 GB 降到 1.8 GB，所以这个估算也跟着重标定过。
        /// </summary>
        public static long EstimatedPeakBytes(int width)
        {
            long cells = CellCount(width);
            const int PerCellBytes = 57;
            const long FixedOverhead = 128L * 1024 * 1024;
            return cells * PerCellBytes + FixedOverhead;
        }

        /// <summary>把估算内存格式化成便于显示的文字。</summary>
        public static string EstimateLabel(int width)
        {
            double gb = EstimatedPeakBytes(width) / (1024.0 * 1024.0 * 1024.0);
            return gb >= 1.0 ? $"约 {gb:F1} GB" : $"约 {gb * 1024:F0} MB";
        }

        /// <summary>
        /// 该档位是否可能超出本机可用内存。用于在 UI 上给出警示（不阻止生成）。
        /// </summary>
        public static bool MayExceedMemory(int width, int systemMemoryMb)
        {
            if (systemMemoryMb <= 0)
                return false;

            long available = (long)systemMemoryMb * 1024 * 1024;
            // 给系统与 Unity 自身留一半
            return EstimatedPeakBytes(width) > available / 2;
        }

        /// <summary>显示用标签，例如 “2048×1024”。</summary>
        public static string Label(int width)
        {
            int w = Snap(width);
            return $"{w}×{HeightFor(w)}";
        }
    }
}
