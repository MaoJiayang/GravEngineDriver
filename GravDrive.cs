using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using VRageMath;

namespace IngameScript
{
    public partial class Program
    {
        /// <summary>
        /// 重力引擎驱动器：
        ///   - 以驾驶舱 MoveIndicator 为输入信号驱动出力；
        ///   - 独立惯性阻尼（自观测加速度 + 零交叉预测）；
        ///   - 写入预算：每帧最多写 N 个发生器，削峰平谷。
        /// </summary>
        public class GravDrive
        {
            // ── 方块引用 ──────────────────────────────────────────────────────
            List<IMyGravityGenerator> Gravs;

            // ── 重力发生器方向映射 ─────────────────────────────────────────────
            Base6Directions.Direction[] GravOrientation;

            // ── 旋转后的驾驶舱参考矩阵（支持非前向安装朝向）────────────────────
            MatrixD _rm;

            // ── 写入预算状态数组 ───────────────────────────────────────────────
            struct GravGenState
            {
                public float Desired;  // 本帧期望值
                public float Written;  // 上次实际写入值
                public bool  Dirty;    // 是否需要写入
            }
            GravGenState[] _state;

            // ── 观测加速度（自校准，含所有外力合力）──────────────────────────
            Vector3D _prevLocalVel;
            Vector3D _obsAccelLocal;
            bool     _firstFrame = true;

            // ── 对外只读状态（供性能显示器使用）─────────────────────────────
            public int LastWrites    { get; private set; }
            public int PendingWrites { get; private set; }

            // ── 构造函数 ──────────────────────────────────────────────────────
            public GravDrive(IMyShipController cockpit,
                             List<IMyGravityGenerator> gravs,
                             string front = "Front")
            {
                Gravs = gravs;
                _rm   = GetRotatedMatrix(cockpit.WorldMatrix, front);

                // 处理重力发生器方向映射
                GravOrientation = new Base6Directions.Direction[Gravs.Count];
                for (int i = 0; i < Gravs.Count; i++)
                {
                    var cf = Gravs[i].WorldMatrix.GetClosestDirection(_rm.Forward);
                    var cu = Gravs[i].WorldMatrix.GetClosestDirection(_rm.Up);
                    var cl = Gravs[i].WorldMatrix.GetClosestDirection(_rm.Left);

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

                // 初始化状态数组，首次关闭所有发生器
                _state = new GravGenState[Gravs.Count];
                for (int i = 0; i < Gravs.Count; i++)
                    Gravs[i].Enabled = false;
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
            /// 每帧调用：根据键盘输入与当前速度计算各重力发生器的期望加速度，标记脏位。
            /// 不直接写入发生器，写入由 FlushWrites() 完成。
            /// </summary>
            /// <param name="moveIndicator">驾驶舱 MoveIndicator（Z=前后, X=左右, Y=上下）</param>
            /// <param name="worldVel">当前世界系线速度</param>
            /// <param name="dampenersOn">驾驶舱 DampenersOverride 值</param>
            /// <param name="maxAccel">最大出力加速度 (m/s²)</param>
            /// <param name="stopThreshold">速度死区 (m/s)，低于此值停止阻尼出力</param>
            /// <param name="dt">距上次运行的时间 (s)</param>
            public void Apply(Vector3 moveIndicator, Vector3D worldVel, bool dampenersOn,
                              double maxAccel, double stopThreshold, double dt)
            {
                // 世界系速度 → 驱动器局部坐标（X=前后, Y=左右, Z=上下）
                Vector3D localVel = new Vector3D(
                    Vector3D.Dot(worldVel, _rm.Forward),
                    Vector3D.Dot(worldVel, _rm.Left),
                    Vector3D.Dot(worldVel, _rm.Up));

                // 更新观测加速度（自校准，包含所有外力合力，用于零交叉预测）
                if (_firstFrame || dt < 1e-6)
                {
                    _obsAccelLocal = Vector3D.Zero;
                    _firstFrame    = false;
                }
                else
                    _obsAccelLocal = (localVel - _prevLocalVel) / dt;

                _prevLocalVel = localVel;

                // MoveIndicator → 驱动器局部坐标，夹紧到 [-1, 1]（防高灵敏度溢出）
                Vector3D input = new Vector3D(
                    MathHelper.Clamp(moveIndicator.Z,  -1f, 1f),   // Z  = 前后
                    MathHelper.Clamp(moveIndicator.X,  -1f, 1f),   // X  = 左右
                    MathHelper.Clamp(-moveIndicator.Y, -1f, 1f));  // -Y = 上下

                // 各轴期望加速度
                Vector3D desired = new Vector3D(
                    ComputeAxis(input.X, localVel.X, _obsAccelLocal.X, dampenersOn, maxAccel, stopThreshold, dt),
                    ComputeAxis(input.Y, localVel.Y, _obsAccelLocal.Y, dampenersOn, maxAccel, stopThreshold, dt),
                    ComputeAxis(input.Z, localVel.Z, _obsAccelLocal.Z, dampenersOn, maxAccel, stopThreshold, dt));

                // 映射到各重力发生器的期望 GravityAcceleration，标记脏位
                for (int i = 0; i < Gravs.Count; i++)
                {
                    float newDesired;
                    switch (GravOrientation[i])
                    {
                        case Base6Directions.Direction.Forward:  newDesired = (float) desired.X; break;
                        case Base6Directions.Direction.Backward: newDesired = (float)-desired.X; break;
                        case Base6Directions.Direction.Left:     newDesired = (float) desired.Y; break;
                        case Base6Directions.Direction.Right:    newDesired = (float)-desired.Y; break;
                        case Base6Directions.Direction.Up:       newDesired = (float) desired.Z; break;
                        case Base6Directions.Direction.Down:     newDesired = (float)-desired.Z; break;
                        default:                                 newDesired = 0f;                break;
                    }
                    if (Math.Abs(newDesired - _state[i].Desired) > 1e-4f)
                    {
                        _state[i].Desired = newDesired;
                        _state[i].Dirty   = true;
                    }
                }
            }

            /// <summary>
            /// 每帧调用（Apply 之后）：将脏状态写入重力发生器，每帧最多写 maxCount 个。
            /// delta 最大的项自然在本帧更早被写入（循环顺序近似），剩余留到下帧。
            /// </summary>
            public void FlushWrites(int maxCount)
            {
                int written = 0;
                for (int i = 0; i < _state.Length && written < maxCount; i++)
                {
                    if (!_state[i].Dirty) continue;
                    float v = _state[i].Desired;
                    Gravs[i].GravityAcceleration = v;
                    Gravs[i].Enabled             = Math.Abs(v) > 1e-4f;
                    _state[i].Written            = v;
                    _state[i].Dirty              = false;
                    written++;
                }
                LastWrites = written;

                int pending = 0;
                for (int i = 0; i < _state.Length; i++)
                    if (_state[i].Dirty) pending++;
                PendingWrites = pending;
            }

            /// <summary>
            /// 紧急关闭所有重力发生器（GravOn = false 时调用）。不受写入预算限制。
            /// </summary>
            public void ShutDown()
            {
                for (int i = 0; i < Gravs.Count; i++)
                {
                    Gravs[i].GravityAcceleration = 0f;
                    Gravs[i].Enabled             = false;
                    _state[i].Desired = 0f;
                    _state[i].Written = 0f;
                    _state[i].Dirty   = false;
                }
                _firstFrame   = true; // 重置观测加速度（下次接管时重新校准）
                LastWrites    = Gravs.Count;
                PendingWrites = 0;
            }

            // ── 私有：单轴控制律 ───────────────────────────────────────────────

            /// <summary>
            /// 计算单轴期望加速度。
            /// 三段逻辑：有输入 → 全力跟随；无输入+阻尼开 → 全力刹车（含零交叉预测）；无输入+阻尼关 → 零。
            /// </summary>
            static double ComputeAxis(double input, double vel, double obsAccel,
                                      bool dampeners, double maxAccel, double stopThreshold, double dt)
            {
                // 有输入：最大力量跟随输入方向
                if (Math.Abs(input) > 0.01)
                    return maxAccel * Math.Sign(input);

                // 无输入 + 阻尼关闭：自由漂移
                if (!dampeners)
                    return 0;

                // 无输入 + 阻尼开启：刹车
                double absVel = Math.Abs(vel);
                if (absVel < stopThreshold)
                    return 0; // 死区：已接近静止，停止出力防低速抖振

                // 零交叉预测：利用自观测加速度（含所有外力合力，自校准）
                // 若预测下一帧速度与当前异号，则本帧停止出力，防止过零超调
                double velNext = vel + obsAccel * dt;
                if (vel * velNext < 0)
                    return 0;

                // 比例刹车：低速时缩力为"恰好一帧停止"，消除超调振荡；
                // 高速时仍全力刹车（min 确保不超过 maxAccel）。
                // 注意：此处正号是正确的——desired 坐标系中 Sign(vel) 对应反向推力，
                // 因为 MoveIndicator.Z=-1 对应前进，整个坐标系符号已反转。
                double brakingAccel = (dt > 1e-6)
                    ? Math.Min(maxAccel, absVel / dt)
                    : maxAccel;
                return brakingAccel * Math.Sign(vel);
            }
        }
    }
}
