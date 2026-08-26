using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FluidCrowd.Demo
{
    public struct Blast
    {
        public int Source;

        public float2 Position;

        public float Radius;

        public float PushInner;

        public float PushRadius;

        public float2 PushBehind;

        public float Impulse;

        public float StunSeconds;

        public bool Lethal;
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdBlastJob : IJobParallelFor
    {
        public float ReferenceRadius;

        [ReadOnly] public NativeArray<Blast> Blasts;
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Radii;

        public NativeArray<int> LastBlast;

        public NativeArray<float2> Velocity;
        public NativeArray<float> Stun;

        public byte Reason;

        public NativeArray<byte> Retired;

        public void Execute(int index)
        {
            if (Retired[index] != 0)
                return;

            float2 position = Positions[index].xy;
            float radius = Radii[index];

            float2 shove = float2.zero;
            float stun = 0f;
            bool taken = false;

            int lastBlast = LastBlast[index];

            for (int i = 0; i < Blasts.Length; i++)
            {
                Blast blast = Blasts[i];

                if (blast.Source != 0 && blast.Source == lastBlast)
                    continue;

                float reach = blast.PushRadius + radius;

                float2 offset = position - blast.Position;
                float distanceSq = math.lengthsq(offset);

                if (distanceSq > reach * reach)
                    continue;

                float distance = math.sqrt(distanceSq);

                bool catches = blast.Lethal || blast.StunSeconds > 0f;

                if (catches && distance <= blast.Radius + radius)
                {
                    taken |= blast.Lethal;
                    stun = math.max(stun, blast.StunSeconds);

                    lastBlast = blast.Source;
                }

                if (blast.Impulse <= 0f)
                    continue;

                float inner = blast.PushInner - radius;

                if (distance < inner)
                    continue;

                if (math.dot(offset, blast.PushBehind) > 0f)
                    continue;

                float2 away = distance > 1e-4f
                    ? offset / distance
                    : new float2(math.cos(index * 2.39996323f), math.sin(index * 2.39996323f));

                float falloff = 1f - (distance - inner) / math.max(reach - inner, 1e-4f);

                shove += away * (falloff * blast.Impulse);
            }

            LastBlast[index] = lastBlast;

            if (stun > 0f)
                Stun[index] = math.max(Stun[index], stun);

            if (taken)
            {
                Retired[index] = Reason;
                return;
            }

            if (math.lengthsq(shove) <= 1e-10f)
                return;

            float ratio = math.clamp(
                ReferenceRadius * ReferenceRadius / math.max(1e-6f, radius * radius), 0.25f, 4f);

            Velocity[index] += shove * ratio;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct StunDecayJob : IJobParallelFor
    {
        public float DeltaTime;

        public NativeArray<float> Stun;

        public void Execute(int index) => Stun[index] = math.max(0f, Stun[index] - DeltaTime);
    }
}
