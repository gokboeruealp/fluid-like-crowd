using Unity.Mathematics;
using UnityEngine;

namespace FluidCrowd.Demo
{
    public enum MapPart
    {
        Rock = 0,
        Mouth = 1,
        Exit = 2,
    }

    [AddComponentMenu("Fluid Crowd/Map Box")]
    public sealed class MapBox : MonoBehaviour
    {
        [Tooltip("Rock is something the crowd walks round. The mouth is where it is released " +
                 "and the exit is where it leaves; there is one of each and the last one found " +
                 "wins.")]
        [SerializeField] MapPart m_Part = MapPart.Rock;

        static readonly Color[] Tint =
        {
            new Color(0.62f, 0.67f, 0.76f, 0.55f),
            new Color(0.30f, 0.85f, 0.45f, 0.55f),
            new Color(0.95f, 0.65f, 0.25f, 0.55f),
        };

        public MapPart Part => m_Part;

        public Box ToBox()
        {
            Vector3 centre = transform.position;
            Vector3 size = transform.lossyScale;

            return new Box(centre.x, centre.y, math.abs(size.x), math.abs(size.y));
        }

        void OnDrawGizmos()
        {
            Vector3 size = transform.lossyScale;
            var drawn = new Vector3(math.abs(size.x), math.abs(size.y), 0.05f);

            Gizmos.color = Tint[(int)m_Part];
            Gizmos.DrawCube(transform.position, drawn);

            Gizmos.color = new Color(0f, 0f, 0f, 0.45f);
            Gizmos.DrawWireCube(transform.position, drawn);
        }
    }
}
