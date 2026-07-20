using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// Generates rivers and lakes from the height field.
    /// Rivers start from high-elevation local maxima and flow downhill
    /// to the sea. When a river reaches a depression, a lake forms.
    /// </summary>
    public static class HydrologyGenerator
    {
        // 8-neighbour offsets (x wraps, y clamps)
        private static readonly int[] DX8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] DY8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        /// <summary>
        /// Generate rivers and lakes from the height field.
        /// </summary>
        public static HydrologyData Generate(int width, int height, float[] heightField)
        {
            int w = width, h = height;
            bool[] riverMask = new bool[w * h];
            bool[] lakeMask = new bool[w * h];
            float[] riverWidth = new float[w * h];
            bool[] visited = new bool[w * h];
            bool[] estuaryMask = new bool[w * h];

            List<int> sources = FindRiverSources(w, h, heightField);

            // Sort by elevation descending (highest sources first)
            sources.Sort((a, b) => heightField[b].CompareTo(heightField[a]));

            int maxRivers = Math.Min(sources.Count, 170);
            for (int i = 0; i < maxRivers; i++)
            {
                TraceRiver(sources[i], w, h, heightField,
                           riverMask, lakeMask, riverWidth, visited, estuaryMask);
            }

            return new HydrologyData
            {
                riverMask = riverMask,
                lakeMask = lakeMask,
                riverWidth = riverWidth,
                estuaryMask = estuaryMask
            };
        }

        // ── River source detection ──

        /// <summary>
        /// Find river sources using a grid-based approach.
        /// Divides the map into a grid and finds the highest land cell
        /// in each cell that is also a local maximum.
        /// </summary>
        private static List<int> FindRiverSources(int w, int h, float[] heightField)
        {
            var sources = new List<int>();

            int gridW = 48;
            int gridH = 24;
            int cellW = w / gridW;
            int cellH = h / gridH;

            for (int gy = 0; gy < gridH; gy++)
            {
                for (int gx = 0; gx < gridW; gx++)
                {
                    int bestIdx = -1;
                    float bestH = 500f; // Minimum source elevation

                    int yStart = gy * cellH;
                    int yEnd = Math.Min((gy + 1) * cellH, h);
                    int xStart = gx * cellW;
                    int xEnd = Math.Min((gx + 1) * cellW, w);

                    for (int y = yStart; y < yEnd; y++)
                    {
                        // Skip polar regions (glaciers)
                        double lat = 90.0 - (double)y / (h - 1) * 180.0;
                        if (Math.Abs(lat) > 65.0)
                            continue;

                        for (int x = xStart; x < xEnd; x++)
                        {
                            int idx = y * w + x;
                            if (heightField[idx] > bestH)
                            {
                                // Check if it's a local maximum
                                bool isMax = true;
                                for (int d = 0; d < 8; d++)
                                {
                                    int nx = (x + DX8[d] + w) % w;
                                    int ny = y + DY8[d];
                                    if (ny < 0 || ny >= h)
                                        continue;
                                    if (heightField[ny * w + nx] > heightField[idx])
                                    {
                                        isMax = false;
                                        break;
                                    }
                                }

                                if (isMax)
                                {
                                    bestH = heightField[idx];
                                    bestIdx = idx;
                                }
                            }
                        }
                    }

                    if (bestIdx >= 0)
                        sources.Add(bestIdx);
                }
            }

            return sources;
        }

        // ── River tracing ──

        /// <summary>
        /// Trace a river from a source downhill to the sea.
        /// Rivers can merge into existing rivers (tributary confluence).
        /// Width grows along the path and at confluences.
        /// Marks estuary pixels at river mouths.
        /// </summary>
        private static void TraceRiver(
            int sourceIdx, int w, int h, float[] heightField,
            bool[] riverMask, bool[] lakeMask, float[] riverWidth,
            bool[] visited, bool[] estuaryMask)
        {
            // Skip if source already consumed by another river
            if (visited[sourceIdx])
                return;

            int current = sourceIdx;
            float currentWidth = 1.0f;
            int maxSteps = 4 * (w + h);

            for (int step = 0; step < maxSteps; step++)
            {
                int cy = current / w;
                double lat = 90.0 - (double)cy / (h - 1) * 180.0;

                // Reached the sea → mark estuary
                if (heightField[current] <= 0f)
                {
                    MarkEstuary(current, w, h, currentWidth, estuaryMask, riverWidth);
                    break;
                }

                // Polar region stop
                if (Math.Abs(lat) > 65.0)
                    break;

                // Hit existing river → add width at confluence and stop
                // (main river already flows to sea; no need to re-trace)
                if (riverMask[current] && step > 0)
                {
                    riverWidth[current] += currentWidth * 0.5f;
                    break;
                }

                riverMask[current] = true;
                visited[current] = true;
                riverWidth[current] = Math.Max(riverWidth[current], currentWidth);

                // Find lowest neighbour
                int cx = current % w;
                int lowestIdx = -1;
                float lowestH = float.MaxValue;

                for (int d = 0; d < 8; d++)
                {
                    int nx = (cx + DX8[d] + w) % w;
                    int ny = cy + DY8[d];
                    if (ny < 0 || ny >= h)
                        continue;

                    int nIdx = ny * w + nx;
                    if (heightField[nIdx] < lowestH)
                    {
                        lowestH = heightField[nIdx];
                        lowestIdx = nIdx;
                    }
                }

                if (lowestIdx < 0)
                    break;

                if (lowestH >= heightField[current])
                {
                    // Local minimum → fill depression (lake)
                    int spillIdx = FillDepression(
                        current, w, h, heightField, lakeMask, visited);

                    if (spillIdx < 0 || spillIdx == current)
                        break;

                    current = spillIdx;
                }
                else
                {
                    // Downhill flow: grow width
                    currentWidth += 0.3f;
                    riverWidth[lowestIdx] = Math.Max(riverWidth[lowestIdx], currentWidth);
                    current = lowestIdx;
                }
            }
        }

        /// <summary>
        /// Mark estuary pixels at a river mouth — a small fan of ocean pixels
        /// around the river-sea boundary, with slightly enlarged width.
        /// </summary>
        private static void MarkEstuary(
            int idx, int w, int h, float width,
            bool[] estuaryMask, float[] riverWidth)
        {
            int cx = idx % w;
            int cy = idx / w;
            int radius = (int)Math.Max(1.0, width * 0.7);

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius)
                        continue;

                    int nx = (cx + dx + w) % w;
                    int ny = cy + dy;
                    if (ny < 0 || ny >= h)
                        continue;

                    int nIdx = ny * w + nx;
                    estuaryMask[nIdx] = true;
                    riverWidth[nIdx] = Math.Max(riverWidth[nIdx], width * 1.3f);
                }
            }
        }

        // ── Depression filling (Priority-Flood) ──

        /// <summary>
        /// Fill a depression starting from a pit using Priority-Flood.
        /// Returns the spill point index where the river should continue,
        /// or -1 if no spill point was found.
        /// </summary>
        private static int FillDepression(
            int pitIdx, int w, int h, float[] heightField,
            bool[] lakeMask, bool[] visited)
        {
            var heap = new MinHeap();
            bool[] closed = new bool[w * h];

            heap.Push(pitIdx, heightField[pitIdx]);
            closed[pitIdx] = true;

            float pitHeight = heightField[pitIdx];
            float waterLevel = pitHeight;
            int spillIdx = -1;
            bool foundSpill = false;
            int maxProcessed = 50000; // Safety limit

            while (heap.Count > 0 && maxProcessed > 0)
            {
                maxProcessed--;
                var entry = heap.Pop();
                int idx = entry.idx;
                float cellH = entry.h;

                // First cell above pit height is the spill point
                if (!foundSpill && cellH > pitHeight)
                {
                    waterLevel = cellH;
                    spillIdx = idx;
                    foundSpill = true;
                }

                // Mark as lake if at or below water level
                if (cellH <= waterLevel)
                {
                    lakeMask[idx] = true;
                    visited[idx] = true;

                    // Add neighbours to explore
                    int x = idx % w;
                    int y = idx / w;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = (x + DX8[d] + w) % w;
                        int ny = y + DY8[d];
                        if (ny < 0 || ny >= h)
                            continue;

                        int nIdx = ny * w + nx;
                        if (!closed[nIdx])
                        {
                            closed[nIdx] = true;
                            heap.Push(nIdx, heightField[nIdx]);
                        }
                    }
                }
                // Cells above water level: don't explore further
            }

            return spillIdx;
        }

        // ── Simple binary min-heap for Priority-Flood ──

        private struct HeapEntry
        {
            public int idx;
            public float h;
        }

        private class MinHeap
        {
            private readonly List<HeapEntry> data = new List<HeapEntry>();

            public int Count => data.Count;

            public void Push(int idx, float h)
            {
                data.Add(new HeapEntry { idx = idx, h = h });
                int i = data.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (data[i].h < data[parent].h)
                    {
                        var tmp = data[i];
                        data[i] = data[parent];
                        data[parent] = tmp;
                        i = parent;
                    }
                    else
                        break;
                }
            }

            public HeapEntry Pop()
            {
                HeapEntry result = data[0];
                int last = data.Count - 1;
                data[0] = data[last];
                data.RemoveAt(last);

                int i = 0;
                while (true)
                {
                    int left = (i << 1) + 1;
                    int right = (i << 1) + 2;
                    int smallest = i;

                    if (left < data.Count && data[left].h < data[smallest].h)
                        smallest = left;
                    if (right < data.Count && data[right].h < data[smallest].h)
                        smallest = right;

                    if (smallest != i)
                    {
                        var tmp = data[i];
                        data[i] = data[smallest];
                        data[smallest] = tmp;
                        i = smallest;
                    }
                    else
                        break;
                }

                return result;
            }
        }
    }
}
