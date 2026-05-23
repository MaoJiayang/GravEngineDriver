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
        List<IMyGravityGenerator> Grav = new List<IMyGravityGenerator>();
        List<IMyShipController>   Cs   = new List<IMyShipController>();
        IMyShipController Cs0;
        IMyTextPanel      _lcd;  // 可选性能显示面板（参数.性能显示面板名称 非空时查找）

        // ── 运行时状态 ────────────────────────────────────────────────────────
        bool   GravOn        = true;
        string currentFacing;
        GravDrive 驱动;

        // ── 计数器 ────────────────────────────────────────────────────────────
        int _updateCtr  = 0;  // 方块刷新计数器
        int _displayCtr = 0;  // 显示刷新计数器

        // ── 性能监控（滚动平均） ───────────────────────────────────────────────
        double[] _perfBuf;
        int      _perfHead;
        double   _perfSum;

        public Program()
        {
            参数          = new 参数管理器(Me);
            currentFacing = 参数.默认朝向;
            _perfBuf      = new double[参数.滚动窗口大小];
            GetBlocks();
            InitDrive();
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }

        // ── 方块查找 ──────────────────────────────────────────────────────────
        void GetBlocks()
        {
            var gravGroup = GridTerminalSystem.GetBlockGroupWithName(参数.重力引擎组);
            Grav.Clear();
            if (gravGroup != null) gravGroup.GetBlocksOfType(Grav);
            if (Grav.Count == 0)   GridTerminalSystem.GetBlocksOfType(Grav);

            var cpGroup = GridTerminalSystem.GetBlockGroupWithName(参数.驾驶舱组);
            Cs.Clear();
            if (cpGroup != null) cpGroup.GetBlocksOfType(Cs, b => b.CanControlShip && b.IsFunctional);
            if (Cs.Count == 0)   GridTerminalSystem.GetBlocksOfType(Cs, b => b.CanControlShip && b.IsFunctional);

            Cs0 = Cs.Find(x => x.IsUnderControl);
            if (Cs0 == null && Cs.Count > 0) Cs0 = Cs[0];

            // 可选 LCD 性能面板
            _lcd = null;
            if (!string.IsNullOrWhiteSpace(参数.性能显示面板名称))
                _lcd = GridTerminalSystem.GetBlockWithName(参数.性能显示面板名称) as IMyTextPanel;
        }

        void InitDrive()
        {
            if (Cs0 == null) { 驱动 = null; return; }
            驱动 = new GravDrive(Cs0, Grav, currentFacing);
        }

        // ── 主入口 ────────────────────────────────────────────────────────────
        public void Main(string arg, UpdateType updateSource)
        {
            // 读取上次运行耗时（μs），更新滚动平均缓冲
            UpdatePerfBuffer();

            // 逐帧控制
            Update();

            // 方块列表刷新（低频）
            _updateCtr++;
            if (_updateCtr >= 参数.更新间隔 || arg == "UpdateBlocks")
            {
                GetBlocks();
                InitDrive();
                _updateCtr = 0;
            }

            // 显示刷新（中频）
            _displayCtr++;
            if (_displayCtr >= 参数.显示刷新间隔)
            {
                UpdateDisplay();
                _displayCtr = 0;
            }

            // ── 指令 ──────────────────────────────────────────────────────────
            if (arg == "SwitchGrav")
                GravOn = !GravOn;

            if (arg == "ChangeFacing")
            {
                currentFacing = (currentFacing == 参数.默认朝向) ? 参数.备选朝向 : 参数.默认朝向;
                InitDrive();
            }
            else if (arg.StartsWith("SetFacing"))
            {
                int sp = arg.IndexOf(' ');
                currentFacing = sp >= 0 ? arg.Substring(sp).Trim() : "Front";
                if (string.IsNullOrEmpty(currentFacing)) currentFacing = "Front";
                InitDrive();
            }
        }

        // ── 逐帧控制 ──────────────────────────────────────────────────────────
        void Update()
        {
            // 检查是否切换了正在操控的驾驶舱
            var active = Cs.Find(x => x.IsUnderControl);
            if (active != null && active != Cs0)
            {
                Cs0 = active;
                InitDrive();
            }

            if (驱动 == null || Cs0 == null)
                return;

            if (!GravOn)
            {
                驱动.ShutDown();
                return;
            }

            Vector3D worldVel = Cs0.GetShipVelocities().LinearVelocity;
            bool dampeners    = Cs0.DampenersOverride;

            驱动.Apply(Cs0.MoveIndicator, worldVel, dampeners,
                      参数.最大出力加速度, 参数.停止阈值,
                      参数.低速区间阈值, 参数.比例常数K);
            驱动.FlushWrites(参数.每帧最大写入数);
        }

        // ── 性能监控 ──────────────────────────────────────────────────────────
        void UpdatePerfBuffer()
        {
            double sample = Runtime.LastRunTimeMs * 1000.0; // 转换为 μs
            _perfSum -= _perfBuf[_perfHead];
            _perfBuf[_perfHead] = sample;
            _perfSum += sample;
            _perfHead = (_perfHead + 1) % _perfBuf.Length;
        }

        void UpdateDisplay()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"重力引擎：{(GravOn ? "开启" : "关闭")}");
            sb.AppendLine($"朝向：{currentFacing}");

            if (Cs0 != null)
            {
                sb.AppendLine($"速度：{Math.Round(Cs0.GetShipSpeed(), 1)} m/s");
                sb.AppendLine($"阻尼：{(Cs0.DampenersOverride ? "开" : "关")}");
            }
            else
                sb.AppendLine("错误：未找到驾驶舱，请执行 UpdateBlocks");

            sb.AppendLine($"重力发生器：{Grav.Count} 个");

            if (驱动 != null)
                sb.AppendLine($"写入：{驱动.LastWrites}/{参数.每帧最大写入数}  待写：{驱动.PendingWrites}");

            double avgUs = _perfSum / _perfBuf.Length;
            sb.AppendLine($"耗时：{Math.Round(avgUs)} us (avg{_perfBuf.Length}f)");
            sb.AppendLine($"指令数：{Runtime.CurrentInstructionCount}/{Runtime.MaxInstructionCount}");

            string text = sb.ToString();
            Echo(text);
            if (_lcd != null)
                _lcd.WriteText(text);
        }
    }
}
