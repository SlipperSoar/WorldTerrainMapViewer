using System;
using System.Threading.Tasks;

namespace WorldTerrain
{
    /// <summary>
    /// 循环并行化的统一入口。
    ///
    /// **使用前提**：每次迭代只能写「自己负责的那几个槽位」，只能读不该被别的迭代同时修改的数据。
    /// 满足这个前提时，并行与串行的结果**逐位相同** —— 因为每个槽位的算术完全没变，
    /// 只是执行顺序不同，而彼此没有依赖。
    ///
    /// 不适用的情形（这些仍然只能串行）：
    ///   - 雨滴侵蚀：粒子逐个修改同一张高度图，后一滴要看到前一滴的改动
    ///   - 排水/填洼（Priority-Flood）：本质上是顺序算法
    ///   - 气候带推进：上风行必须先算完，下风行才能读
    ///   - 三角网栅格化：多个三角形可能覆盖同一像素，结果取决于写入顺序
    ///
    /// 不支持的平台（WebGL 主线程没有多线程）会自动退回串行，结果一致、只是慢。
    /// 设置环境变量 <c>WORLDTERRAIN_SERIAL=1</c> 可强制串行，用于逐位对比验证。
    /// </summary>
    public static class Parallelism
    {
        /// <summary>低于这个迭代数时并行只有开销，直接串行。</summary>
        public const int MinParallelLength = 1024;

        /// <summary>是否允许并行。环境变量 WORLDTERRAIN_SERIAL=1 时强制关闭。</summary>
        public static bool Enabled { get; set; } = DetectEnabled();

        private static bool DetectEnabled()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL 不支持线程，Parallel.For 会退化甚至抛异常
            return false;
#else
            try
            {
                string env = Environment.GetEnvironmentVariable("WORLDTERRAIN_SERIAL");
                if (!string.IsNullOrEmpty(env) && env != "0")
                    return false;
            }
            catch
            {
                // 某些平台不允许读环境变量，忽略
            }

            return Environment.ProcessorCount > 1;
#endif
        }

        /// <summary>并行执行 <c>body(i)</c>，i ∈ [from, to)。</summary>
        public static void For(int from, int to, Action<int> body)
        {
            if (body == null)
                return;

            int length = to - from;
            if (length <= 0)
                return;

            if (!Enabled || length < MinParallelLength)
            {
                for (int i = from; i < to; i++)
                    body(i);
                return;
            }

            Parallel.For(from, to, body);
        }

        /// <summary>并行执行 <c>body(i)</c>，i ∈ [0, length)。</summary>
        public static void For(int length, Action<int> body) => For(0, length, body);

        /// <summary>
        /// 二维并行：按行切分，<c>body(y)</c> 内自己遍历该行的所有列。
        /// 行是自然的分区单位，缓存局部性比按一维下标切分好。
        /// </summary>
        public static void ForRows(int height, Action<int> body)
        {
            if (body == null || height <= 0)
                return;

            if (!Enabled || height < 8)
            {
                for (int y = 0; y < height; y++)
                    body(y);
                return;
            }

            Parallel.For(0, height, body);
        }
    }
}
