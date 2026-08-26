using Unity.Mathematics;

namespace FluidCrowd
{
    public struct NavGrid
    {
        public float2 Origin;
        public float CellSize;
        public int Width;
        public int Height;

        public int CellCount => Width * Height;

        public static NavGrid Create(float2 min, float2 max, float cellSize)
        {
            float2 size = max - min;
            return new NavGrid
            {
                Origin = min,
                CellSize = cellSize,
                Width = math.max(1, (int)math.ceil(size.x / cellSize)),
                Height = math.max(1, (int)math.ceil(size.y / cellSize)),
            };
        }

        public int2 WorldToCoord(float2 world)
        {
            int2 coord = (int2)math.floor((world - Origin) / CellSize);
            return math.clamp(coord, int2.zero, new int2(Width - 1, Height - 1));
        }

        public bool InBounds(int2 coord) =>
            coord.x >= 0 && coord.x < Width && coord.y >= 0 && coord.y < Height;

        public int Index(int2 coord) => coord.y * Width + coord.x;

        public int WorldToIndex(float2 world) => Index(WorldToCoord(world));

        public int2 Coord(int index) => new int2(index % Width, index / Width);

        public float2 CellCenter(int index)
        {
            int2 coord = Coord(index);
            return Origin + (new float2(coord.x, coord.y) + 0.5f) * CellSize;
        }
    }

    public static class GridNeighbours
    {
        public const int Count = 8;

        public static int2 Offset(int index)
        {
            switch (index)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(-1, 0);
                case 2: return new int2(0, 1);
                case 3: return new int2(0, -1);
                case 4: return new int2(1, 1);
                case 5: return new int2(1, -1);
                case 6: return new int2(-1, 1);
                default: return new int2(-1, -1);
            }
        }

        public static bool IsDiagonal(int2 offset) => offset.x != 0 && offset.y != 0;
    }
}
