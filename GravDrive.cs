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
        ///   - 独立惯性阻尼：死区 / 低速比例 / 高速全力 三区控制；
        ///   - 写入预算：每帧最多写 N 个发生器，削峰平谷。
        /// </summary>
        public class GravDrive
        {
            // ── 方块引用 ──────────────────────────────────────────────────────
            List<IMyGravityGenerator> Gravs;
            IMyShipController _cockpit;  // 用于每帧实时更新 _rm
            string _front;

            // ── 重力发生器方向映射 ─────────────────────────────────────────────
            Base6Directions.Direction[] GravOrientation;
            // 每轴贡献的发生器总数（X=前后, Y=左右, Z=上下），至少为 1 防除零
            int[] _axisCount;

            // ── 旋转后的驾驶舱参考矩阵（支持非前向安装朝向）────────────────────
            // 每帧由 Apply() 实时重建，确保转向后仍正确投影速度
            MatrixD _rm;

            // ── 写入预算状态数组 ───────────────────────────────────────────────
            struct GravGenState
            {
                public float Desired;  // 本帧期望的 GravityAcceleration
                public float Written;  // 上次实际写入的 GravityAcceleration
                public bool  Enabled;  // 上次实际写入的 Enabled 状态
                public bool  Dirty;    // 是否需要写入
            }
            GravGenState[] _state;

            // ── Dirty 索引队列（避免每帧 O(n) 扫描）────────────────────────
            RingQueue<int> _dirtyQueue;

            // ── 对外只读状态（供性能显示器使用）─────────────────────────────
            public int LastWrites    { get; private set; }
            public int PendingWrites { get; private set; }

            // ── 构造函数 ──────────────────────────────────────────────────────
            public GravDrive(IMyShipController cockpit,
                             List<IMyGravityGenerator> gravs,
                             string front = "Front")
            {
                Gravs    = gravs;
                _cockpit = cockpit;
                _front   = front;
                _rm      = GetRotatedMatrix(cockpit.WorldMatrix, front);

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

                // 统计每轴方向的发生器数量（Forward+Backward 同属 X 轴，以此类推）
                int cntX = 0, cntY = 0, cntZ = 0;
                for (int i = 0; i < Gravs.Count; i++)
                {
                    switch (GravOrientation[i])
                    {
                        case Base6Directions.Direction.Forward:
                        case Base6Directions.Direction.Backward: cntX++; break;
                        case Base6Directions.Direction.Left:
                        case Base6Directions.Direction.Right:    cntY++; break;
                        case Base6Directions.Direction.Up:
                        case Base6Directions.Direction.Down:     cntZ++; break;
                    }
                }
                _axisCount = new int[3]
                {
                    Math.Max(1, cntX),
                    Math.Max(1, cntY),
                    Math.Max(1, cntZ)
                };

                // 初始化状态数组与 dirty 队列
                _state      = new GravGenState[Gravs.Count];
                _dirtyQueue = new RingQueue<int>(Gravs.Count);
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
            /// <param name="lowSpeedThreshold">低速比例控制区间上限 (m/s)</param>
            /// <param name="k">比例常数（输出 = K × 速度）</param>
            public void Apply(Vector3 moveIndicator, Vector3D worldVel, bool dampenersOn,
                              double maxAccel, double stopThreshold,
                              double lowSpeedThreshold, double k)
            {
                // 每帧实时重建参考矩阵，确保飞船转向后速度投影仍正确
                _rm = GetRotatedMatrix(_cockpit.WorldMatrix, _front);

                // 世界系速度 → 驱动器局部坐标（X=前后, Y=左右, Z=上下）
                Vector3D localVel = new Vector3D(
                    Vector3D.Dot(worldVel, _rm.Forward),
                    Vector3D.Dot(worldVel, _rm.Left),
                    Vector3D.Dot(worldVel, _rm.Up));

                // MoveIndicator → 驱动器局部坐标，夹紧到 [-1, 1]（防高灵敏度溢出）
                Vector3D input = new Vector3D(
                    MathHelper.Clamp(moveIndicator.Z,  -1f, 1f),   // Z  = 前后
                    MathHelper.Clamp(moveIndicator.X,  -1f, 1f),   // X  = 左右
                    MathHelper.Clamp(-moveIndicator.Y, -1f, 1f));  // -Y = 上下

                // 各轴期望加速度（逐轴独立，三区控制：死区 / 低速比例 / 高速全力）
                Vector3D desired = new Vector3D(
                    ComputeAxis(input.X, localVel.X, dampenersOn, maxAccel * _axisCount[0], stopThreshold, lowSpeedThreshold, k),
                    ComputeAxis(input.Y, localVel.Y, dampenersOn, maxAccel * _axisCount[1], stopThreshold, lowSpeedThreshold, k),
                    ComputeAxis(input.Z, localVel.Z, dampenersOn, maxAccel * _axisCount[2], stopThreshold, lowSpeedThreshold, k));

                // 映射到各重力发生器的期望 GravityAcceleration，标记脏位
                for (int i = 0; i < Gravs.Count; i++)
                {
                    // desired 是轴向总加速度，分摊到该轴每个发生器
                    float newDesired;
                    switch (GravOrientation[i])
                    {
                        case Base6Directions.Direction.Forward:  newDesired = (float)( desired.X / _axisCount[0]); break;
                        case Base6Directions.Direction.Backward: newDesired = (float)(-desired.X / _axisCount[0]); break;
                        case Base6Directions.Direction.Left:     newDesired = (float)( desired.Y / _axisCount[1]); break;
                        case Base6Directions.Direction.Right:    newDesired = (float)(-desired.Y / _axisCount[1]); break;
                        case Base6Directions.Direction.Up:       newDesired = (float)( desired.Z / _axisCount[2]); break;
                        case Base6Directions.Direction.Down:     newDesired = (float)(-desired.Z / _axisCount[2]); break;
                        default:                                 newDesired = 0f;                                   break;
                    }
                    if (Math.Abs(newDesired - _state[i].Desired) > 1e-4f)
                    {
                        if (!_state[i].Dirty)
                            _dirtyQueue.TryEnqueue(i);
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
                int i;
                while (written < maxCount && _dirtyQueue.TryDequeue(out i))
                {
                    if (!_state[i].Dirty) continue;  // 已被 ShutDown 等清除，跳过

                    float v = _state[i].Desired;
                    bool shouldEnable = Math.Abs(v) > 1e-4f;

                    bool accelChanged = Math.Abs(v - _state[i].Written) > 1e-4f;
                    bool enableChanged = shouldEnable != _state[i].Enabled;

                    // 状态与方块当前值完全一致，跳过，不消耗写入预算
                    if (!accelChanged && !enableChanged)
                    {
                        _state[i].Dirty = false;
                        continue;
                    }

                    if (accelChanged)
                    {
                        Gravs[i].GravityAcceleration = v;
                        _state[i].Written = v;
                    }
                    if (enableChanged)
                    {
                        Gravs[i].Enabled = shouldEnable;
                        _state[i].Enabled = shouldEnable;
                    }
                    _state[i].Dirty = false;
                    written++;
                }
                LastWrites    = written;
                PendingWrites = _dirtyQueue.Count;
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
                    _state[i].Enabled = false;
                    _state[i].Dirty   = false;
                }
                _dirtyQueue.Clear();
                LastWrites    = Gravs.Count;
                PendingWrites = 0;
            }

            // ── 私有：单轴控制律 ───────────────────────────────────────────────

            /// <summary>
            /// 计算单轴期望加速度。
            /// 三段逻辑：有输入→跟随；无输入+阻尼开→刹车（高速bang-bang / 低速比例）；无输入+阻尼关→零。
            /// </summary>
            static double ComputeAxis(double input, double vel,
                                      bool dampeners, double maxAccel, double stopThreshold,
                                      double lowSpeedThreshold, double k)
            {
                // 有输入：最大力量跟随输入方向
                if (Math.Abs(input) > 0.01)
                    return maxAccel * Math.Sign(input);

                // 无输入 + 阻尼关闭：自由漂移
                if (!dampeners)
                    return 0;

                // 无输入 + 阻尼开启：刹车
                double absVel = Math.Abs(vel);
                if (absVel <= stopThreshold)
                    return 0;

                // 低速区间：比例控制，平滑收敛到零（Sign(vel) 即制动方向，坐标系已反转）
                if (absVel < lowSpeedThreshold)
                {
                    double propOut = k * vel;
                    if (Math.Abs(propOut) > maxAccel)
                        return maxAccel * Math.Sign(vel);
                    return propOut;
                }

                // 高速区间：全力制动
                return maxAccel * Math.Sign(vel);
            }
        }
    }
}
