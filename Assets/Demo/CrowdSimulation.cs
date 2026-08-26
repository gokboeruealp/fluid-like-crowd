using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace FluidCrowd.Demo
{
    public struct CrowdTuning
    {
        public int SolverIterations;
        public float Relaxation;

        public bool Pressure;

        public float RestDensity;
        public float FreeSurface;
        public int PressureIterations;
        public float PressureStiffness;
        public float MaxPressurePush;
        public float Viscosity;

        public bool Contacts;

        public float Responsiveness;
        public float LaneSpread;
        public float LaneRoomCells;
        public float Wander;
        public float WanderRate;
        public float GaitDrift;
        public float GaitRate;

        public bool DensityAwareFlow;
        public int FlowRebuildInterval;
        public float FlowDensityWeight;

        public static CrowdTuning Default() => new CrowdTuning
        {
            SolverIterations = 3,
            Relaxation = 1.5f,

            Pressure = true,
            RestDensity = 0.84f,
            FreeSurface = 0.7f,
            PressureIterations = 20,
            PressureStiffness = 0.3f,
            MaxPressurePush = 16f,
            Viscosity = 4f,

            Contacts = true,

            Responsiveness = 6f,
            LaneSpread = 0.45f,
            LaneRoomCells = 3f,
            Wander = 0.3f,
            WanderRate = 0.35f,
            GaitDrift = 0.15f,
            GaitRate = 0.5f,

            DensityAwareFlow = false,

            FlowRebuildInterval = 45,
            FlowDensityWeight = 0.25f,
        };
    }

    public sealed class CrowdSimulation : IDisposable
    {
        const float SizeJitter = 0.1f;
        const float SpeedJitter = 0.12f;

        public const byte ArrivedAtGoal = 1;
        public const byte TakenByBlast = 2;

        public readonly Arena Arena;
        public readonly int Capacity;

        public readonly int Budget;
        public readonly float BodyRadius;
        public readonly float WalkSpeed;

        public readonly NavGrid Grid;
        public readonly FlowField Flow;
        public readonly WallField Wall;
        public readonly CrowdContinuum Continuum;
        public readonly SpatialHash Hash;

        public NativeArray<float3> Positions;
        public NativeArray<float2> Velocity;
        public NativeArray<float> Radii;
        public NativeArray<float> Speeds;
        public NativeArray<float> Stun;

        NativeArray<float2> m_Previous;
        NativeArray<float2> m_Delta;
        NativeArray<float> m_Lane;
        NativeArray<float> m_Overlap;
        NativeArray<float> m_Buried;
        NativeArray<int> m_LastBlast;
        NativeArray<byte> m_Retired;

        readonly float2 m_FieldMin;
        readonly float2 m_FieldMax;
        readonly int m_WalkableCells;
        readonly Stopwatch m_Timer = new Stopwatch();

        int m_Population;

        int m_Released;

        float m_Backlog;
        int m_Fresh;

        Unity.Mathematics.Random m_Mouth = Unity.Mathematics.Random.CreateFromIndex(9161u);

        uint m_Frame;
        float m_SimTime;
        bool m_PressureLive = true;

        bool m_DensityFlow;
        int m_FlowInterval = 1;
        float m_FlowWeight;
        bool m_Disposed;

        public float WorstOverlap { get; private set; }

        public float WorstBuried { get; private set; }

        public int Taken { get; private set; }
        public int Arrived { get; private set; }

        public int Released => m_Released;
        public int Waiting => Budget - m_Released;

        public bool Finished => m_Released >= Budget && m_Population == 0;

        public double PressureMs { get; private set; }
        public double WalkMs { get; private set; }
        public double HashMs { get; private set; }
        public double ContactMs { get; private set; }
        public double FlowMs { get; private set; }

        public int Population => m_Population;

        public float MaxRadius => BodyRadius * (1f + SizeJitter);

        public float Saturation
        {
            get
            {
                float floor = m_WalkableCells * Grid.CellSize * Grid.CellSize;
                if (floor <= 0f)
                    return 0f;

                return m_Population * math.PI * BodyRadius * BodyRadius / floor;
            }
        }

        static int Workers => math.max(1, JobsUtility.JobWorkerCount + 1);

        public CrowdSimulation(Arena arena, int capacity, int budget, float navCell, float crowdCell,
            float bodyRadius, float walkSpeed)
        {
            Arena = arena;
            Capacity = capacity;
            Budget = math.min(budget, capacity);
            BodyRadius = bodyRadius;
            WalkSpeed = walkSpeed;

            m_FieldMin = arena.Min;
            m_FieldMax = arena.Max;

            Grid = NavGrid.Create(m_FieldMin, m_FieldMax, navCell);
            Flow = new FlowField(Grid, Allocator.Persistent);
            Wall = new WallField(Grid, Allocator.Persistent);

            Continuum = new CrowdContinuum(
                NavGrid.Create(m_FieldMin, m_FieldMax, math.max(crowdCell, 2f * MaxRadius)),
                Workers, Allocator.Persistent);

            Hash = new SpatialHash(
                NavGrid.Create(m_FieldMin, m_FieldMax, 2f * MaxRadius), capacity, Workers,
                Allocator.Persistent);

            Allocate(capacity);

            m_WalkableCells = arena.Paint(Flow.Cost, Grid);
            arena.MarkGoal(Flow, Grid);

            RebuildGeometry();
            RebuildFlow(0f);
            ReportOrphans();
        }

        void ReportOrphans()
        {
            int orphans = 0;

            for (int i = 0; i < Flow.Integration.Length; i++)
            {
                if (Flow.Cost[i] != FlowField.Blocked && Flow.Integration[i] >= float.MaxValue)
                    orphans++;
            }

            if (orphans == 0)
                return;

            UnityEngine.Debug.LogWarning(
                $"[FluidCrowd] {orphans} walkable cells cannot reach the goal — " +
                $"{orphans * Grid.CellSize * Grid.CellSize:F0} square units of the map are sealed off. " +
                "Nothing is seeded in them, but nothing can leave them either.");
        }

        void Allocate(int capacity)
        {
            Positions = new NativeArray<float3>(capacity, Allocator.Persistent);
            Velocity = new NativeArray<float2>(capacity, Allocator.Persistent);
            Radii = new NativeArray<float>(capacity, Allocator.Persistent);
            Speeds = new NativeArray<float>(capacity, Allocator.Persistent);
            Stun = new NativeArray<float>(capacity, Allocator.Persistent);

            m_Previous = new NativeArray<float2>(capacity, Allocator.Persistent);
            m_Delta = new NativeArray<float2>(capacity, Allocator.Persistent);
            m_Lane = new NativeArray<float>(capacity, Allocator.Persistent);
            m_Overlap = new NativeArray<float>(capacity, Allocator.Persistent);
            m_Buried = new NativeArray<float>(capacity, Allocator.Persistent);
            m_LastBlast = new NativeArray<int>(capacity, Allocator.Persistent);
            m_Retired = new NativeArray<byte>(capacity, Allocator.Persistent);
        }

        public void Release(float deltaTime, float perSecond)
        {
            m_Backlog = math.min(m_Backlog + perSecond * deltaTime, perSecond * deltaTime + 1f);

            int room = math.min(Budget - m_Released, Capacity - m_Population);

            m_Fresh = 0;

            while (m_Backlog >= 1f && m_Fresh < room)
            {
                if (!TryFindRoom(out float2 at))
                    break;

                Add(at);

                m_Backlog -= 1f;
                m_Fresh++;
            }

            m_Released += m_Fresh;
            m_Population += m_Fresh;
        }

        void Add(float2 at)
        {
            int slot = m_Population + m_Fresh;

            var random = Unity.Mathematics.Random.CreateFromIndex(
                (uint)(m_Released + m_Fresh) * 747796405u + 2891336453u);

            Positions[slot] = new float3(at, 0f);
            m_Previous[slot] = at;
            Velocity[slot] = float2.zero;
            Stun[slot] = 0f;
            m_LastBlast[slot] = 0;
            m_Retired[slot] = 0;

            Radii[slot] = BodyRadius * (1f + random.NextFloat(-SizeJitter, SizeJitter));
            Speeds[slot] = WalkSpeed * (1f + random.NextFloat(-SpeedJitter, SpeedJitter));

            m_Lane[slot] = random.NextFloat(-1f, 1f);
        }

        bool TryFindRoom(out float2 at)
        {
            float2 min = Arena.Spawn.Min;
            float2 max = Arena.Spawn.Max;

            float clearance = MaxRadius * 2f + 0.25f;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                at = m_Mouth.NextFloat2(min, max);

                if (Occupied(at, clearance))
                    continue;

                bool clash = false;

                for (int i = 0; i < m_Fresh && !clash; i++)
                    clash = math.distancesq(Positions[m_Population + i].xy, at) < clearance * clearance;

                if (!clash)
                    return true;
            }

            at = default;
            return false;
        }

        bool Occupied(float2 at, float clearance)
        {
            if (m_Population == 0)
                return false;

            NavGrid grid = Hash.Grid;

            int reach = (int)math.ceil(clearance / grid.CellSize);
            int2 centre = grid.WorldToCoord(at);

            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int2 coord = centre + new int2(dx, dy);
                    if (!grid.InBounds(coord))
                        continue;

                    int cell = grid.Index(coord);

                    for (int slot = Hash.CellStarts[cell]; slot < Hash.CellStarts[cell + 1]; slot++)
                    {
                        int other = Hash.SortedUnits[slot];

                        if (math.distancesq(Positions[other].xy, at) < clearance * clearance)
                            return true;
                    }
                }
            }

            return false;
        }

        void Compact()
        {
            int live = m_Population;
            int index = 0;

            Arrived = 0;
            Taken = 0;

            while (index < live)
            {
                byte reason = m_Retired[index];

                if (reason == 0)
                {
                    index++;
                    continue;
                }

                if (reason == ArrivedAtGoal)
                    Arrived++;
                else
                    Taken++;

                live--;
                Move(from: live, to: index);
            }

            m_Population = live;
        }

        void Move(int from, int to)
        {
            if (from != to)
            {
                Positions[to] = Positions[from];
                Velocity[to] = Velocity[from];
                Radii[to] = Radii[from];
                Speeds[to] = Speeds[from];
                Stun[to] = Stun[from];

                m_Previous[to] = m_Previous[from];
                m_Lane[to] = m_Lane[from];
                m_LastBlast[to] = m_LastBlast[from];
                m_Retired[to] = m_Retired[from];
            }

            m_Retired[from] = 0;
        }

        public void Restart()
        {
            for (int i = 0; i < m_Population; i++)
                m_Retired[i] = 0;

            m_Population = 0;
            m_Released = 0;
            m_Fresh = 0;
            m_Backlog = 0f;
            Arrived = 0;
            Taken = 0;

            m_Mouth = Unity.Mathematics.Random.CreateFromIndex(9161u);
        }

        public void Step(
            float deltaTime, float releaseRate, IReadOnlyList<Blast> blasts, in CrowdTuning tuning)
        {
            m_Frame++;
            m_SimTime = math.fmod(m_SimTime + deltaTime, 4096f);

            Arrived = 0;
            Taken = 0;

            if (m_Population > 0)
                Advance(m_Population, deltaTime, blasts, tuning);

            Release(deltaTime, releaseRate);

            Compact();

            if (!m_DensityFlow)
                return;

            new DensityScatterJob
            {
                Grid = Grid,
                ActiveCount = m_Population,
                Positions = Positions,
                Density = Flow.Density,
            }.Schedule().Complete();

            if (m_Frame % (uint)m_FlowInterval == 0)
                RebuildFlow(m_FlowWeight);
        }

        void Advance(int count, float deltaTime, IReadOnlyList<Blast> blasts, in CrowdTuning tuning)
        {
            m_DensityFlow = tuning.DensityAwareFlow;
            m_FlowInterval = math.max(1, tuning.FlowRebuildInterval);
            m_FlowWeight = tuning.FlowDensityWeight;

            float maxSpeed = math.min(WalkSpeed * 6f, Grid.CellSize * 2f / math.max(1e-4f, deltaTime));

            ApplyBlasts(count, blasts);

            new StunDecayJob
            {
                DeltaTime = deltaTime,
                Stun = Stun,
            }.Schedule(count, 256).Complete();

            SolvePressure(count, deltaTime, tuning);

            var crowd = new CrowdSampler
            {
                Grid = Continuum.Grid,
                Correction = Continuum.Correction,
                Mean = Continuum.Mean,
            };

            var wall = new WallSampler
            {
                Grid = Wall.Grid,
                Distance = Wall.Distance,
                Normal = Wall.Normal,
            };

            m_Timer.Restart();
            new CrowdWalkJob
            {
                Grid = Grid,
                DeltaTime = deltaTime,
                Time = m_SimTime,
                FieldMin = m_FieldMin,
                FieldMax = m_FieldMax,
                LaneSpread = tuning.LaneSpread,
                LaneCells = tuning.LaneRoomCells,
                WanderSpread = tuning.Wander,
                WanderRate = tuning.WanderRate,
                GaitDrift = tuning.GaitDrift,
                GaitRate = tuning.GaitRate,
                Responsiveness = tuning.Responsiveness,
                MaxSpeed = maxSpeed,
                Cost = Flow.Cost,
                Flow = Flow.Flow,
                Clearance = Flow.Clearance,
                Speeds = Speeds,
                Stun = Stun,
                Lane = m_Lane,
                Crowd = crowd,
                Positions = Positions,
                Previous = m_Previous,
                Velocity = Velocity,
            }.Schedule(count, 256).Complete();
            WalkMs = Elapsed();

            m_Timer.Restart();
            Hash.Schedule(Positions, count).Complete();
            HashMs = Elapsed();

            m_Timer.Restart();

            float relaxation = tuning.Contacts ? tuning.Relaxation : 0f;

            for (int iteration = 0; iteration < math.max(1, tuning.SolverIterations); iteration++)
            {
                JobHandle contacts = new CrowdContactJob
                {
                    Grid = Hash.Grid,
                    MaxRadius = MaxRadius,
                    Relaxation = relaxation,
                    Positions = Positions,
                    Radii = Radii,
                    CellStarts = Hash.CellStarts,
                    SortedUnits = Hash.SortedUnits,
                    Delta = m_Delta,
                }.Schedule(count, 256);

                new CrowdProjectJob
                {
                    Wall = wall,
                    FieldMin = m_FieldMin,
                    FieldMax = m_FieldMax,
                    Delta = m_Delta,
                    Radii = Radii,
                    Positions = Positions,
                }.Schedule(count, 256, contacts).Complete();
            }

            ContactMs = Elapsed();

            new CrowdReadbackJob
            {
                DeltaTime = deltaTime,
                MaxSpeed = maxSpeed,
                Viscosity = tuning.Viscosity,

                TurnRate = 9f,
                FacingSpread = 0.25f,

                Crowd = crowd,
                Previous = m_Previous,
                Speeds = Speeds,
                Lane = m_Lane,
                Positions = Positions,
                Velocity = Velocity,
            }.Schedule(count, 256).Complete();

            Measure(count, wall);

            new CrowdArriveJob
            {
                GoalMin = Arena.Goal.Min,
                GoalMax = Arena.Goal.Max,
                Reason = ArrivedAtGoal,
                Positions = Positions,
                Retired = m_Retired,
            }.Schedule(count, 256).Complete();
        }

        void ApplyBlasts(int count, IReadOnlyList<Blast> blasts)
        {
            if (blasts == null || blasts.Count == 0)
                return;

            var array = new NativeArray<Blast>(blasts.Count, Allocator.TempJob);

            for (int i = 0; i < blasts.Count; i++)
                array[i] = blasts[i];

            new CrowdBlastJob
            {
                ReferenceRadius = BodyRadius,
                Reason = TakenByBlast,
                Blasts = array,
                Positions = Positions,
                Radii = Radii,
                LastBlast = m_LastBlast,
                Velocity = Velocity,
                Stun = Stun,
                Retired = m_Retired,
            }.Schedule(count, 256).Complete();

            array.Dispose();
        }

        void SolvePressure(int count, float deltaTime, in CrowdTuning tuning)
        {
            if (!tuning.Pressure)
            {
                if (m_PressureLive)
                {
                    for (int i = 0; i < Continuum.Correction.Length; i++)
                        Continuum.Correction[i] = float2.zero;

                    m_PressureLive = false;
                }

                PressureMs = 0.0;
                return;
            }

            m_PressureLive = true;

            m_Timer.Restart();
            Continuum.Schedule(
                Positions, Velocity, Radii, count, deltaTime, tuning.RestDensity,
                tuning.PressureIterations, tuning.PressureStiffness, tuning.FreeSurface,
                tuning.MaxPressurePush).Complete();
            PressureMs = Elapsed();
        }

        void Measure(int count, in WallSampler wall)
        {
            new CrowdOverlapJob
            {
                Grid = Hash.Grid,
                MaxRadius = MaxRadius,
                Wall = wall,
                Positions = Positions,
                Radii = Radii,
                CellStarts = Hash.CellStarts,
                SortedUnits = Hash.SortedUnits,
                Overlap = m_Overlap,
                Buried = m_Buried,
            }.Schedule(count, 256).Complete();

            float overlap = 0f;
            float buried = 0f;

            for (int i = 0; i < count; i++)
            {
                overlap = math.max(overlap, m_Overlap[i]);
                buried = math.max(buried, m_Buried[i]);
            }

            WorstOverlap = overlap;
            WorstBuried = buried;
        }

        void RebuildGeometry()
        {
            JobHandle clearance = Flow.ScheduleClearance();
            JobHandle wall = Wall.Schedule(Flow.Cost, clearance);

            Continuum.ScheduleSolid(Grid, Flow.Cost, wall).Complete();
        }

        void RebuildFlow(float densityWeight)
        {
            m_Timer.Restart();
            Flow.Schedule(densityWeight, 0.6f, 2f).Complete();
            FlowMs = Elapsed();
        }

        double Elapsed()
        {
            m_Timer.Stop();
            return m_Timer.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            Hash?.Dispose();
            Continuum?.Dispose();
            Wall?.Dispose();
            Flow?.Dispose();

            if (Positions.IsCreated) Positions.Dispose();
            if (Velocity.IsCreated) Velocity.Dispose();
            if (Radii.IsCreated) Radii.Dispose();
            if (Speeds.IsCreated) Speeds.Dispose();
            if (Stun.IsCreated) Stun.Dispose();
            if (m_Previous.IsCreated) m_Previous.Dispose();
            if (m_Delta.IsCreated) m_Delta.Dispose();
            if (m_Lane.IsCreated) m_Lane.Dispose();
            if (m_Overlap.IsCreated) m_Overlap.Dispose();
            if (m_Buried.IsCreated) m_Buried.Dispose();
            if (m_LastBlast.IsCreated) m_LastBlast.Dispose();
            if (m_Retired.IsCreated) m_Retired.Dispose();

            m_Disposed = true;
        }
    }
}
