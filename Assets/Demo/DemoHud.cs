using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    public sealed class DemoHud
    {
        const float DesignHeight = 720f;

        const float LargestScale = 2.2f;

        const float Margin = 14f;
        const float PanelWidth = 230f;
        const float PanelHeight = 100f;
        const float LabelWidth = 108f;
        const float SlotWidth = 118f;
        const float SlotHeight = 44f;
        const float SlotGap = 7f;

        static readonly Color Panel = new Color(0.02f, 0.025f, 0.035f, 0.82f);
        static readonly Color Rule = new Color(1f, 1f, 1f, 0.09f);
        static readonly Color Dim = new Color(0.62f, 0.66f, 0.72f);
        static readonly Color Bright = new Color(0.96f, 0.97f, 1f);
        static readonly Color Warn = new Color(1f, 0.55f, 0.35f);

        float m_Width;
        float m_Height;

        Texture2D m_Fill;
        GUIStyle m_Label;
        GUIStyle m_Strong;
        GUIStyle m_Slot;
        GUIStyle m_SlotKey;

        double m_FrameMs;

        public void Draw(CrowdDemo demo)
        {
            EnsureStyles();

            m_FrameMs += (Time.unscaledDeltaTime * 1000.0 - m_FrameMs) * 0.05;

            float scale = math.clamp(Screen.height / DesignHeight, 1f, LargestScale);

            m_Width = Screen.width / scale;
            m_Height = Screen.height / scale;

            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            DrawCounters(demo);
            DrawBar(demo, m_Height - Margin - SlotHeight);

            GUI.matrix = previous;
        }

        void DrawCounters(CrowdDemo demo)
        {
            var area = new Rect(Margin, Margin, PanelWidth, PanelHeight);
            Box(area);

            GUILayout.BeginArea(
                new Rect(area.x + 12f, area.y + 10f, area.width - 24f, area.height - 20f));

            Line("on the map", $"{demo.Simulation.Population:N0}", Bright);
            Line("walked out", $"{demo.Arrived:N0}", Dim);
            Line("taken", $"{demo.Taken:N0}", demo.Taken > 0 ? Warn : Dim);

            Divider();

            double fps = m_FrameMs > 0.01 ? 1000.0 / m_FrameMs : 0.0;

            Line("fps", $"{fps:F0}", fps < 30.0 ? Warn : Bright);

            GUILayout.EndArea();
        }

        void DrawBar(CrowdDemo demo, float top)
        {
            AbilityCaster caster = demo.Caster;

            float total = AbilityTable.Count * SlotWidth + (AbilityTable.Count - 1) * SlotGap;
            float left = (m_Width - total) * 0.5f;

            for (int slot = 0; slot < AbilityTable.Count; slot++)
            {
                AbilitySpec spec = AbilityTable.At(slot);
                Color colour = CrowdRenderer.AbilityColour(slot);

                var rect = new Rect(left + slot * (SlotWidth + SlotGap), top, SlotWidth, SlotHeight);

                Box(rect);

                Fill(new Rect(rect.x, rect.y, 3f, rect.height), colour);

                if (caster.Armed == slot)
                    Outline(rect, colour);

                m_Slot.normal.textColor = Bright;

                GUI.Label(new Rect(rect.x + 12f, rect.y + 6f, rect.width - 16f, 18f), spec.Name, m_Slot);

                GUI.Label(
                    new Rect(rect.x + 12f, rect.y + 24f, rect.width - 16f, 16f), Hint(spec), m_SlotKey);

                GUI.Label(new Rect(rect.xMax - 22f, rect.y + 5f, 16f, 18f), $"{slot + 1}", m_SlotKey);
            }
        }

        static string Hint(in AbilitySpec spec)
        {
            if (spec.IsLine)
                return "drag a line";

            return spec.Lethal ? "click to drop" : "click to place";
        }

        void Line(string name, string value, Color colour)
        {
            GUILayout.BeginHorizontal();

            m_Label.normal.textColor = Dim;
            GUILayout.Label(name, m_Label, GUILayout.Width(LabelWidth));

            m_Strong.normal.textColor = colour;
            GUILayout.Label(value, m_Strong);

            GUILayout.EndHorizontal();
        }

        void Divider()
        {
            GUILayout.Space(5f);
            Fill(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), Rule);
            GUILayout.Space(5f);
        }

        void Box(Rect rect)
        {
            Fill(rect, Panel);
            Outline(rect, Rule);
        }

        void Outline(Rect rect, Color colour)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), colour);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), colour);
        }

        void Fill(Rect rect, Color colour)
        {
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, m_Fill);
            GUI.color = previous;
        }

        void EnsureStyles()
        {
            if (m_Fill != null)
                return;

            m_Fill = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            m_Fill.SetPixel(0, 0, Color.white);
            m_Fill.Apply();

            m_Label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = false,
                padding = new RectOffset(0, 0, 1, 1),
                margin = new RectOffset(0, 0, 0, 0),
            };

            m_Strong = new GUIStyle(m_Label) { fontStyle = FontStyle.Bold };
            m_Slot = new GUIStyle(m_Label) { fontSize = 14, fontStyle = FontStyle.Bold };
            m_SlotKey = new GUIStyle(m_Label) { fontSize = 12 };
            m_SlotKey.normal.textColor = Dim;
        }
    }
}
