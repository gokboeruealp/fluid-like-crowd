using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdArriveJob : IJobParallelFor
    {
        public float2 GoalMin;
        public float2 GoalMax;

        public byte Reason;

        [ReadOnly] public NativeArray<float3> Positions;

        public NativeArray<byte> Retired;

        public void Execute(int index)
        {
            if (Retired[index] != 0)
                return;

            float2 position = Positions[index].xy;

            if (math.all(position >= GoalMin) && math.all(position <= GoalMax))
                Retired[index] = Reason;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdOverlapJob : IJobParallelFor
    {
        public NavGrid Grid;
        public float MaxRadius;

        public WallSampler Wall;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<int> CellStarts;
        [ReadOnly] public NativeArray<int> SortedUnits;

        [WriteOnly] public NativeArray<float> Overlap;

        [WriteOnly] public NativeArray<float> Buried;

        public void Execute(int index)
        {
            float2 position = Positions[index].xy;
            float radius = Radii[index];

            int reach = (int)math.ceil((radius + MaxRadius) / Grid.CellSize);
            int2 centre = Grid.WorldToCoord(position);

            float worst = 0f;

            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int2 coord = centre + new int2(dx, dy);
                    if (!Grid.InBounds(coord))
                        continue;

                    int cell = Grid.Index(coord);

                    for (int slot = CellStarts[cell]; slot < CellStarts[cell + 1]; slot++)
                    {
                        int other = SortedUnits[slot];
                        if (other == index)
                            continue;

                        float contact = radius + Radii[other];
                        float distanceSq = math.distancesq(position, Positions[other].xy);

                        if (distanceSq >= contact * contact)
                            continue;

                        worst = math.max(worst, 1f - math.sqrt(distanceSq) / contact);
                    }
                }
            }

            Overlap[index] = worst;
            Buried[index] = math.max(0f, radius - Wall.Depth(position));
        }
    }
}
