using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using System.Collections.Generic;
using VRageMath;

namespace IngameScript
{
    public partial class Program
    {
        /// <summary>
        /// 重力引擎驱动器：读取推进器出力比，将其等比映射到重力发生器，
        /// 实现以重力发生器为主推力的辅助推进。
        /// </summary>
        public class GravDrive
        {
            // ── 方块引用 ──────────────────────────────────────────────────────
            IMyShipController            Cockpit;
            List<IMyThrust>              Thrusts;
            List<IMyGravityGenerator>    Gravs;

            // ── 推进器方向映射 ─────────────────────────────────────────────────
            Base6Directions.Direction[]  ThrustOrientation;
            IMyThrust ThrustForward,  ThrustBackward;
            IMyThrust ThrustLeft,     ThrustRight;
            IMyThrust ThrustUp,       ThrustDown;

            // ── 重力发生器方向映射 ─────────────────────────────────────────────
            Base6Directions.Direction[]  GravOrientation;

            // ── 构造函数 ──────────────────────────────────────────────────────
            public GravDrive(IMyShipController cockpit,
                             List<IMyThrust> thrusts,
                             List<IMyGravityGenerator> gravs,
                             string front = "Front")
            {
                Cockpit = cockpit;
                Thrusts = thrusts;
                Gravs   = gravs;

                MatrixD rm = GetRotatedMatrix(Cockpit.WorldMatrix, front);

                // ── 处理推进器 ──────────────────────────────────────────────
                ThrustOrientation = new Base6Directions.Direction[Thrusts.Count];
                for (int i = 0; i < Thrusts.Count; i++)
                {
                    if (Thrusts[i] == null || !Thrusts[i].IsWorking) continue;

                    var cf = Thrusts[i].WorldMatrix.GetClosestDirection(rm.Forward);
                    var cu = Thrusts[i].WorldMatrix.GetClosestDirection(rm.Up);
                    var cl = Thrusts[i].WorldMatrix.GetClosestDirection(rm.Left);

                    switch (cf)
                    {
                        case Base6Directions.Direction.Forward:
                            ThrustOrientation[i] = Base6Directions.Direction.Forward;
                            if (ThrustForward  == null || !ThrustForward.IsWorking)  ThrustForward  = Thrusts[i];
                            break;
                        case Base6Directions.Direction.Backward:
                            ThrustOrientation[i] = Base6Directions.Direction.Backward;
                            if (ThrustBackward == null || !ThrustBackward.IsWorking) ThrustBackward = Thrusts[i];
                            break;
                    }
                    switch (cu)
                    {
                        case Base6Directions.Direction.Forward:
                            ThrustOrientation[i] = Base6Directions.Direction.Up;
                            if (ThrustUp   == null || !ThrustUp.IsWorking)   ThrustUp   = Thrusts[i];
                            break;
                        case Base6Directions.Direction.Backward:
                            ThrustOrientation[i] = Base6Directions.Direction.Down;
                            if (ThrustDown == null || !ThrustDown.IsWorking) ThrustDown = Thrusts[i];
                            break;
                    }
                    switch (cl)
                    {
                        case Base6Directions.Direction.Forward:
                            ThrustOrientation[i] = Base6Directions.Direction.Left;
                            if (ThrustLeft  == null || !ThrustLeft.IsWorking)  ThrustLeft  = Thrusts[i];
                            break;
                        case Base6Directions.Direction.Backward:
                            ThrustOrientation[i] = Base6Directions.Direction.Right;
                            if (ThrustRight == null || !ThrustRight.IsWorking) ThrustRight = Thrusts[i];
                            break;
                    }
                }

                // ── 处理重力发生器 ──────────────────────────────────────────
                GravOrientation = new Base6Directions.Direction[Gravs.Count];
                for (int i = 0; i < Gravs.Count; i++)
                {
                    var cf = Gravs[i].WorldMatrix.GetClosestDirection(rm.Forward);
                    var cu = Gravs[i].WorldMatrix.GetClosestDirection(rm.Up);
                    var cl = Gravs[i].WorldMatrix.GetClosestDirection(rm.Left);

                    switch (cf)
                    {
                        case Base6Directions.Direction.Up:   GravOrientation[i] = Base6Directions.Direction.Forward;  break;
                        case Base6Directions.Direction.Down: GravOrientation[i] = Base6Directions.Direction.Backward; break;
                    }
                    switch (cu)
                    {
                        case Base6Directions.Direction.Up:   GravOrientation[i] = Base6Directions.Direction.Up;   break;
                        case Base6Directions.Direction.Down: GravOrientation[i] = Base6Directions.Direction.Down; break;
                    }
                    switch (cl)
                    {
                        case Base6Directions.Direction.Up:   GravOrientation[i] = Base6Directions.Direction.Left;  break;
                        case Base6Directions.Direction.Down: GravOrientation[i] = Base6Directions.Direction.Right; break;
                    }
                }
            }

            // ── 矩阵旋转（用于支持非前向驾驶舱）─────────────────────────────
            MatrixD GetRotatedMatrix(MatrixD m, string front)
            {
                MatrixD n = m;
                switch (front)
                {
                    case "Back":  n.Forward = m.Backward; n.Left = m.Right;   break;
                    case "Up":    n.Forward = m.Up;  n.Up = m.Forward; n.Left = m.Right;   break;
                    case "Down":  n.Forward = m.Down; n.Up = m.Backward; n.Left = m.Right; break;
                    case "Left":  n.Backward = m.Right;  n.Left = m.Backward; break;
                    case "Right": n.Backward = m.Left;   n.Left = m.Forward;  break;
                }
                return n;
            }

            // ── 公开方法 ──────────────────────────────────────────────────────

            /// <summary>
            /// 将推进器当前出力比同步为重力发生器加速度，实现重力驱动辅助推进。
            /// </summary>
            /// <param name="Multiplier">各轴推力倍率 Vector3D，建议 10~20 m/s²</param>
            /// <param name="ThrustVector">叠加的额外重力偏置（前后/左右/上下，m/s²）</param>
            public void GravSyncWithThrusters(Vector3D Multiplier, Vector3D ThrustVector)
            {
                float fwd  = ThrustForward  != null ? ThrustForward.CurrentThrust  / ThrustForward.MaxEffectiveThrust  : 0f;
                float bwd  = ThrustBackward != null ? ThrustBackward.CurrentThrust / ThrustBackward.MaxEffectiveThrust : 0f;
                float lft  = ThrustLeft     != null ? ThrustLeft.CurrentThrust     / ThrustLeft.MaxEffectiveThrust     : 0f;
                float rgt  = ThrustRight    != null ? ThrustRight.CurrentThrust    / ThrustRight.MaxEffectiveThrust    : 0f;
                float up   = ThrustUp       != null ? ThrustUp.CurrentThrust       / ThrustUp.MaxEffectiveThrust       : 0f;
                float dn   = ThrustDown     != null ? ThrustDown.CurrentThrust     / ThrustDown.MaxEffectiveThrust     : 0f;

                ThrustVector.X += (fwd - bwd) * Multiplier.X;
                ThrustVector.Y += (lft - rgt) * Multiplier.Y;
                ThrustVector.Z += (up  - dn)  * Multiplier.Z;

                // NaN 保护（MaxEffectiveThrust 为 0 时会产生 NaN）
                if (double.IsNaN(ThrustVector.X)) ThrustVector.X = 0;
                if (double.IsNaN(ThrustVector.Y)) ThrustVector.Y = 0;
                if (double.IsNaN(ThrustVector.Z)) ThrustVector.Z = 0;

                SetGravOverride(ThrustVector);
            }

            /// <summary>
            /// 按船体坐标系向量设置所有重力发生器出力。
            /// X = 前后，Y = 左右，Z = 上下（m/s²）。
            /// 传入 Vector3D.Zero 则禁用所有重力发生器。
            /// </summary>
            public void SetGravOverride(Vector3D Value)
            {
                bool active = !Value.IsZero() && !double.IsNaN(Value.X);
                for (int i = 0; i < Gravs.Count; i++)
                {
                    switch (GravOrientation[i])
                    {
                        case Base6Directions.Direction.Forward:  Gravs[i].GravityAcceleration = (float) Value.X; break;
                        case Base6Directions.Direction.Backward: Gravs[i].GravityAcceleration = (float)-Value.X; break;
                        case Base6Directions.Direction.Left:     Gravs[i].GravityAcceleration = (float) Value.Y; break;
                        case Base6Directions.Direction.Right:    Gravs[i].GravityAcceleration = (float)-Value.Y; break;
                        case Base6Directions.Direction.Up:       Gravs[i].GravityAcceleration = (float) Value.Z; break;
                        case Base6Directions.Direction.Down:     Gravs[i].GravityAcceleration = (float)-Value.Z; break;
                    }
                    Gravs[i].Enabled = active;
                }
            }
        }
    }
}
