using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    [AddComponentMenu("Fluid Crowd/Crowd Arena")]
    public sealed class CrowdArena : MonoBehaviour
    {
        [Tooltip("The simulated field, centred on this object. Nothing exists outside it and a " +
                 "body cannot leave it.")]
        [SerializeField] Vector2 m_Field = new Vector2(300f, 176f);

        [Tooltip("The part of the field the camera frames. Run it narrower than the field and " +
                 "the mouth and the exit sit in the margin, so the crowd walks into shot.")]
        [SerializeField] Vector2 m_View = new Vector2(280f, 176f);

        public Arena Build()
        {
            var rock = new List<Box>(128);

            Box mouth = default;
            Box exit = default;

            bool haveMouth = false;
            bool haveExit = false;

            MapBox[] parts = GetComponentsInChildren<MapBox>();

            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i].Part)
                {
                    case MapPart.Rock:
                        rock.Add(parts[i].ToBox());
                        break;

                    case MapPart.Mouth:
                        mouth = parts[i].ToBox();
                        haveMouth = true;
                        break;

                    case MapPart.Exit:
                        exit = parts[i].ToBox();
                        haveExit = true;
                        break;
                }
            }

            if (!haveMouth || !haveExit)
            {
                Debug.LogError(
                    $"[FluidCrowd] {name} needs a Map Box set to Mouth and one set to Exit " +
                    "under it. Without both there is nowhere to release the crowd from and " +
                    "nowhere for it to walk to.", this);

                return null;
            }

            float2 centre = new float2(transform.position.x, transform.position.y);

            float2 field = math.abs(new float2(m_Field.x, m_Field.y)) * 0.5f;
            float2 view = math.abs(new float2(m_View.x, m_View.y)) * 0.5f;

            return new Arena(
                centre - field, centre + field,
                centre - view, centre + view,
                mouth, exit, rock.ToArray());
        }

        void OnDrawGizmos()
        {
            Vector3 centre = transform.position;

            Gizmos.color = new Color(0.45f, 0.55f, 0.70f, 0.9f);
            Gizmos.DrawWireCube(centre, new Vector3(m_Field.x, m_Field.y, 0f));

            Gizmos.color = new Color(0.85f, 0.85f, 0.35f, 0.5f);
            Gizmos.DrawWireCube(centre, new Vector3(m_View.x, m_View.y, 0f));
        }
    }
}
