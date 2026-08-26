using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace FluidCrowd.Demo
{
    public struct DrawInstance
    {
        public float4 Body;

        public float4 Facing;

        public static DrawInstance Round(float2 centre, float2 size, float2 facing, float tint) =>
            new DrawInstance
            {
                Body = new float4(centre, size),
                Facing = new float4(facing, tint, 0f),
            };

        public static DrawInstance Ring(float2 centre, float2 size, float2 facing, float tint, float wall) =>
            new DrawInstance
            {
                Body = new float4(centre, size),
                Facing = new float4(facing, tint, -math.max(1e-3f, wall)),
            };

        public static DrawInstance Block(float2 centre, float2 size, float tint) => new DrawInstance
        {
            Body = new float4(centre, size),
            Facing = new float4(1f, 0f, tint, 1f),
        };
    }

    public sealed class InstanceRenderer : IDisposable
    {
        static readonly int InstancesId = Shader.PropertyToID("_Instances");
        static readonly int PaletteId = Shader.PropertyToID("_Palette");
        static readonly int MinBrightnessId = Shader.PropertyToID("_MinBrightness");
        static readonly int DepthId = Shader.PropertyToID("_Depth");
        static readonly int OutlineId = Shader.PropertyToID("_Outline");

        public const int PaletteSize = 8;

        const int BufferCount = 3;

        readonly Material m_Material;
        readonly Mesh m_Mesh;
        readonly GraphicsBuffer[] m_InstanceBuffers = new GraphicsBuffer[BufferCount];
        readonly GraphicsBuffer m_CommandBuffer;
        readonly GraphicsBuffer.IndirectDrawIndexedArgs[] m_Commands =
            new GraphicsBuffer.IndirectDrawIndexedArgs[1];

        RenderParams m_RenderParams;
        int m_BufferIndex;
        bool m_Disposed;

        public InstanceRenderer(
            Material sourceMaterial, int capacity, Bounds worldBounds, Color[] palette,
            float minBrightness, float depth = 0f, float outline = 0f)
        {
            if (sourceMaterial == null)
                throw new ArgumentNullException(nameof(sourceMaterial));

            m_Material = new Material(sourceMaterial) { hideFlags = HideFlags.HideAndDontSave };

            var colours = new Vector4[PaletteSize];
            for (int i = 0; i < PaletteSize; i++)
            {
                Color colour = palette != null && i < palette.Length ? palette[i] : Color.magenta;
                colours[i] = new Vector4(colour.r, colour.g, colour.b, colour.a);
            }

            m_Material.SetVectorArray(PaletteId, colours);
            m_Material.SetFloat(MinBrightnessId, minBrightness);
            m_Material.SetFloat(DepthId, depth);
            m_Material.SetFloat(OutlineId, outline);

            m_Mesh = CreateUnitQuad();

            for (int i = 0; i < BufferCount; i++)
                m_InstanceBuffers[i] = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, math.max(1, capacity),
                    UnsafeUtility.SizeOf<DrawInstance>());

            m_CommandBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);

            m_Commands[0].indexCountPerInstance = m_Mesh.GetIndexCount(0);
            m_Commands[0].startIndex = m_Mesh.GetIndexStart(0);
            m_Commands[0].baseVertexIndex = m_Mesh.GetBaseVertex(0);

            m_RenderParams = new RenderParams(m_Material)
            {
                worldBounds = worldBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                renderingLayerMask = 1,
                layer = 0,
            };
        }

        public void Render(NativeArray<DrawInstance> instances, int count)
        {
            if (count <= 0)
                return;

            m_BufferIndex = (m_BufferIndex + 1) % BufferCount;
            GraphicsBuffer buffer = m_InstanceBuffers[m_BufferIndex];

            buffer.SetData(instances, 0, 0, count);
            m_Material.SetBuffer(InstancesId, buffer);

            m_Commands[0].instanceCount = (uint)count;
            m_CommandBuffer.SetData(m_Commands);

            Graphics.RenderMeshIndirect(m_RenderParams, m_Mesh, m_CommandBuffer);
        }

        static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh
            {
                name = "TintedQuad",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f),
                },
            };

            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            for (int i = 0; i < BufferCount; i++)
                m_InstanceBuffers[i]?.Dispose();

            m_CommandBuffer?.Dispose();

            if (m_Mesh != null) UnityEngine.Object.Destroy(m_Mesh);
            if (m_Material != null) UnityEngine.Object.Destroy(m_Material);

            m_Disposed = true;
        }
    }
}
