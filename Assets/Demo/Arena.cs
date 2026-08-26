using Unity.Collections;
using Unity.Mathematics;

namespace FluidCrowd.Demo
{
    public readonly struct Box
    {
        public readonly float2 Centre;
        public readonly float2 Size;

        public Box(float x, float y, float width, float height)
        {
            Centre = new float2(x, y);
            Size = new float2(width, height);
        }

        public static Box Between(float x0, float y0, float x1, float y1) => new Box(
            (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, math.abs(x1 - x0), math.abs(y1 - y0));

        public float2 Min => Centre - Size * 0.5f;
        public float2 Max => Centre + Size * 0.5f;

        public bool Contains(float2 point) =>
            math.all(math.abs(point - Centre) <= Size * 0.5f);
    }

    public sealed class Arena
    {
        public readonly float2 Min;
        public readonly float2 Max;

        public readonly float2 ViewMin;
        public readonly float2 ViewMax;

        public readonly Box Spawn;
        public readonly Box Goal;
        public readonly Box[] Rock;

        public Arena(
            float2 min, float2 max, float2 viewMin, float2 viewMax, Box spawn, Box goal,
            Box[] rock)
        {
            Min = min;
            Max = max;
            ViewMin = viewMin;
            ViewMax = viewMax;
            Spawn = spawn;
            Goal = goal;
            Rock = rock;
        }

        public float2 Size => Max - Min;
        public float2 ViewSize => ViewMax - ViewMin;

        public int Paint(NativeArray<byte> cost, in NavGrid grid)
        {
            for (int i = 0; i < cost.Length; i++)
                cost[i] = 1;

            for (int i = 0; i < Rock.Length; i++)
            {
                int2 lo = grid.WorldToCoord(Rock[i].Min);
                int2 hi = grid.WorldToCoord(Rock[i].Max);

                for (int y = lo.y; y <= hi.y; y++)
                {
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        int cell = grid.Index(new int2(x, y));

                        if (Rock[i].Contains(grid.CellCenter(cell)))
                            cost[cell] = FlowField.Blocked;
                    }
                }
            }

            int walkable = 0;

            for (int i = 0; i < cost.Length; i++)
            {
                if (cost[i] != FlowField.Blocked)
                    walkable++;
            }

            return walkable;
        }

        public void MarkGoal(FlowField field, in NavGrid grid)
        {
            field.GoalCells.Clear();

            for (int i = 0; i < field.Cost.Length; i++)
            {
                if (field.Cost[i] == FlowField.Blocked)
                    continue;

                if (Goal.Contains(grid.CellCenter(i)))
                    field.GoalCells.Add(i);
            }
        }
    }
}
