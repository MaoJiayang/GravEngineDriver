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
            List<IMyGravityGenerator> 引擎列表;
            IMyShipController _驾驶舱;
            string _前方;

            // ── 重力发生器方向映射 ─────────────────────────────────────────────
            Base6Directions.Direction[] 引擎方向;
            int[] _轴数;

            // ── 旋转后的驾驶舱参考矩阵（支持非前向安装朝向）────────────────────
            MatrixD _参考矩阵;

            // ── 写入预算状态数组 ───────────────────────────────────────────────
            struct 发生器状态
            {
                public float 期望值;  // 本帧期望的 GravityAcceleration
                public float 已写入;  // 上次实际写入的 GravityAcceleration
                public bool  已启用;  // 上次实际写入的 Enabled 状态
                public bool  需写入;  // 是否需要写入
            }
            发生器状态[] _状态;

            // ── 脏索引队列（避免每帧 O(n) 扫描）────────────────────────────
            RingQueue<int> _脏队列;

            // ── 对外只读状态（供性能显示器使用）─────────────────────────────
            public int 本次写入 { get; private set; }
            public int 待写入   { get; private set; }

            // ── 构造函数 ──────────────────────────────────────────────────────
            public GravDrive(IMyShipController 驾驶舱,
                             List<IMyGravityGenerator> 引擎,
                             string 前方 = "Front")
            {
                引擎列表 = 引擎;
                _驾驶舱  = 驾驶舱;
                _前方    = 前方;
                _参考矩阵 = 旋转矩阵(驾驶舱.WorldMatrix, 前方);

                // 处理重力发生器方向映射
                引擎方向 = new Base6Directions.Direction[引擎列表.Count];
                for (int i = 0; i < 引擎列表.Count; i++)
                {
                    var 前向 = 引擎列表[i].WorldMatrix.GetClosestDirection(_参考矩阵.Forward);
                    var 上向 = 引擎列表[i].WorldMatrix.GetClosestDirection(_参考矩阵.Up);
                    var 左向 = 引擎列表[i].WorldMatrix.GetClosestDirection(_参考矩阵.Left);

                    switch (前向)
                    {
                        case Base6Directions.Direction.Up:   引擎方向[i] = Base6Directions.Direction.Forward;  break;
                        case Base6Directions.Direction.Down: 引擎方向[i] = Base6Directions.Direction.Backward; break;
                    }
                    switch (上向)
                    {
                        case Base6Directions.Direction.Up:   引擎方向[i] = Base6Directions.Direction.Up;   break;
                        case Base6Directions.Direction.Down: 引擎方向[i] = Base6Directions.Direction.Down; break;
                    }
                    switch (左向)
                    {
                        case Base6Directions.Direction.Up:   引擎方向[i] = Base6Directions.Direction.Left;  break;
                        case Base6Directions.Direction.Down: 引擎方向[i] = Base6Directions.Direction.Right; break;
                    }
                }

                // 统计每轴方向的发生器数量（Forward+Backward 同属 X 轴，以此类推）
                int 前轴数 = 0, 左轴数 = 0, 上轴数 = 0;
                for (int i = 0; i < 引擎列表.Count; i++)
                {
                    switch (引擎方向[i])
                    {
                        case Base6Directions.Direction.Forward:
                        case Base6Directions.Direction.Backward: 前轴数++; break;
                        case Base6Directions.Direction.Left:
                        case Base6Directions.Direction.Right:    左轴数++; break;
                        case Base6Directions.Direction.Up:
                        case Base6Directions.Direction.Down:     上轴数++; break;
                    }
                }
                _轴数 = new int[3]
                {
                    Math.Max(1, 前轴数),
                    Math.Max(1, 左轴数),
                    Math.Max(1, 上轴数)
                };

                // 初始化状态数组与脏队列
                _状态   = new 发生器状态[引擎列表.Count];
                _脏队列 = new RingQueue<int>(引擎列表.Count);
                for (int i = 0; i < 引擎列表.Count; i++)
                    引擎列表[i].Enabled = false;
            }

            // ── 矩阵旋转（用于支持非前向驾驶舱）─────────────────────────────
            MatrixD 旋转矩阵(MatrixD 原矩阵, string 前方)
            {
                MatrixD 新矩阵 = 原矩阵;
                switch (前方)
                {
                    case "Back":  新矩阵.Forward = 原矩阵.Backward; 新矩阵.Left = 原矩阵.Right;   break;
                    case "Up":    新矩阵.Forward = 原矩阵.Up;    新矩阵.Up = 原矩阵.Forward; 新矩阵.Left = 原矩阵.Right; break;
                    case "Down":  新矩阵.Forward = 原矩阵.Down;  新矩阵.Up = 原矩阵.Backward; 新矩阵.Left = 原矩阵.Right; break;
                    case "Left":  新矩阵.Backward = 原矩阵.Right;  新矩阵.Left = 原矩阵.Backward; break;
                    case "Right": 新矩阵.Backward = 原矩阵.Left;   新矩阵.Left = 原矩阵.Forward;  break;
                }
                return 新矩阵;
            }

            // ── 公开方法 ──────────────────────────────────────────────────────

            /// <summary>
            /// 每帧调用：根据键盘输入与当前速度计算各重力发生器的期望加速度，标记脏位。
            /// 不直接写入发生器，写入由 执行写入() 完成。
            /// </summary>
            public void 计算期望(Vector3 移动指示, Vector3D 世界速度, bool 阻尼开启,
                              double 最大加速度, double 停止阈值,
                              double 低速阈值)
            {
                // 每帧实时重建参考矩阵，确保飞船转向后速度投影仍正确
                _参考矩阵 = 旋转矩阵(_驾驶舱.WorldMatrix, _前方);

                // 世界系速度 → 驱动器局部坐标（X=前后, Y=左右, Z=上下）
                Vector3D 局部速度 = new Vector3D(
                    Vector3D.Dot(世界速度, _参考矩阵.Forward),
                    Vector3D.Dot(世界速度, _参考矩阵.Left),
                    Vector3D.Dot(世界速度, _参考矩阵.Up));

                // MoveIndicator → 驱动器局部坐标，夹紧到 [-1, 1]（防高灵敏度溢出）
                Vector3D 输入 = new Vector3D(
                    MathHelper.Clamp(移动指示.Z,  -1f, 1f),   // Z = 前后
                    MathHelper.Clamp(移动指示.X,  -1f, 1f),   // X = 左右
                    MathHelper.Clamp(-移动指示.Y, -1f, 1f));  // -Y = 上下

                // 各轴期望加速度（逐轴独立，三区控制：死区 / 低速比例 / 高速全力）
                Vector3D 期望 = new Vector3D(
                    计算单轴(输入.X, 局部速度.X, 阻尼开启, 最大加速度 * _轴数[0], 停止阈值, 低速阈值),
                    计算单轴(输入.Y, 局部速度.Y, 阻尼开启, 最大加速度 * _轴数[1], 停止阈值, 低速阈值),
                    计算单轴(输入.Z, 局部速度.Z, 阻尼开启, 最大加速度 * _轴数[2], 停止阈值, 低速阈值));

                // 映射到各重力发生器的期望 GravityAcceleration，标记脏位
                for (int i = 0; i < 引擎列表.Count; i++)
                {
                    // 期望 是轴向总加速度，分摊到该轴每个发生器
                    float 新期望值;
                    switch (引擎方向[i])
                    {
                        case Base6Directions.Direction.Forward:  新期望值 = (float)( 期望.X / _轴数[0]); break;
                        case Base6Directions.Direction.Backward: 新期望值 = (float)(-期望.X / _轴数[0]); break;
                        case Base6Directions.Direction.Left:     新期望值 = (float)( 期望.Y / _轴数[1]); break;
                        case Base6Directions.Direction.Right:    新期望值 = (float)(-期望.Y / _轴数[1]); break;
                        case Base6Directions.Direction.Up:       新期望值 = (float)( 期望.Z / _轴数[2]); break;
                        case Base6Directions.Direction.Down:     新期望值 = (float)(-期望.Z / _轴数[2]); break;
                        default:                                 新期望值 = 0f;                              break;
                    }
                    if (Math.Abs(新期望值 - _状态[i].期望值) > 1e-4f)
                    {
                        if (!_状态[i].需写入)
                            _脏队列.TryEnqueue(i);
                        _状态[i].期望值 = 新期望值;
                        _状态[i].需写入 = true;
                    }
                }
            }

            /// <summary>
            /// 每帧调用（计算期望 之后）：将脏状态写入重力发生器，每帧最多写 最大写入数 个。
            /// </summary>
            public void 执行写入(int 最大写入数)
            {
                int 已写入数 = 0;
                int i;
                while (已写入数 < 最大写入数 && _脏队列.TryDequeue(out i))
                {
                    if (!_状态[i].需写入) continue;  // 已被 紧急关闭 等清除，跳过

                    float 值 = _状态[i].期望值;
                    bool  应启用 = Math.Abs(值) > 1e-4f;

                    bool 加速度已变 = Math.Abs(值 - _状态[i].已写入) > 1e-4f;
                    bool 启用已变   = 应启用 != _状态[i].已启用;

                    // 状态与方块当前值完全一致，跳过，不消耗写入预算
                    if (!加速度已变 && !启用已变)
                    {
                        _状态[i].需写入 = false;
                        continue;
                    }

                    if (加速度已变)
                    {
                        引擎列表[i].GravityAcceleration = 值;
                        _状态[i].已写入 = 值;
                    }
                    if (启用已变)
                    {
                        引擎列表[i].Enabled = 应启用;
                        _状态[i].已启用 = 应启用;
                    }
                    _状态[i].需写入 = false;
                    已写入数++;
                }
                本次写入 = 已写入数;
                待写入   = _脏队列.Count;
            }

            /// <summary>
            /// 紧急关闭所有重力发生器（引擎开关 = false 时调用）。不受写入预算限制。
            /// </summary>
            public void 紧急关闭()
            {
                for (int i = 0; i < 引擎列表.Count; i++)
                {
                    引擎列表[i].GravityAcceleration = 0f;
                    引擎列表[i].Enabled             = false;
                    _状态[i].期望值 = 0f;
                    _状态[i].已写入 = 0f;
                    _状态[i].已启用 = false;
                    _状态[i].需写入 = false;
                }
                _脏队列.Clear();
                本次写入 = 引擎列表.Count;
                待写入   = 0;
            }

            // ── 私有：单轴控制律 ───────────────────────────────────────────────

            /// <summary>
            /// 计算单轴期望加速度。
            /// 三段逻辑：有输入→跟随；无输入+阻尼开→刹车（高速全力 / 低速比例）；无输入+阻尼关→零。
            /// </summary>
            static double 计算单轴(double 输入, double 速度,
                                  bool 阻尼, double 最大加速度, double 停止阈值,
                                  double 低速阈值)
            {
                // 有输入：最大力量跟随输入方向
                if (Math.Abs(输入) > 0.01)
                    return 最大加速度 * Math.Sign(输入);

                // 无输入 + 阻尼关闭：自由漂移
                if (!阻尼)
                    return 0;

                // 无输入 + 阻尼开启：刹车
                double 速度绝对值 = Math.Abs(速度);
                if (速度绝对值 <= 停止阈值)
                    return 0;

                // 低速区间：比例控制，K = maxAccel / 低速阈值，使输出在阈值处刚好达到满力
                if (速度绝对值 < 低速阈值)
                {
                    double 比例常数 = 最大加速度 / 低速阈值;
                    double 比例输出 = 比例常数 * 速度;
                    if (Math.Abs(比例输出) > 最大加速度)
                        return 最大加速度 * Math.Sign(速度);
                    return 比例输出;
                }

                // 高速区间：全力制动
                return 最大加速度 * Math.Sign(速度);
            }
        }
    }
}
