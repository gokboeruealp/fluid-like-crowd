using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd
{
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdWalkJob : IJobParallelFor
    {
        public NavGrid Grid;
        public float DeltaTime;

        public float Time;

        public float2 FieldMin;
        public float2 FieldMax;

        public float LaneSpread;

        public float LaneCells;

        public float WanderSpread;
        public float WanderRate;

        public float GaitDrift;
        public float GaitRate;

        public float Responsiveness;

        public float MaxSpeed;

        [ReadOnly] public NativeArray<byte> Cost;
        [ReadOnly] public NativeArray<float2> Flow;
        [ReadOnly] public NativeArray<float> Clearance;
        [ReadOnly] public NativeArray<float> Speeds;

        [ReadOnly] public NativeArray<float> Stun;

        [ReadOnly] public NativeArray<float> Lane;

        public CrowdSampler Crowd;

        public NativeArray<float3> Positions;

        public NativeArray<float2> Previous;

        public NativeArray<float2> Velocity;

        public void Execute(int index)
        {
            float3 stored = Positions[index];
            float2 position = stored.xy;

            int cell = Grid.WorldToIndex(position);
            float2 direction = SampleFlow(position, cell);

            if (LaneSpread > 0f || WanderSpread > 0f)
            {
                float room = math.saturate(Clearance[cell] / math.max(1f, LaneCells));
                float2 across = new float2(direction.y, -direction.x);

                float offset = Lane[index] * LaneSpread +
                               CrowdNoise.Meander(Lane[index], 0u, WanderRate, Time) * WanderSpread;

                direction = math.normalizesafe(direction + across * (offset * room), direction);
            }

            float speed = Stun[index] > 0f ? 0f : Speeds[index];

            if (GaitDrift > 0f)
                speed *= 1f + CrowdNoise.Meander(Lane[index], 0x9E3779B9u, GaitRate, Time) * GaitDrift;

            float2 velocity = math.lerp(
                Velocity[index], direction * speed, 1f - math.exp(-Responsiveness * DeltaTime));

            velocity += Crowd.Push(position);

            velocity = CrowdNoise.Limit(velocity, MaxSpeed);

            Previous[index] = position;
            Velocity[index] = velocity;

            Positions[index] = new float3(
                math.clamp(position + velocity * DeltaTime, FieldMin, FieldMax), stored.z);
        }

        float2 SampleFlow(float2 position, int cell)
        {
            float2 local = (position - Grid.Origin) / Grid.CellSize - 0.5f;
            int2 baseCoord = (int2)math.floor(local);
            float2 t = local - baseCoord;

            float2 sum = float2.zero;
            float weight = 0f;

            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    int2 coord = baseCoord + new int2(dx, dy);
                    if (!Grid.InBounds(coord))
                        continue;

                    int neighbour = Grid.Index(coord);
                    if (Cost[neighbour] == FlowField.Blocked)
                        continue;

                    float w = (dx == 0 ? 1f - t.x : t.x) * (dy == 0 ? 1f - t.y : t.y);
                    sum += Flow[neighbour] * w;
                    weight += w;
                }
            }

            if (weight <= 1e-5f)
                return Flow[cell];

            return math.normalizesafe(sum / weight, Flow[cell]);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdContactJob : IJobParallelFor
    {
        public NavGrid Grid;

        public float MaxRadius;

        public float Relaxation;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<int> CellStarts;
        [ReadOnly] public NativeArray<int> SortedUnits;

        [WriteOnly] public NativeArray<float2> Delta;

        public void Execute(int index)
        {
            float2 position = Positions[index].xy;
            float radius = Radii[index];

            float mass = radius * radius;

            int reach = (int)math.ceil((radius + MaxRadius) / Grid.CellSize);
            int2 centre = Grid.WorldToCoord(position);

            float2 push = float2.zero;
            int contacts = 0;

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

                        float otherRadius = Radii[other];
                        float contact = radius + otherRadius;

                        float2 delta = position - Positions[other].xy;
                        float distanceSq = math.lengthsq(delta);

                        if (distanceSq >= contact * contact)
                            continue;

                        float otherMass = otherRadius * otherRadius;
                        float share = otherMass / (mass + otherMass);

                        contacts++;

                        if (distanceSq < 1e-8f)
                        {
                            float angle = (index ^ other) * 0.61803399f;
                            math.sincos(angle, out float sin, out float cos);
                            push += new float2(cos, sin) * (contact * share);
                            continue;
                        }

                        float distance = math.sqrt(distanceSq);
                        push += delta / distance * ((contact - distance) * share);
                    }
                }
            }

            Delta[index] = contacts > 0 ? push * (Relaxation / contacts) : float2.zero;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdProjectJob : IJobParallelFor
    {
        public WallSampler Wall;
        public float2 FieldMin;
        public float2 FieldMax;

        [ReadOnly] public NativeArray<float2> Delta;
        [ReadOnly] public NativeArray<float> Radii;

        public NativeArray<float3> Positions;

        public void Execute(int index)
        {
            float3 stored = Positions[index];
            float2 position = stored.xy + Delta[index];

            Wall.Resolve(ref position, Radii[index]);

            Positions[index] = new float3(math.clamp(position, FieldMin, FieldMax), stored.z);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdReadbackJob : IJobParallelFor
    {
        public float DeltaTime;
        public float MaxSpeed;

        public float Viscosity;

        public float TurnRate;

        public float FacingSpread;

        public CrowdSampler Crowd;

        [ReadOnly] public NativeArray<float2> Previous;
        [ReadOnly] public NativeArray<float> Speeds;
        [ReadOnly] public NativeArray<float> Lane;

        public NativeArray<float3> Positions;
        public NativeArray<float2> Velocity;

        public void Execute(int index)
        {
            float3 stored = Positions[index];
            float2 position = stored.xy;
            float2 motion = position - Previous[index];

            float2 velocity = motion / DeltaTime;

            if (Viscosity > 0f)
                velocity = math.lerp(
                    velocity, Crowd.Current(position), math.saturate(Viscosity * DeltaTime));

            Velocity[index] = CrowdNoise.Limit(velocity, MaxSpeed);

            float intended = Speeds[index] * DeltaTime;
            float progress = intended > 1e-6f ? math.saturate(math.length(motion) / intended) : 0f;

            Positions[index] = new float3(
                position,
                CrowdNoise.Face(stored.z, motion, Lane[index], FacingSpread, TurnRate * DeltaTime * progress));
        }
    }

    public static class CrowdNoise
    {
        public static float2 Limit(float2 value, float ceiling)
        {
            float lengthSq = math.lengthsq(value);
            return lengthSq > ceiling * ceiling
                ? value * (ceiling / math.sqrt(lengthSq))
                : value;
        }

        public static float Face(float heading, float2 motion, float lane, float spread, float step)
        {
            if (spread > 0f)
            {
                float lean = spread * (Random01(Hash(math.asuint(lane) ^ 0x85EBCA6Bu), 0) * 2f - 1f);

                math.sincos(lean, out float sin, out float cos);
                motion = new float2(
                    motion.x * cos - motion.y * sin, motion.x * sin + motion.y * cos);
            }

            return Turn(heading, motion, step);
        }

        public static float Turn(float heading, float2 towards, float step)
        {
            if (math.lengthsq(towards) < 1e-8f || step <= 0f)
                return heading;

            const float turn = 2f * math.PI;

            float delta = math.atan2(towards.y, towards.x) - heading;
            delta -= turn * math.round(delta / turn);

            return heading + math.clamp(delta, -step, step);
        }

        public static float Meander(float lane, uint salt, float rate, float time)
        {
            uint seed = Hash(math.asuint(lane) ^ salt);

            float t = time * rate;
            int step = (int)math.floor(t);
            float f = t - step;

            f = f * f * (3f - 2f * f);

            return math.lerp(Random01(seed, step), Random01(seed, step + 1), f) * 2f - 1f;
        }

        public static uint Hash(uint value)
        {
            value ^= value >> 16;
            value *= 2246822519u;
            value ^= value >> 13;
            value *= 3266489917u;
            value ^= value >> 16;
            return value;
        }

        public static float Random01(uint seed, int step) =>
            (Hash(seed ^ ((uint)step * 2654435761u)) >> 8) * (1f / 16777216f);
    }
}
