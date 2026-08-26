using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    [AddComponentMenu("Fluid Crowd/Crowd Demo")]
    public sealed class CrowdDemo : MonoBehaviour
    {
        [Header("The map")]
        [Tooltip("The arena the crowd walks through. Left empty, the one in the scene is used.")]
        [SerializeField] CrowdArena m_Map;

        [Header("The release")]
        [Tooltip("How many bodies there are to let out, in total. The map starts empty, fills as " +
                 "they walk in, and empties again once the last one is through.")]
        [SerializeField] int m_Budget = 100_000;

        [Tooltip("Ceiling on how many can be on the map at once. Reaching it holds the release " +
                 "back, the same way a full mouth does.")]
        [SerializeField] int m_Capacity = 100_000;

        [Tooltip("Bodies per second out of the mouth, when there is room for them. Well above " +
                 "what the gate passes, on purpose — the crowd is supposed to back up behind it.")]
        [SerializeField] float m_ReleaseRate = 400f;

        [Header("Bodies")]
        [SerializeField] float m_BodyRadius = 0.2f;
        [SerializeField] float m_WalkSpeed = 6f;

        [Header("Grids")]
        [Tooltip("Flow field, clearance and wall distance resolution. Build cost is O(cells), and " +
                 "it is also how sharply a corner is felt: the wall field is interpolated, so a " +
                 "flat wall is exact at any resolution and a corner is rounded off by about a cell.")]
        [SerializeField] float m_NavCellSize = 0.5f;

        [Tooltip("Crowd field resolution. Has to hold several bodies — a cell finer than a body " +
                 "measures its own quantisation instead of a density.")]
        [SerializeField] float m_CrowdCellSize = 1.2f;

        [Header("Rendering")]
        [Tooltip("Left empty, the one in Resources is used. It only has to be a material on the " +
                 "FluidCrowd/Body shader.")]
        [SerializeField] Material m_BodyMaterial;

        Arena m_Arena;
        CrowdSimulation m_Simulation;
        CrowdRenderer m_Renderer;
        AbilityCaster m_Caster;
        DemoHud m_Hud;
        DemoCamera m_Camera;

        readonly List<Mark> m_Marks = new List<Mark>(256);

        CrowdTuning m_Tuning = CrowdTuning.Default();

        float2 m_Cursor;
        bool m_CursorOnMap;

        int m_Taken;
        int m_Arrived;

        public CrowdSimulation Simulation => m_Simulation;
        public AbilityCaster Caster => m_Caster;

        public int Taken => m_Taken;
        public int Arrived => m_Arrived;

        void Awake()
        {
            CrowdArena map = m_Map != null ? m_Map : FindAnyObjectByType<CrowdArena>();

            if (map == null)
            {
                Debug.LogError(
                    "[FluidCrowd] No Crowd Arena in the scene. The map is built from one, out of " +
                    "the Map Boxes underneath it.", this);
                enabled = false;
                return;
            }

            m_Arena = map.Build();

            if (m_Arena == null)
            {
                enabled = false;
                return;
            }

            m_Simulation = new CrowdSimulation(
                m_Arena, m_Capacity, m_Budget, m_NavCellSize, m_CrowdCellSize, m_BodyRadius,
                m_WalkSpeed);

            Material material = m_BodyMaterial != null
                ? m_BodyMaterial
                : Resources.Load<Material>("Body");

            if (material == null)
            {
                Debug.LogError(
                    "[FluidCrowd] No body material. Expected one on the FluidCrowd/Body shader " +
                    "at Assets/Resources/Body.mat, or one dropped into the component's slot.");
                enabled = false;
                return;
            }

            m_Renderer = new CrowdRenderer(material, m_Simulation);
            m_Caster = new AbilityCaster();
            m_Hud = new DemoHud();

            BuildCamera();
        }

        void BuildCamera()
        {
            var holder = new GameObject("Crowd Camera");
            holder.transform.SetParent(transform, worldPositionStays: false);

            m_Camera = new DemoCamera(holder.AddComponent<Camera>(), m_Arena)
            {
                Background = m_Renderer.Background,
            };
        }

        void Update()
        {
            m_Camera.Update();
            ReadCursor();
            HandleInput();

            float deltaTime = math.min(Time.deltaTime, 1f / 30f);

            m_Caster.Tick(deltaTime);
            m_Simulation.Step(deltaTime, m_ReleaseRate, m_Caster.Blasts, m_Tuning);

            m_Taken += m_Simulation.Taken;
            m_Arrived += m_Simulation.Arrived;

            m_Marks.Clear();
            m_Caster.CollectMarks(m_Marks, m_CursorOnMap);

            m_Renderer.Draw(
                m_Simulation, m_Marks, CrowdRenderer.Overlay.None, m_Tuning,
                m_Camera.UnitsPerPixel);
        }

        void ReadCursor()
        {
            m_Cursor = m_Camera.ScreenToWorld(Input.mousePosition);
            m_CursorOnMap = math.all(m_Cursor >= m_Arena.Min) && math.all(m_Cursor <= m_Arena.Max);

            m_Caster.Aim(m_Cursor);
        }

        void HandleInput()
        {
            for (int slot = 0; slot < AbilityTable.Count; slot++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + slot))
                    m_Caster.Arm(slot);
            }

            if (m_CursorOnMap && Input.GetMouseButtonDown(0))
                m_Caster.Press(m_Cursor);

            if (Input.GetMouseButtonUp(0))
            {
                if (m_CursorOnMap)
                    m_Caster.Release(m_Cursor);
                else
                    m_Caster.Cancel();
            }

            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
                m_Caster.Disarm();
        }

        void OnGUI()
        {
            m_Hud?.Draw(this);
        }

        void OnDestroy()
        {
            m_Renderer?.Dispose();
            m_Simulation?.Dispose();
        }
    }
}
