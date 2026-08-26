using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct CrowdDrawJob : IJobParallelFor
    {
        public float2 Shape;

        public float Darkest;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<float> Speeds;
        [ReadOnly] public NativeArray<float> Stun;

        [WriteOnly] public NativeArray<DrawInstance> Instances;

        public void Execute(int index)
        {
            float3 stored = Positions[index];

            math.sincos(stored.z, out float sin, out float cos);

            float pace = math.saturate(
                math.length(Velocity[index]) / math.max(1e-4f, Speeds[index]));

            bool frozen = Stun[index] > 0f;

            float shade = frozen ? 0.95f : math.lerp(Darkest, 1f, pace);
            float palette = frozen ? 1f : 0f;

            float diameter = Radii[index] * 2f;

            Instances[index] = new DrawInstance
            {
                Body = new float4(stored.xy, diameter * Shape),

                Facing = new float4(cos, sin, palette + math.min(shade, 0.999f), 0f),
            };
        }
    }

    public sealed class CrowdRenderer : IDisposable
    {
        public enum Overlay
        {
            None = 0,
            Density = 1,
            Pressure = 2,
        }

        const int MarkCapacity = 4096;

        const float Stroke = 0.06f;
        const float ThinnestPixels = 2.5f;

        const float OverlayFloor = 0.04f;

        static readonly Color[] GroundPalette =
        {
            new Color(0.055f, 0.062f, 0.078f),
            new Color(0.150f, 0.165f, 0.195f),
            new Color(0.130f, 0.230f, 0.170f),
            new Color(0.280f, 0.190f, 0.070f),
            new Color(0.180f, 0.850f, 0.800f),
        };

        static readonly Color[] BodyPalette =
        {
            new Color(0.870f, 0.790f, 0.640f),
            new Color(0.420f, 0.780f, 1.000f),
        };

        static readonly Color[] MarkPalette =
        {
            new Color(0.055f, 0.062f, 0.078f),
            new Color(0.850f, 0.900f, 0.550f),
            new Color(1.000f, 0.550f, 0.200f),
            new Color(0.700f, 0.800f, 0.950f),
            new Color(0.780f, 0.560f, 1.000f),
            new Color(0.450f, 0.900f, 1.000f),
            new Color(1.000f, 0.930f, 0.480f),
        };

        public static Color AbilityColour(int slot) => MarkPalette[slot + 1];

        readonly InstanceRenderer m_Ground;
        readonly InstanceRenderer m_Marks;
        readonly InstanceRenderer m_Bodies;

        NativeArray<DrawInstance> m_GroundInstances;
        NativeArray<DrawInstance> m_MarkInstances;
        NativeArray<DrawInstance> m_BodyInstances;

        readonly NavGrid m_FieldGrid;
        bool m_Disposed;

        public Color Background => GroundPalette[0];

        public CrowdRenderer(Material material, CrowdSimulation simulation)
        {
            Arena arena = simulation.Arena;
            m_FieldGrid = simulation.Continuum.Grid;

            float2 size = arena.Size;
            var bounds = new Bounds(Vector3.zero, new Vector3(size.x, size.y, 1f));

            int ground = 3 + m_FieldGrid.CellCount + arena.Rock.Length;

            m_Ground = new InstanceRenderer(
                material, ground, bounds, GroundPalette, minBrightness: 0f, depth: 0.3f);

            m_Marks = new InstanceRenderer(
                material, MarkCapacity, bounds, MarkPalette, minBrightness: 0f, depth: -0.1f);

            m_Bodies = new InstanceRenderer(
                material, simulation.Capacity, bounds, BodyPalette, minBrightness: 0.2f,
                outline: 0.055f);

            m_GroundInstances = new NativeArray<DrawInstance>(ground, Allocator.Persistent);
            m_MarkInstances = new NativeArray<DrawInstance>(MarkCapacity, Allocator.Persistent);
            m_BodyInstances = new NativeArray<DrawInstance>(
                simulation.Capacity, Allocator.Persistent);
        }

        public void Draw(
            CrowdSimulation simulation, List<Mark> marks, Overlay overlay, in CrowdTuning tuning,
            float unitsPerPixel)
        {
            m_Ground.Render(m_GroundInstances, PaintGround(simulation, overlay, tuning));
            m_Marks.Render(m_MarkInstances, PaintMarks(marks, unitsPerPixel));

            int count = simulation.Population;
            if (count <= 0)
                return;

            new CrowdDrawJob
            {
                Shape = new float2(1.45f, 0.9f),
                Darkest = 0.3f,
                Positions = simulation.Positions,
                Velocity = simulation.Velocity,
                Radii = simulation.Radii,
                Speeds = simulation.Speeds,
                Stun = simulation.Stun,
                Instances = m_BodyInstances,
            }.Schedule(count, 256).Complete();

            m_Bodies.Render(m_BodyInstances, count);
        }

        static float Solid(int palette) => palette + 0.999f;

        int PaintGround(CrowdSimulation simulation, Overlay overlay, in CrowdTuning tuning)
        {
            Arena arena = simulation.Arena;
            int at = 0;

            m_GroundInstances[at++] = DrawInstance.Block(
                (arena.Min + arena.Max) * 0.5f, arena.Size, Solid(0));

            m_GroundInstances[at++] = DrawInstance.Block(
                arena.Spawn.Centre, arena.Spawn.Size, Solid(2));

            m_GroundInstances[at++] = DrawInstance.Block(
                arena.Goal.Centre, arena.Goal.Size, Solid(3));

            if (overlay != Overlay.None)
                at = PaintField(simulation, overlay, tuning, at);

            for (int i = 0; i < arena.Rock.Length; i++)
                m_GroundInstances[at++] = DrawInstance.Block(
                    arena.Rock[i].Centre, arena.Rock[i].Size, Solid(1));

            return at;
        }

        int PaintField(
            CrowdSimulation simulation, Overlay overlay, in CrowdTuning tuning, int at)
        {
            CrowdContinuum continuum = simulation.Continuum;

            float cell = m_FieldGrid.CellSize;
            var size = new float2(cell, cell);

            float scale = overlay == Overlay.Density
                ? 1f / math.max(1e-3f, tuning.RestDensity)
                : 1f / math.max(1e-3f, tuning.MaxPressurePush);

            for (int i = 0; i < m_FieldGrid.CellCount; i++)
            {
                float raw = overlay == Overlay.Density
                    ? continuum.Density[i]
                    : math.length(continuum.Correction[i]);

                float shade = math.saturate(raw * scale);

                if (shade < OverlayFloor)
                    continue;

                m_GroundInstances[at++] = DrawInstance.Block(
                    m_FieldGrid.CellCenter(i), size, 4f + math.min(shade, 0.999f));
            }

            return at;
        }

        int PaintMarks(List<Mark> marks, float unitsPerPixel)
        {
            int at = 0;

            for (int i = 0; i < marks.Count && at < MarkCapacity; i++)
            {
                Mark mark = marks[i];

                float tint = (int)mark.Kind + 1f + math.min(mark.Brightness, 0.999f);

                float2 offset = mark.To - mark.From;
                float length = math.length(offset);

                float2 facing = length > 1e-4f ? offset / length : new float2(1f, 0f);
                float2 centre = (mark.From + mark.To) * 0.5f;
                var size = new float2(length + mark.Radius * 2f, mark.Radius * 2f);

                if (!mark.Ring)
                {
                    m_MarkInstances[at++] = DrawInstance.Round(centre, size, facing, tint);
                    continue;
                }

                float wall = math.min(
                    math.max(mark.Radius * Stroke, unitsPerPixel * ThinnestPixels),
                    mark.Radius * 0.8f);

                m_MarkInstances[at++] = DrawInstance.Ring(centre, size, facing, tint, wall);
            }

            return at;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Ground?.Dispose();
            m_Marks?.Dispose();
            m_Bodies?.Dispose();

            if (m_GroundInstances.IsCreated) m_GroundInstances.Dispose();
            if (m_MarkInstances.IsCreated) m_MarkInstances.Dispose();
            if (m_BodyInstances.IsCreated) m_BodyInstances.Dispose();

            m_Disposed = true;
        }
    }
}
