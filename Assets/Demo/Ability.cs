using System.Collections.Generic;
using Unity.Mathematics;

namespace FluidCrowd.Demo
{
    public enum AbilityKind : byte
    {
        ArrowRain = 0,
        Mortar = 1,
        StrafingRun = 2,
        Shockwave = 3,
        Stasis = 4,
        Atom = 5,
    }

    public readonly struct AbilitySpec
    {
        public readonly AbilityKind Kind;
        public readonly string Name;

        public readonly float Radius;

        public readonly float Length;

        public readonly float Impact;

        public readonly float Push;

        public readonly float PushSpeed;

        public readonly int PerTick;

        public readonly int Ticks;

        public readonly float TickInterval;

        public readonly float StunSeconds;

        public readonly bool Lethal;

        public AbilitySpec(
            AbilityKind kind, string name, float radius, float length, float impact, float push,
            float pushSpeed, int perTick, int ticks, float tickInterval, float stunSeconds = 0f,
            bool lethal = true)
        {
            Kind = kind;
            Name = name;
            Radius = radius;
            Length = length;
            Impact = impact;
            Push = push;
            PushSpeed = pushSpeed;
            PerTick = perTick;
            Ticks = ticks;
            TickInterval = tickInterval;
            StunSeconds = stunSeconds;
            Lethal = lethal;
        }

        public bool IsLine => Length > 0f;

        public bool IsScattered => Impact > 0f;

        public bool Catches => Lethal || StunSeconds > 0f;
    }

    public static class AbilityTable
    {
        public const int Count = 6;

        static readonly AbilitySpec[] s_Specs =
        {
            new AbilitySpec(
                AbilityKind.ArrowRain, "Arrow rain", radius: 3.2f, length: 0f, impact: 0.55f,
                push: 1.6f, pushSpeed: 9f, perTick: 3, ticks: 8, tickInterval: 0.1f),

            new AbilitySpec(
                AbilityKind.Mortar, "Mortar", radius: 4.2f, length: 0f, impact: 2.2f, push: 4.5f,
                pushSpeed: 18f, perTick: 1, ticks: 6, tickInterval: 0.13f),

            new AbilitySpec(
                AbilityKind.StrafingRun, "Strafing run", radius: 2f, length: 22f, impact: 1f,
                push: 2.6f, pushSpeed: 15f, perTick: 2, ticks: 28, tickInterval: 0.045f),

            new AbilitySpec(
                AbilityKind.Shockwave, "Shockwave", radius: 0f, length: 0f, impact: 0f, push: 34f,
                pushSpeed: 110f, perTick: 1, ticks: 1, tickInterval: 0f, lethal: false),

            new AbilitySpec(
                AbilityKind.Stasis, "Stasis field", radius: 5f, length: 0f, impact: 0f, push: 0f,
                pushSpeed: 0f, perTick: 1, ticks: 1, tickInterval: 0f, stunSeconds: 2.6f,
                lethal: false),

            new AbilitySpec(
                AbilityKind.Atom, "Atom", radius: 14f, length: 0f, impact: 0f, push: 18f,
                pushSpeed: 90f, perTick: 1, ticks: 1, tickInterval: 0f),
        };

        public static AbilitySpec At(int index) => s_Specs[index];

        public static AbilitySpec Of(AbilityKind kind) => s_Specs[(int)kind];
    }

    public struct Mark
    {
        public float2 From;
        public float2 To;
        public float Radius;
        public AbilityKind Kind;

        public float Brightness;

        public bool Ring;
    }

    public sealed class AbilityCaster
    {
        const float MinLine = 6f;

        const float FlashSeconds = 0.24f;

        struct Strike
        {
            public AbilityKind Kind;

            public int Source;

            public float2 From;
            public float2 To;

            public float2 Forward;
            public float Spacing;

            public float Area;
            public float Impact;
            public float Push;
            public float Impulse;
            public float StunSeconds;
            public bool Lethal;

            public int PerTick;
            public int TicksLeft;
            public int TicksTotal;
            public float Interval;
            public float Timer;
        }

        struct Flash
        {
            public float2 Position;
            public float Radius;
            public AbilityKind Kind;
            public float Life;
            public bool Ring;
        }

        readonly List<Strike> m_Strikes = new List<Strike>(16);
        readonly List<Flash> m_Flashes = new List<Flash>(512);
        readonly List<Blast> m_Blasts = new List<Blast>(64);

        Random m_Random = Random.CreateFromIndex(7);

        int m_Casts;

        int m_Armed = -1;
        bool m_Drawing;
        float2 m_LineFrom;
        float2 m_Cursor;

        public IReadOnlyList<Blast> Blasts => m_Blasts;

        public int Armed => m_Armed;

        public bool IsArmed => m_Armed >= 0;

        public int InFlight => m_Strikes.Count;

        public void Arm(int slot)
        {
            if (slot < 0 || slot >= AbilityTable.Count)
                return;

            m_Armed = m_Armed == slot ? -1 : slot;
            m_Drawing = false;
        }

        public void Disarm()
        {
            m_Armed = -1;
            m_Drawing = false;
        }

        public void Aim(float2 cursor) => m_Cursor = cursor;

        public void Cancel() => m_Drawing = false;

        public void Press(float2 at)
        {
            if (!IsArmed)
                return;

            m_Cursor = at;
            m_LineFrom = at;
            m_Drawing = true;
        }

        public void Release(float2 at)
        {
            if (!IsArmed || !m_Drawing)
                return;

            m_Cursor = at;
            m_Drawing = false;

            AbilitySpec spec = AbilityTable.At(m_Armed);

            if (!spec.IsLine)
            {
                Cast(spec, at, at);
                return;
            }

            float2 offset = at - m_LineFrom;
            float length = math.length(offset);

            if (length < MinLine)
                return;

            float2 forward = offset / length;
            Cast(spec, m_LineFrom, m_LineFrom + forward * math.min(length, spec.Length));
        }

        public void Tick(float deltaTime)
        {
            m_Blasts.Clear();

            for (int i = m_Strikes.Count - 1; i >= 0; i--)
            {
                Strike strike = m_Strikes[i];
                strike.Timer -= deltaTime;

                while (strike.Timer <= 0f && strike.TicksLeft > 0)
                {
                    int landed = strike.TicksTotal - strike.TicksLeft;

                    float t = strike.TicksTotal > 1 ? landed / (float)(strike.TicksTotal - 1) : 0f;
                    float2 at = math.lerp(strike.From, strike.To, t);

                    if (strike.Lethal || strike.StunSeconds > 0f)
                    {
                        for (int shot = 0; shot < strike.PerTick; shot++)
                            Land(strike, at);
                    }

                    Shove(strike, at, last: strike.TicksLeft == 1);

                    strike.TicksLeft--;
                    strike.Timer += math.max(strike.Interval, 1e-3f);
                }

                if (strike.TicksLeft <= 0)
                    m_Strikes.RemoveAt(i);
                else
                    m_Strikes[i] = strike;
            }

            for (int i = m_Flashes.Count - 1; i >= 0; i--)
            {
                Flash flash = m_Flashes[i];
                flash.Life -= deltaTime;

                if (flash.Life <= 0f)
                    m_Flashes.RemoveAt(i);
                else
                    m_Flashes[i] = flash;
            }
        }

        public void CollectMarks(List<Mark> into, bool cursorOnMap)
        {
            for (int i = 0; i < m_Flashes.Count; i++)
            {
                Flash flash = m_Flashes[i];

                into.Add(new Mark
                {
                    From = flash.Position,
                    To = flash.Position,
                    Radius = flash.Radius,
                    Kind = flash.Kind,
                    Brightness = math.saturate(flash.Life / FlashSeconds),
                    Ring = flash.Ring,
                });
            }

            for (int i = 0; i < m_Strikes.Count; i++)
            {
                Strike strike = m_Strikes[i];

                into.Add(new Mark
                {
                    From = strike.From,
                    To = strike.To,
                    Radius = strike.Area,
                    Kind = strike.Kind,
                    Brightness = 0.75f,
                    Ring = true,
                });
            }

            if (!IsArmed || !cursorOnMap)
                return;

            AbilitySpec spec = AbilityTable.At(m_Armed);

            float radius = spec.Catches ? spec.Radius : spec.Push;

            float2 from = m_Cursor;
            float2 to = m_Cursor;

            if (spec.IsLine && m_Drawing)
            {
                float2 offset = m_Cursor - m_LineFrom;
                float length = math.length(offset);

                from = m_LineFrom;
                to = length > 1e-4f
                    ? m_LineFrom + offset / length * math.min(length, spec.Length)
                    : m_LineFrom;
            }

            into.Add(new Mark
            {
                From = from,
                To = to,
                Radius = radius,
                Kind = spec.Kind,
                Brightness = 1f,
                Ring = true,
            });
        }

        public void Reset()
        {
            m_Strikes.Clear();
            m_Flashes.Clear();
            m_Blasts.Clear();

            Disarm();
        }

        void Cast(in AbilitySpec spec, float2 from, float2 to)
        {
            int ticks = math.max(1, spec.Ticks);

            float2 offset = to - from;
            float length = math.length(offset);

            float2 forward = length > 1e-4f ? offset / length : float2.zero;
            float spacing = ticks > 1 ? length / (ticks - 1) * 0.5f : 0f;

            float interval = math.max(0f, spec.TickInterval);

            if (spec.IsLine && ticks > 1 && length > 1e-4f)
            {
                float speed = spec.Length / math.max(1e-3f, (ticks - 1) * spec.TickInterval);
                interval = length / speed / (ticks - 1);
            }

            m_Strikes.Add(new Strike
            {
                Kind = spec.Kind,

                Source = ++m_Casts,

                From = from,
                To = to,
                Forward = forward,
                Spacing = spacing,

                Area = spec.Radius,
                Impact = spec.Impact,
                Push = spec.Push,
                Impulse = spec.PushSpeed,
                StunSeconds = spec.StunSeconds,
                Lethal = spec.Lethal,

                PerTick = math.max(1, spec.PerTick),
                TicksLeft = ticks,
                TicksTotal = ticks,
                Interval = interval,

                Timer = 0f,
            });
        }

        void Land(in Strike strike, float2 centre)
        {
            bool scattered = strike.Impact > 0f;

            float radius = scattered ? strike.Impact : strike.Area;
            float2 at = scattered ? Scatter(strike, centre) : centre;

            m_Blasts.Add(new Blast
            {
                Source = strike.Source,
                Position = at,
                Radius = radius,

                PushRadius = radius,

                StunSeconds = strike.StunSeconds,
                Lethal = strike.Lethal,
            });

            m_Flashes.Add(new Flash
            {
                Position = at,
                Radius = radius,
                Kind = strike.Kind,
                Life = FlashSeconds,

                Ring = !scattered,
            });
        }

        void Shove(in Strike strike, float2 centre, bool last)
        {
            if (strike.Impulse <= 0f)
                return;

            float reach = strike.Area + strike.Push;

            m_Blasts.Add(new Blast
            {
                Source = strike.Source,
                Position = centre,

                Radius = 0f,

                PushInner = last ? 0f : strike.Area,
                PushRadius = reach,

                PushBehind = last ? float2.zero : strike.Forward,

                Impulse = strike.Impulse,
            });

            if (strike.Lethal || strike.StunSeconds > 0f)
                return;

            m_Flashes.Add(new Flash
            {
                Position = centre,
                Radius = reach,
                Kind = strike.Kind,
                Life = FlashSeconds,
                Ring = true,
            });
        }

        float2 Scatter(in Strike strike, float2 centre)
        {
            float across = math.max(0f, strike.Area - strike.Impact);

            if (strike.Spacing > 0f)
            {
                var side = new float2(-strike.Forward.y, strike.Forward.x);

                return centre +
                       strike.Forward * m_Random.NextFloat(-strike.Spacing, strike.Spacing) +
                       side * m_Random.NextFloat(-across, across);
            }

            float angle = m_Random.NextFloat(0f, 2f * math.PI);
            float distance = across * math.sqrt(m_Random.NextFloat());

            math.sincos(angle, out float sin, out float cos);
            return centre + new float2(cos, sin) * distance;
        }
    }
}
