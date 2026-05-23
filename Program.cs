using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Text;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        // ── 参数 ──────────────────────────────────────────────────────────────
        参数管理器 参数;

        // ── 方块列表 ──────────────────────────────────────────────────────────
        List<IMyGravityGenerator> 重力引擎   = new List<IMyGravityGenerator>();
        List<IMyShipController>   驾驶舱列表 = new List<IMyShipController>();
        List<IMyTextPanel>        _显示屏    = new List<IMyTextPanel>();
        IMyShipController 当前驾驶舱;

        // ── 运行时状态 ────────────────────────────────────────────────────────
        bool   引擎开关 = true;
        string 当前朝向;
        GravDrive 驱动;

        // ── 计数器 ────────────────────────────────────────────────────────────
        int _刷新计数 = 0;  // 方块刷新计数器
        int _显示计数 = 0;  // 显示刷新计数器

        // ── 性能监控（滚动平均） ───────────────────────────────────────────────
        double[] _耗时缓冲;
        int      _耗时指针;
        double   _耗时总和;

        public Program()
        {
            参数     = new 参数管理器(Me);
            当前朝向 = 参数.默认朝向;
            _耗时缓冲 = new double[参数.滚动窗口大小];
            获取方块();
            初始化驱动();
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }

        // ── 方块查找 ──────────────────────────────────────────────────────────
        void 获取方块()
        {
            var 编组 = string.IsNullOrWhiteSpace(参数.自定义编组名称)
                ? null
                : GridTerminalSystem.GetBlockGroupWithName(参数.自定义编组名称);

            重力引擎.Clear();
            if (编组 != null) 编组.GetBlocksOfType(重力引擎);
            if (重力引擎.Count == 0) GridTerminalSystem.GetBlocksOfType(重力引擎);

            驾驶舱列表.Clear();
            if (编组 != null) 编组.GetBlocksOfType(驾驶舱列表, b => b.CanControlShip && b.IsFunctional);
            if (驾驶舱列表.Count == 0) GridTerminalSystem.GetBlocksOfType(驾驶舱列表, b => b.CanControlShip && b.IsFunctional);

            当前驾驶舱 = 驾驶舱列表.Find(x => x.IsUnderControl);
            if (当前驾驶舱 == null && 驾驶舱列表.Count > 0) 当前驾驶舱 = 驾驶舱列表[0];

            // LCD 性能面板：在编组内查找，找到几个打几个
            _显示屏.Clear();
            if (编组 != null) 编组.GetBlocksOfType(_显示屏);
        }

        void 初始化驱动()
        {
            if (当前驾驶舱 == null) { 驱动 = null; return; }
            驱动 = new GravDrive(当前驾驶舱, 重力引擎, 当前朝向);
        }

        // ── 主入口 ────────────────────────────────────────────────────────────
        public void Main(string arg, UpdateType updateSource)
        {
            // 读取上次运行耗时（μs），更新滚动平均缓冲
            更新耗时统计();

            // 逐帧控制
            逐帧更新();

            // 方块列表刷新（低频）
            _刷新计数++;
            if (_刷新计数 >= 参数.更新间隔 || arg == "UpdateBlocks")
            {
                获取方块();
                初始化驱动();
                _刷新计数 = 0;
            }

            // 显示刷新（中频）
            _显示计数++;
            if (_显示计数 >= 参数.显示刷新间隔)
            {
                更新显示();
                _显示计数 = 0;
            }

            // ── 指令 ──────────────────────────────────────────────────────────
            if (arg == "SwitchGrav")
                引擎开关 = !引擎开关;

            if (arg == "ChangeFacing")
            {
                当前朝向 = (当前朝向 == 参数.默认朝向) ? 参数.备选朝向 : 参数.默认朝向;
                初始化驱动();
            }
            else if (arg.StartsWith("SetFacing"))
            {
                int sp = arg.IndexOf(' ');
                当前朝向 = sp >= 0 ? arg.Substring(sp).Trim() : "Front";
                if (string.IsNullOrEmpty(当前朝向)) 当前朝向 = "Front";
                初始化驱动();
            }
        }

        // ── 逐帧控制 ──────────────────────────────────────────────────────────
        void 逐帧更新()
        {
            // 检查是否切换了正在操控的驾驶舱
            var 当前操控 = 驾驶舱列表.Find(x => x.IsUnderControl);
            if (当前操控 != null && 当前操控 != 当前驾驶舱)
            {
                当前驾驶舱 = 当前操控;
                初始化驱动();
            }

            if (驱动 == null || 当前驾驶舱 == null)
                return;

            if (!引擎开关)
            {
                驱动.紧急关闭();
                return;
            }

            Vector3D 世界速度 = 当前驾驶舱.GetShipVelocities().LinearVelocity;
            bool 阻尼         = 当前驾驶舱.DampenersOverride;

            驱动.计算期望(当前驾驶舱.MoveIndicator, 世界速度, 阻尼,
                      参数.最大出力加速度, 参数.停止阈值,
                      参数.低速区间阈值, 参数.比例常数K);
            驱动.执行写入(参数.每帧最大写入数);
        }

        // ── 性能监控 ──────────────────────────────────────────────────────────
        void 更新耗时统计()
        {
            double 本次耗时 = Runtime.LastRunTimeMs * 1000.0; // 转换为 μs
            _耗时总和 -= _耗时缓冲[_耗时指针];
            _耗时缓冲[_耗时指针] = 本次耗时;
            _耗时总和 += 本次耗时;
            _耗时指针 = (_耗时指针 + 1) % _耗时缓冲.Length;
        }

        void 更新显示()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"重力引擎：{(引擎开关 ? "开启" : "关闭")}");
            sb.AppendLine($"朝向：{当前朝向}");

            if (当前驾驶舱 != null)
            {
                sb.AppendLine($"速度：{Math.Round(当前驾驶舱.GetShipSpeed(), 1)} m/s");
                sb.AppendLine($"阻尼：{(当前驾驶舱.DampenersOverride ? "开" : "关")}");
            }
            else
                sb.AppendLine("错误：未找到驾驶舱，请执行 UpdateBlocks");

            sb.AppendLine($"重力发生器：{重力引擎.Count} 个");

            if (驱动 != null)
                sb.AppendLine($"写入：{驱动.本次写入}/{参数.每帧最大写入数}  待写：{驱动.待写入}");

            double 平均耗时 = _耗时总和 / _耗时缓冲.Length;
            sb.AppendLine($"耗时：{Math.Round(平均耗时)} us (avg{_耗时缓冲.Length}f)");
            sb.AppendLine($"指令数：{Runtime.CurrentInstructionCount}/{Runtime.MaxInstructionCount}");

            string 显示文本 = sb.ToString();
            Echo(显示文本);
            foreach (var 屏 in _显示屏)
                屏.WriteText(显示文本);
        }
    }
}
