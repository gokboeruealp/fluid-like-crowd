using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    public sealed class FlowField : IDisposable
    {
        public const byte Blocked = 255;

        public readonly NavGrid Grid;

        public NativeArray<byte> Cost;
        public NativeArray<float> Integration;

        public NativeArray<float> Clearance;
        public NativeArray<float2> Flow;
        public NativeArray<int> Density;

        public NativeList<int> GoalCells;

        NativeList<int> m_Frontier;
        bool m_Disposed;

        public FlowField(NavGrid grid, Allocator allocator)
        {
            Grid = grid;
            int cells = grid.CellCount;

            Cost = new NativeArray<byte>(cells, allocator);
            Integration = new NativeArray<float>(cells, allocator, NativeArrayOptions.UninitializedMemory);
            Clearance = new NativeArray<float>(cells, allocator);
            Flow = new NativeArray<float2>(cells, allocator);
            Density = new NativeArray<int>(cells, allocator);
            GoalCells = new NativeList<int>(256, allocator);
            m_Frontier = new NativeList<int>(cells, allocator);
        }

        public JobHandle ScheduleClearance(JobHandle dependency = default) =>
            new ClearanceFieldJob
            {
                Grid = Grid,
                Cost = Cost,
                Clearance = Clearance,
                Frontier = m_Frontier,
            }.Schedule(dependency);

        public JobHandle Schedule(
            float densityWeight, float centreWeight = 0f, float centreCells = 0f,
            JobHandle dependency = default)
        {
            var integration = new IntegrationFieldJob
            {
                Grid = Grid,
                Cost = Cost,
                Density = Density,
                DensityWeight = densityWeight,
                Clearance = Clearance,
                CentreWeight = centreWeight,
                CentreCells = centreCells,
                GoalCells = GoalCells.AsArray(),
                Integration = Integration,
                Frontier = m_Frontier,
            }.Schedule(dependency);

            return new FlowFieldJob
            {
                Grid = Grid,
                Cost = Cost,
                Integration = Integration,
                Flow = Flow,
            }.Schedule(Grid.CellCount, 256, integration);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            if (Cost.IsCreated) Cost.Dispose();
            if (Integration.IsCreated) Integration.Dispose();
            if (Clearance.IsCreated) Clearance.Dispose();
            if (Flow.IsCreated) Flow.Dispose();
            if (Density.IsCreated) Density.Dispose();
            if (GoalCells.IsCreated) GoalCells.Dispose();
            if (m_Frontier.IsCreated) m_Frontier.Dispose();

            m_Disposed = true;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct IntegrationFieldJob : IJob
    {
        public NavGrid Grid;
        public float DensityWeight;

        [ReadOnly] public NativeArray<byte> Cost;
        [ReadOnly] public NativeArray<int> Density;
        [ReadOnly] public NativeArray<int> GoalCells;

        [ReadOnly] public NativeArray<float> Clearance;
        public float CentreWeight;
        public float CentreCells;

        public NativeArray<float> Integration;
        public NativeList<int> Frontier;

        public void Execute()
        {
            for (int i = 0; i < Integration.Length; i++)
                Integration[i] = float.MaxValue;

            Frontier.Clear();

            for (int i = 0; i < GoalCells.Length; i++)
            {
                int cell = GoalCells[i];
                if (Cost[cell] == FlowField.Blocked)
                    continue;

                Integration[cell] = 0f;
                Frontier.Add(cell);
            }

            int head = 0;
            while (head < Frontier.Length)
            {
                int current = Frontier[head++];
                float currentCost = Integration[current];
                int2 coord = Grid.Coord(current);

                for (int n = 0; n < GridNeighbours.Count; n++)
                {
                    int2 offset = GridNeighbours.Offset(n);
                    int2 neighbourCoord = coord + offset;

                    if (!Grid.InBounds(neighbourCoord))
                        continue;

                    int neighbour = Grid.Index(neighbourCoord);
                    if (Cost[neighbour] == FlowField.Blocked)
                        continue;

                    bool diagonal = GridNeighbours.IsDiagonal(offset);
                    if (diagonal && CutsCorner(coord, neighbourCoord))
                        continue;

                    float step = diagonal ? 1.4142136f : 1f;
                    float cellCost = Cost[neighbour] +
                                     DensityWeight * Density[neighbour] +
                                     CentreCharge(neighbour);

                    float candidate = currentCost + step * cellCost;

                    if (candidate < Integration[neighbour] - 1e-4f)
                    {
                        Integration[neighbour] = candidate;
                        Frontier.Add(neighbour);
                    }
                }
            }
        }

        float CentreCharge(int cell)
        {
            if (CentreWeight <= 0f || CentreCells <= 0f)
                return 0f;

            float room = Clearance[cell];
            return room >= CentreCells ? 0f : CentreWeight * (1f - room / CentreCells);
        }

        bool CutsCorner(int2 from, int2 to)
        {
            int horizontal = Grid.Index(new int2(to.x, from.y));
            int vertical = Grid.Index(new int2(from.x, to.y));
            return Cost[horizontal] == FlowField.Blocked || Cost[vertical] == FlowField.Blocked;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct ClearanceFieldJob : IJob
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<byte> Cost;

        public NativeArray<float> Clearance;
        public NativeList<int> Frontier;

        public void Execute()
        {
            Frontier.Clear();

            for (int i = 0; i < Clearance.Length; i++)
            {
                bool blocked = Cost[i] == FlowField.Blocked;
                Clearance[i] = blocked ? 0f : float.MaxValue;

                if (blocked)
                    Frontier.Add(i);
            }

            int head = 0;
            while (head < Frontier.Length)
            {
                int current = Frontier[head++];
                float here = Clearance[current];
                int2 coord = Grid.Coord(current);

                for (int n = 0; n < GridNeighbours.Count; n++)
                {
                    int2 offset = GridNeighbours.Offset(n);
                    int2 neighbourCoord = coord + offset;

                    if (!Grid.InBounds(neighbourCoord))
                        continue;

                    int neighbour = Grid.Index(neighbourCoord);
                    if (Cost[neighbour] == FlowField.Blocked)
                        continue;

                    float candidate = here + (GridNeighbours.IsDiagonal(offset) ? 1.4142136f : 1f);

                    if (candidate < Clearance[neighbour] - 1e-4f)
                    {
                        Clearance[neighbour] = candidate;
                        Frontier.Add(neighbour);
                    }
                }
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct FlowFieldJob : IJobParallelFor
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<byte> Cost;
        [ReadOnly] public NativeArray<float> Integration;

        [WriteOnly] public NativeArray<float2> Flow;

        public void Execute(int index)
        {
            if (Cost[index] == FlowField.Blocked)
            {
                Flow[index] = float2.zero;
                return;
            }

            int2 coord = Grid.Coord(index);
            float best = Integration[index];
            int2 bestOffset = int2.zero;

            for (int n = 0; n < GridNeighbours.Count; n++)
            {
                int2 offset = GridNeighbours.Offset(n);
                int2 neighbourCoord = coord + offset;

                if (!Grid.InBounds(neighbourCoord))
                    continue;

                int neighbour = Grid.Index(neighbourCoord);
                if (Cost[neighbour] == FlowField.Blocked)
                    continue;

                if (Integration[neighbour] < best)
                {
                    best = Integration[neighbour];
                    bestOffset = offset;
                }
            }

            Flow[index] = math.normalizesafe(new float2(bestOffset.x, bestOffset.y));
        }
    }

    [BurstCompile]
    public struct DensityScatterJob : IJob
    {
        public NavGrid Grid;
        public int ActiveCount;

        [ReadOnly] public NativeArray<float3> Positions;

        public NativeArray<int> Density;

        public void Execute()
        {
            for (int i = 0; i < Density.Length; i++)
                Density[i] = 0;

            for (int i = 0; i < ActiveCount; i++)
                Density[Grid.WorldToIndex(Positions[i].xy)]++;
        }
    }
}
