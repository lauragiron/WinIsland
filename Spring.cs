using System;

namespace WinIsland
{
    public class Spring
    {
        // 目标数值（比如：胶囊想要变成的宽度）
        public double Target { get; set; }

        // 当前数值（胶囊现在的宽度）
        public double Current { get; private set; }

        // 速度
        private double Velocity;

        // 物理参数 (你可以调整这两个数来改变手感！)
        private const double Tension = 200.0; // 张力：越大切换越快
        private const double Friction = 18.0; // 摩擦力：越小回弹越厉害（果冻感）

        public Spring(double startValue)
        {
            Current = startValue;
            Target = startValue;
        }

        // 每一帧调用一次这个方法来更新位置
        public double Update(double dt)
        {
            // 物理公式：F = -kx - dv
            var force = Tension * (Target - Current);
            var acceleration = force - Friction * Velocity;

            Velocity += acceleration * dt;
            Current += Velocity * dt;

            return Current;
        }
    }
}