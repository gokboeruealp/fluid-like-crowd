using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    public sealed class SpatialHash : IDisposable
    {
        public readonly NavGrid Grid;

        public NativeArray<int> CellStarts;

        public NativeArray<int> SortedUnits;

        NativeArray<int> m_WorkerCount;

        NativeArray<int> m_CellTotal;
        NativeArray<int> m_UnitCell;

        readonly int m_Workers;
        bool m_Disposed;

        public SpatialHash(NavGrid grid, int capacity, int workers, Allocator allocator)
        {
            Grid = grid;
            m_Workers = math.max(1, workers);

            CellStarts = new NativeArray<int>(grid.CellCount + 1, allocator);
            SortedUnits = new NativeArray<int>(capacity, allocator, NativeArrayOptions.UninitializedMemory);
            m_CellTotal = new NativeArray<int>(grid.CellCount, allocator, NativeArrayOptions.UninitializedMemory);
            m_UnitCell = new NativeArray<int>(capacity, allocator, NativeArrayOptions.UninitializedMemory);
            m_WorkerCount = new NativeArray<int>(grid.CellCount * m_Workers, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public JobHandle Schedule(NativeArray<float3> positions, int activeCount, JobHandle dependency = default)
        {
            JobHandle handle = new HashCellJob
            {
                Grid = Grid,
                Positions = positions,
                UnitCell = m_UnitCell,
            }.Schedule(activeCount, 256, dependency);

            handle = new HashCountJob
            {
                Cells = Grid.CellCount,
                Workers = m_Workers,
                ActiveCount = activeCount,
                UnitCell = m_UnitCell,
                WorkerCount = m_WorkerCount,
            }.Schedule(m_Workers, 1, handle);

            handle = new HashTotalJob
            {
                Cells = Grid.CellCount,
                Workers = m_Workers,
                WorkerCount = m_WorkerCount,
                CellTotal = m_CellTotal,
            }.Schedule(Grid.CellCount, 256, handle);

            handle = new HashScanJob
            {
                Cells = Grid.CellCount,
                CellTotal = m_CellTotal,
                CellStarts = CellStarts,
            }.Schedule(handle);

            handle = new HashOffsetJob
            {
                Cells = Grid.CellCount,
                Workers = m_Workers,
                CellStarts = CellStarts,
                WorkerCount = m_WorkerCount,
            }.Schedule(Grid.CellCount, 256, handle);

            return new HashScatterJob
            {
                Cells = Grid.CellCount,
                Workers = m_Workers,
                ActiveCount = activeCount,
                UnitCell = m_UnitCell,
                WorkerCursor = m_WorkerCount,
                SortedUnits = SortedUnits,
            }.Schedule(m_Workers, 1, handle);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            if (CellStarts.IsCreated) CellStarts.Dispose();
            if (SortedUnits.IsCreated) SortedUnits.Dispose();
            if (m_CellTotal.IsCreated) m_CellTotal.Dispose();
            if (m_UnitCell.IsCreated) m_UnitCell.Dispose();
            if (m_WorkerCount.IsCreated) m_WorkerCount.Dispose();

            m_Disposed = true;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct HashCellJob : IJobParallelFor
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<float3> Positions;

        [WriteOnly] public NativeArray<int> UnitCell;

        public void Execute(int index) => UnitCell[index] = Grid.WorldToIndex(Positions[index].xy);
    }

    [BurstCompile]
    public struct HashCountJob : IJobParallelFor
    {
        public int Cells;
        public int Workers;
        public int ActiveCount;

        [ReadOnly] public NativeArray<int> UnitCell;

        [NativeDisableParallelForRestriction] public NativeArray<int> WorkerCount;

        public void Execute(int worker)
        {
            int offset = worker * Cells;

            for (int c = 0; c < Cells; c++)
                WorkerCount[offset + c] = 0;

            int lo = (int)((long)ActiveCount * worker / Workers);
            int hi = (int)((long)ActiveCount * (worker + 1) / Workers);

            for (int i = lo; i < hi; i++)
                WorkerCount[offset + UnitCell[i]]++;
        }
    }

    [BurstCompile]
    public struct HashTotalJob : IJobParallelFor
    {
        public int Cells;
        public int Workers;

        [ReadOnly] public NativeArray<int> WorkerCount;

        [WriteOnly] public NativeArray<int> CellTotal;

        public void Execute(int index)
        {
            int total = 0;
            for (int w = 0; w < Workers; w++)
                total += WorkerCount[w * Cells + index];

            CellTotal[index] = total;
        }
    }

    [BurstCompile]
    public struct HashScanJob : IJob
    {
        public int Cells;

        [ReadOnly] public NativeArray<int> CellTotal;

        [WriteOnly] public NativeArray<int> CellStarts;

        public void Execute()
        {
            int running = 0;

            for (int c = 0; c < Cells; c++)
            {
                CellStarts[c] = running;
                running += CellTotal[c];
            }

            CellStarts[Cells] = running;
        }
    }

    [BurstCompile]
    public struct HashOffsetJob : IJobParallelFor
    {
        public int Cells;
        public int Workers;

        [ReadOnly] public NativeArray<int> CellStarts;

        [NativeDisableParallelForRestriction] public NativeArray<int> WorkerCount;

        public void Execute(int index)
        {
            int cursor = CellStarts[index];

            for (int w = 0; w < Workers; w++)
            {
                int slot = w * Cells + index;
                int count = WorkerCount[slot];
                WorkerCount[slot] = cursor;
                cursor += count;
            }
        }
    }

    [BurstCompile]
    public struct HashScatterJob : IJobParallelFor
    {
        public int Cells;
        public int Workers;
        public int ActiveCount;

        [ReadOnly] public NativeArray<int> UnitCell;

        [NativeDisableParallelForRestriction] public NativeArray<int> WorkerCursor;
        [NativeDisableParallelForRestriction] public NativeArray<int> SortedUnits;

        public void Execute(int worker)
        {
            int offset = worker * Cells;

            int lo = (int)((long)ActiveCount * worker / Workers);
            int hi = (int)((long)ActiveCount * (worker + 1) / Workers);

            for (int i = lo; i < hi; i++)
            {
                int slot = offset + UnitCell[i];
                SortedUnits[WorkerCursor[slot]++] = i;
            }
        }
    }
}
