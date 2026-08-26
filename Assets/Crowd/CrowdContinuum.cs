using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    public sealed class CrowdContinuum : IDisposable
    {
        public readonly NavGrid Grid;

        public NativeArray<float> Density;

        public NativeArray<float> Solid;

        public NativeArray<float2> Mean;

        public NativeArray<bool> Congested;

        public NativeArray<float2> Correction;

        NativeArray<float> m_Pressure, m_Scratch, m_Rhs;
        NativeArray<float2> m_Momentum;
        NativeArray<float> m_Mass;

        NativeArray<float2> m_WorkerMomentum;
        NativeArray<float> m_WorkerMass;

        readonly int m_Workers;
        bool m_Disposed;

        public CrowdContinuum(NavGrid grid, int workers, Allocator allocator)
        {
            Grid = grid;
            m_Workers = math.max(1, workers);
            int cells = grid.CellCount;

            Density = new NativeArray<float>(cells, allocator);
            Solid = new NativeArray<float>(cells, allocator);
            Mean = new NativeArray<float2>(cells, allocator);
            Congested = new NativeArray<bool>(cells, allocator);
            Correction = new NativeArray<float2>(cells, allocator);

            m_Pressure = new NativeArray<float>(cells, allocator);
            m_Scratch = new NativeArray<float>(cells, allocator);
            m_Rhs = new NativeArray<float>(cells, allocator);
            m_Momentum = new NativeArray<float2>(cells, allocator);
            m_Mass = new NativeArray<float>(cells, allocator);

            m_WorkerMomentum = new NativeArray<float2>(cells * m_Workers, allocator);
            m_WorkerMass = new NativeArray<float>(cells * m_Workers, allocator);
        }

        public JobHandle ScheduleSolid(in NavGrid navGrid, NativeArray<byte> cost, JobHandle dependency = default) =>
            new CrowdSolidJob
            {
                Grid = Grid,
                Nav = navGrid,
                Cost = cost,
                Solid = Solid,
            }.Schedule(Grid.CellCount, 64, dependency);

        public JobHandle Schedule(
            NativeArray<float3> positions, NativeArray<float2> velocity, NativeArray<float> radii,
            int activeCount, float deltaTime, float restDensity, int iterations, float stiffness,
            float freeSurface, float maxCorrection, JobHandle dependency = default)
        {
            JobHandle handle = new CrowdMeasureJob
            {
                Grid = Grid,
                ActiveCount = activeCount,
                Workers = m_Workers,
                Positions = positions,
                Velocity = velocity,
                Radii = radii,
                WorkerMomentum = m_WorkerMomentum,
                WorkerMass = m_WorkerMass,
            }.Schedule(m_Workers, 1, dependency);

            handle = new CrowdReduceJob
            {
                Cells = Grid.CellCount,
                Workers = m_Workers,
                CellArea = Grid.CellSize * Grid.CellSize,
                WorkerMomentum = m_WorkerMomentum,
                WorkerMass = m_WorkerMass,
                Momentum = m_Momentum,
                Mass = m_Mass,
                Density = Density,
                Mean = Mean,
            }.Schedule(Grid.CellCount, 128, handle);

            handle = new CrowdDivergenceJob
            {
                Grid = Grid,
                DeltaTime = deltaTime,
                RestDensity = restDensity,
                FreeSurface = math.clamp(freeSurface, 0.1f, 1f),
                Stiffness = stiffness,
                Density = Density,
                Solid = Solid,
                Mean = Mean,
                Congested = Congested,
                Rhs = m_Rhs,
                Pressure = m_Pressure,
            }.Schedule(Grid.Height, 1, handle);

            NativeArray<float> from = m_Pressure, into = m_Scratch;

            for (int i = 0; i < math.max(1, iterations); i++)
            {
                handle = new CrowdPressureJob
                {
                    Grid = Grid,
                    Solid = Solid,
                    Congested = Congested,
                    Rhs = m_Rhs,
                    From = from,
                    Into = into,
                }.Schedule(Grid.Height, 1, handle);

                (from, into) = (into, from);
            }

            return new CrowdCorrectionJob
            {
                Grid = Grid,
                DeltaTime = deltaTime,
                MaxCorrection = maxCorrection,
                Solid = Solid,
                Congested = Congested,
                Pressure = from,
                Correction = Correction,
            }.Schedule(Grid.Height, 1, handle);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            if (Density.IsCreated) Density.Dispose();
            if (Solid.IsCreated) Solid.Dispose();
            if (Mean.IsCreated) Mean.Dispose();
            if (Congested.IsCreated) Congested.Dispose();
            if (Correction.IsCreated) Correction.Dispose();
            if (m_Pressure.IsCreated) m_Pressure.Dispose();
            if (m_Scratch.IsCreated) m_Scratch.Dispose();
            if (m_Rhs.IsCreated) m_Rhs.Dispose();
            if (m_Momentum.IsCreated) m_Momentum.Dispose();
            if (m_Mass.IsCreated) m_Mass.Dispose();
            if (m_WorkerMomentum.IsCreated) m_WorkerMomentum.Dispose();
            if (m_WorkerMass.IsCreated) m_WorkerMass.Dispose();

            m_Disposed = true;
        }
    }

    public struct CrowdSampler
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<float2> Correction;
        [ReadOnly] public NativeArray<float2> Mean;

        public float2 Push(float2 world) => Blend(Correction, world);

        public float2 Current(float2 world) => Blend(Mean, world);

        float2 Blend(in NativeArray<float2> field, float2 world)
        {
            float2 local = (world - Grid.Origin) / Grid.CellSize - 0.5f;
            int2 baseCoord = (int2)math.floor(local);
            float2 t = local - baseCoord;

            float2 v00 = field[Clamped(baseCoord + new int2(0, 0))];
            float2 v10 = field[Clamped(baseCoord + new int2(1, 0))];
            float2 v01 = field[Clamped(baseCoord + new int2(0, 1))];
            float2 v11 = field[Clamped(baseCoord + new int2(1, 1))];

            return math.lerp(math.lerp(v00, v10, t.x), math.lerp(v01, v11, t.x), t.y);
        }

        int Clamped(int2 coord) =>
            Grid.Index(math.clamp(coord, int2.zero, new int2(Grid.Width - 1, Grid.Height - 1)));
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdSolidJob : IJobParallelFor
    {
        public NavGrid Grid;
        public NavGrid Nav;

        [ReadOnly] public NativeArray<byte> Cost;

        [WriteOnly] public NativeArray<float> Solid;

        public void Execute(int index)
        {
            int2 coord = Grid.Coord(index);
            float2 min = Grid.Origin + new float2(coord.x, coord.y) * Grid.CellSize;

            int steps = math.max(4, (int)math.ceil(Grid.CellSize / Nav.CellSize) * 2);
            float stride = Grid.CellSize / steps;

            int blocked = 0;

            for (int y = 0; y < steps; y++)
            {
                for (int x = 0; x < steps; x++)
                {
                    float2 point = min + (new float2(x, y) + 0.5f) * stride;
                    int2 navCoord = (int2)math.floor((point - Nav.Origin) / Nav.CellSize);

                    if (!Nav.InBounds(navCoord) || Cost[Nav.Index(navCoord)] == FlowField.Blocked)
                        blocked++;
                }
            }

            Solid[index] = blocked / (float)(steps * steps);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdMeasureJob : IJobParallelFor
    {
        public NavGrid Grid;
        public int ActiveCount;
        public int Workers;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Radii;

        [NativeDisableParallelForRestriction] public NativeArray<float2> WorkerMomentum;
        [NativeDisableParallelForRestriction] public NativeArray<float> WorkerMass;

        public void Execute(int worker)
        {
            int cells = Grid.CellCount;
            int offset = worker * cells;

            for (int i = 0; i < cells; i++)
            {
                WorkerMomentum[offset + i] = float2.zero;
                WorkerMass[offset + i] = 0f;
            }

            int lo = (int)((long)ActiveCount * worker / Workers);
            int hi = (int)((long)ActiveCount * (worker + 1) / Workers);

            for (int i = lo; i < hi; i++)
            {
                float2 position = Positions[i].xy;
                float radius = Radii[i];

                float area = math.PI * radius * radius;
                float2 momentum = Velocity[i] * area;

                float2 local = (position - Grid.Origin) / Grid.CellSize - 0.5f;
                int2 baseCoord = (int2)math.floor(local);
                float2 t = local - baseCoord;

                Deposit(offset, baseCoord + new int2(0, 0), (1f - t.x) * (1f - t.y), area, momentum);
                Deposit(offset, baseCoord + new int2(1, 0), t.x * (1f - t.y), area, momentum);
                Deposit(offset, baseCoord + new int2(0, 1), (1f - t.x) * t.y, area, momentum);
                Deposit(offset, baseCoord + new int2(1, 1), t.x * t.y, area, momentum);
            }
        }

        void Deposit(int offset, int2 coord, float weight, float area, float2 momentum)
        {
            if (!Grid.InBounds(coord))
                return;

            int slot = offset + Grid.Index(coord);
            WorkerMass[slot] += weight * area;
            WorkerMomentum[slot] += weight * momentum;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdReduceJob : IJobParallelFor
    {
        public int Cells;
        public int Workers;
        public float CellArea;

        [ReadOnly] public NativeArray<float2> WorkerMomentum;
        [ReadOnly] public NativeArray<float> WorkerMass;

        [WriteOnly] public NativeArray<float2> Momentum;
        [WriteOnly] public NativeArray<float> Mass;
        [WriteOnly] public NativeArray<float> Density;
        [WriteOnly] public NativeArray<float2> Mean;

        public void Execute(int index)
        {
            float mass = 0f;
            float2 momentum = float2.zero;

            for (int w = 0; w < Workers; w++)
            {
                int slot = w * Cells + index;
                mass += WorkerMass[slot];
                momentum += WorkerMomentum[slot];
            }

            Mass[index] = mass;
            Momentum[index] = momentum;
            Density[index] = mass / CellArea;
            Mean[index] = mass > 1e-6f ? momentum / mass : float2.zero;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdDivergenceJob : IJobParallelFor
    {
        public NavGrid Grid;
        public float DeltaTime;
        public float RestDensity;

        public float FreeSurface;

        public float Stiffness;

        [ReadOnly] public NativeArray<float> Density;
        [ReadOnly] public NativeArray<float> Solid;
        [ReadOnly] public NativeArray<float2> Mean;

        [NativeDisableParallelForRestriction] public NativeArray<bool> Congested;
        [NativeDisableParallelForRestriction] public NativeArray<float> Rhs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Pressure;

        public void Execute(int y)
        {
            int width = Grid.Width;
            int row = y * width;
            float scale = 1f / (2f * Grid.CellSize);
            float payback = Stiffness / (RestDensity * DeltaTime * DeltaTime);

            bool hasDown = y > 0;
            bool hasUp = y < Grid.Height - 1;

            float floor = RestDensity * FreeSurface;
            float band = math.max(1e-4f, RestDensity - floor);

            for (int x = 0; x < width; x++)
            {
                int index = row + x;

                if (Solid[index] > CrowdRock.Threshold)
                {
                    Congested[index] = false;
                    Rhs[index] = 0f;
                    Pressure[index] = 0f;
                    continue;
                }

                float free = math.max(CrowdRock.MinimumFloor, 1f - Solid[index]);
                float packed = Density[index] / free;

                if (packed < floor)
                {
                    Congested[index] = false;
                    Rhs[index] = 0f;
                    Pressure[index] = 0f;
                    continue;
                }

                float gate = math.saturate((packed - floor) / band);

                float2 right = Face(x < width - 1 ? index + 1 : index, index);
                float2 left = Face(x > 0 ? index - 1 : index, index);
                float2 up = Face(hasUp ? index + width : index, index);
                float2 down = Face(hasDown ? index - width : index, index);

                float divergence = ((right.x - left.x) + (up.y - down.y)) * scale;

                Congested[index] = true;
                Rhs[index] = gate * divergence / DeltaTime
                           - payback * math.max(0f, packed - RestDensity);
            }
        }

        float2 Face(int neighbour, int fallback) =>
            Solid[neighbour] <= CrowdRock.Threshold ? Mean[neighbour] : Mean[fallback];
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdPressureJob : IJobParallelFor
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<float> Solid;
        [ReadOnly] public NativeArray<bool> Congested;
        [ReadOnly] public NativeArray<float> Rhs;
        [ReadOnly] public NativeArray<float> From;

        [NativeDisableParallelForRestriction] public NativeArray<float> Into;

        public void Execute(int y)
        {
            int width = Grid.Width;
            int row = y * width;
            float h2 = Grid.CellSize * Grid.CellSize;

            bool hasDown = y > 0;
            bool hasUp = y < Grid.Height - 1;

            for (int x = 0; x < width; x++)
            {
                int index = row + x;

                if (!Congested[index])
                {
                    Into[index] = 0f;
                    continue;
                }

                float sum = 0f;
                int count = 0;

                if (x > 0) Accumulate(index - 1, ref sum, ref count);
                if (x < width - 1) Accumulate(index + 1, ref sum, ref count);
                if (hasDown) Accumulate(index - width, ref sum, ref count);
                if (hasUp) Accumulate(index + width, ref sum, ref count);

                Into[index] = count == 0 ? 0f : math.max(0f, (sum - h2 * Rhs[index]) / count);
            }
        }

        void Accumulate(int neighbour, ref float sum, ref int count)
        {
            if (Solid[neighbour] > CrowdRock.Threshold)
                return;

            if (Congested[neighbour])
                sum += From[neighbour];

            count++;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdCorrectionJob : IJobParallelFor
    {
        public NavGrid Grid;
        public float DeltaTime;
        public float MaxCorrection;

        [ReadOnly] public NativeArray<float> Solid;
        [ReadOnly] public NativeArray<bool> Congested;
        [ReadOnly] public NativeArray<float> Pressure;

        [NativeDisableParallelForRestriction] public NativeArray<float2> Correction;

        public void Execute(int y)
        {
            int width = Grid.Width;
            int row = y * width;
            float scale = -DeltaTime / (2f * Grid.CellSize);

            bool hasDown = y > 0;
            bool hasUp = y < Grid.Height - 1;

            for (int x = 0; x < width; x++)
            {
                int index = row + x;

                if (Solid[index] > CrowdRock.Threshold)
                {
                    Correction[index] = float2.zero;
                    continue;
                }

                float right = Sample(x < width - 1 ? index + 1 : index, index);
                float left = Sample(x > 0 ? index - 1 : index, index);
                float up = Sample(hasUp ? index + width : index, index);
                float down = Sample(hasDown ? index - width : index, index);

                Correction[index] = CrowdNoise.Limit(
                    new float2(right - left, up - down) * scale, MaxCorrection);
            }
        }

        float Sample(int neighbour, int fallback) =>
            Solid[neighbour] > CrowdRock.Threshold ? Pressure[fallback] : Pressure[neighbour];
    }

    public static class CrowdRock
    {
        public const float Threshold = 0.85f;

        public const float MinimumFloor = 0.15f;
    }
}
