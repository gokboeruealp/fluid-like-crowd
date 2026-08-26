using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    public sealed class WallField : IDisposable
    {
        public readonly NavGrid Grid;

        public NativeArray<float> Distance;

        public NativeArray<float2> Normal;

        NativeArray<float2> m_Nearest;
        bool m_Disposed;

        public WallField(NavGrid grid, Allocator allocator)
        {
            Grid = grid;
            int cells = grid.CellCount;

            Distance = new NativeArray<float>(cells, allocator, NativeArrayOptions.UninitializedMemory);
            Normal = new NativeArray<float2>(cells, allocator, NativeArrayOptions.UninitializedMemory);
            m_Nearest = new NativeArray<float2>(cells, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public JobHandle Schedule(NativeArray<byte> cost, JobHandle dependency = default)
        {
            var sweep = new WallDistanceJob
            {
                Grid = Grid,
                Cost = cost,
                Nearest = m_Nearest,
                Distance = Distance,
            }.Schedule(dependency);

            return new WallNormalJob
            {
                Grid = Grid,
                Distance = Distance,
                Normal = Normal,
            }.Schedule(Grid.CellCount, 256, sweep);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            if (Distance.IsCreated) Distance.Dispose();
            if (Normal.IsCreated) Normal.Dispose();
            if (m_Nearest.IsCreated) m_Nearest.Dispose();

            m_Disposed = true;
        }
    }

    public struct WallSampler
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<float> Distance;
        [ReadOnly] public NativeArray<float2> Normal;

        public float Depth(float2 world)
        {
            Weights(world, out int2 baseCoord, out float2 t);

            float d00 = Distance[Clamped(baseCoord + new int2(0, 0))];
            float d10 = Distance[Clamped(baseCoord + new int2(1, 0))];
            float d01 = Distance[Clamped(baseCoord + new int2(0, 1))];
            float d11 = Distance[Clamped(baseCoord + new int2(1, 1))];

            return math.lerp(math.lerp(d00, d10, t.x), math.lerp(d01, d11, t.x), t.y);
        }

        public float2 Out(float2 world)
        {
            Weights(world, out int2 baseCoord, out float2 t);

            float2 n00 = Normal[Clamped(baseCoord + new int2(0, 0))];
            float2 n10 = Normal[Clamped(baseCoord + new int2(1, 0))];
            float2 n01 = Normal[Clamped(baseCoord + new int2(0, 1))];
            float2 n11 = Normal[Clamped(baseCoord + new int2(1, 1))];

            float2 blended = math.lerp(math.lerp(n00, n10, t.x), math.lerp(n01, n11, t.x), t.y);
            return math.normalizesafe(blended);
        }

        public bool Resolve(ref float2 position, float radius)
        {
            float depth = Depth(position);
            if (depth >= radius)
                return false;

            position += Out(position) * (radius - depth);
            return true;
        }

        void Weights(float2 world, out int2 baseCoord, out float2 t)
        {
            float2 local = (world - Grid.Origin) / Grid.CellSize - 0.5f;
            baseCoord = (int2)math.floor(local);
            t = local - baseCoord;
        }

        int Clamped(int2 coord) =>
            Grid.Index(math.clamp(coord, int2.zero, new int2(Grid.Width - 1, Grid.Height - 1)));
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct WallDistanceJob : IJob
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<byte> Cost;

        public NativeArray<float2> Nearest;

        [WriteOnly] public NativeArray<float> Distance;

        const float Far = 1e6f;

        public void Execute()
        {
            Seed();

            for (int y = 0; y < Grid.Height; y++)
            {
                for (int x = 0; x < Grid.Width; x++)
                {
                    Compare(x, y, -1, 0);
                    Compare(x, y, 0, -1);
                    Compare(x, y, -1, -1);
                    Compare(x, y, 1, -1);
                }

                for (int x = Grid.Width - 2; x >= 0; x--)
                    Compare(x, y, 1, 0);
            }

            for (int y = Grid.Height - 1; y >= 0; y--)
            {
                for (int x = Grid.Width - 1; x >= 0; x--)
                {
                    Compare(x, y, 1, 0);
                    Compare(x, y, 0, 1);
                    Compare(x, y, 1, 1);
                    Compare(x, y, -1, 1);
                }

                for (int x = 1; x < Grid.Width; x++)
                    Compare(x, y, -1, 0);
            }

            Resolve();
        }

        void Seed()
        {
            for (int y = 0; y < Grid.Height; y++)
            {
                for (int x = 0; x < Grid.Width; x++)
                {
                    int cell = Grid.Index(new int2(x, y));
                    bool solid = Cost[cell] == FlowField.Blocked;

                    float2 best = new float2(Far, Far);
                    float bestSq = Far * Far;

                    for (int n = 0; n < 4; n++)
                    {
                        int2 step = Axis(n);
                        int2 coord = new int2(x, y) + step;

                        bool otherSolid = !Grid.InBounds(coord) || Cost[Grid.Index(coord)] == FlowField.Blocked;

                        if (otherSolid == solid)
                            continue;

                        float2 candidate = new float2(step.x, step.y) * 0.5f;
                        float candidateSq = math.lengthsq(candidate);

                        if (candidateSq < bestSq)
                        {
                            bestSq = candidateSq;
                            best = candidate;
                        }
                    }

                    Nearest[cell] = best;
                }
            }
        }

        static int2 Axis(int index)
        {
            switch (index)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(-1, 0);
                case 2: return new int2(0, 1);
                default: return new int2(0, -1);
            }
        }

        void Compare(int x, int y, int dx, int dy)
        {
            int2 coord = new int2(x + dx, y + dy);
            if (!Grid.InBounds(coord))
                return;

            int cell = Grid.Index(new int2(x, y));

            float2 candidate = Nearest[Grid.Index(coord)] + new float2(dx, dy);

            if (math.lengthsq(candidate) < math.lengthsq(Nearest[cell]))
                Nearest[cell] = candidate;
        }

        void Resolve()
        {
            for (int cell = 0; cell < Nearest.Length; cell++)
            {
                float length = math.length(Nearest[cell]) * Grid.CellSize;
                Distance[cell] = Cost[cell] == FlowField.Blocked ? -length : length;
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct WallNormalJob : IJobParallelFor
    {
        public NavGrid Grid;

        [ReadOnly] public NativeArray<float> Distance;

        [WriteOnly] public NativeArray<float2> Normal;

        public void Execute(int index)
        {
            int2 coord = Grid.Coord(index);

            float left = Depth(coord + new int2(-1, 0));
            float right = Depth(coord + new int2(1, 0));
            float down = Depth(coord + new int2(0, -1));
            float up = Depth(coord + new int2(0, 1));

            float2 gradient = new float2(right - left, up - down);

            Normal[index] = math.normalizesafe(gradient);
        }

        float Depth(int2 coord) =>
            Grid.InBounds(coord) ? Distance[Grid.Index(coord)] : -Grid.CellSize;
    }
}
