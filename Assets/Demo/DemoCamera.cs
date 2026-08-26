using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    public sealed class DemoCamera
    {
        const float MinZoom = 0.8f;
        const float MaxZoom = 14f;

        const float ZoomRate = 0.16f;

        readonly Camera m_Camera;
        readonly Arena m_Arena;

        float m_Zoom = 1f;
        float2 m_Pan;
        Vector3 m_Dragging;

        public DemoCamera(Camera camera, Arena arena)
        {
            m_Camera = camera;
            m_Arena = arena;

            m_Camera.orthographic = true;
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.nearClipPlane = 0.1f;
            m_Camera.farClipPlane = 40f;

            Apply();
        }

        public Color Background
        {
            set => m_Camera.backgroundColor = value;
        }

        public float UnitsPerPixel =>
            m_Camera.orthographicSize * 2f / math.max(1, Screen.height);

        public float2 ScreenToWorld(Vector3 screen)
        {
            Vector3 world = m_Camera.ScreenToWorldPoint(
                new Vector3(screen.x, screen.y, -m_Camera.transform.position.z));

            return new float2(world.x, world.y);
        }

        public void Update()
        {
            Drag();
            Wheel();

            if (Input.GetKeyDown(KeyCode.Z))
                Fit();

            Apply();
        }

        public void Fit()
        {
            m_Zoom = 1f;
            m_Pan = float2.zero;
        }

        void Drag()
        {
            if (Input.GetMouseButtonDown(2))
                m_Dragging = Input.mousePosition;

            if (!Input.GetMouseButton(2))
                return;

            Vector3 now = Input.mousePosition;

            float perPixel = m_Camera.orthographicSize * 2f / math.max(1, Screen.height);

            m_Pan -= new float2(now.x - m_Dragging.x, now.y - m_Dragging.y) * perPixel;
            m_Dragging = now;
        }

        void Wheel()
        {
            float scroll = Input.mouseScrollDelta.y;

            if (math.abs(scroll) < 1e-4f)
                return;

            float2 before = ScreenToWorld(Input.mousePosition);

            m_Zoom = math.clamp(m_Zoom * math.exp(scroll * ZoomRate), MinZoom, MaxZoom);
            Apply();

            m_Pan += before - ScreenToWorld(Input.mousePosition);
        }

        void Apply()
        {
            float aspect = math.max(0.1f, m_Camera.aspect);
            float2 view = m_Arena.ViewSize * 0.5f;

            float size = math.max(view.y, view.x / aspect) * 1.02f / m_Zoom;

            float2 half = new float2(size * aspect, size);
            float2 slack = math.max(0f, m_Arena.Size * 0.5f - half);

            m_Pan = math.clamp(m_Pan, -slack, slack);

            float2 centre = (m_Arena.Min + m_Arena.Max) * 0.5f + m_Pan;

            m_Camera.orthographicSize = size;
            m_Camera.transform.position = new Vector3(centre.x, centre.y, -10f);
        }
    }
}
