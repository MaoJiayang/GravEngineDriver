using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        // ── 参数 ──────────────────────────────────────────────────────────────
        参数管理器 参数;

        // ── 方块列表 ──────────────────────────────────────────────────────────
        List<IMyGravityGenerator> Grav      = new List<IMyGravityGenerator>();
        List<IMyShipController>   Cs        = new List<IMyShipController>();
        List<IMyThrust>           Thrusters = new List<IMyThrust>();
        IMyShipController Cs0;

        // ── 运行时状态 ────────────────────────────────────────────────────────
        bool   GravOn        = true;
        int    skip;
        long   timetick      = 0;
        string currentFacing;
        GravDrive 驱动;

        public Program()
        {
            参数          = new 参数管理器(Me);
            skip          = 参数.跳过帧;
            currentFacing = 参数.默认朝向;
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
            if (!Grav.Any()) GridTerminalSystem.GetBlocksOfType(Grav);

            var cpGroup = GridTerminalSystem.GetBlockGroupWithName(参数.驾驶舱组);
            Cs.Clear();
            if (cpGroup != null) cpGroup.GetBlocksOfType(Cs, b => b.CanControlShip && b.IsFunctional);
            if (!Cs.Any()) GridTerminalSystem.GetBlocksOfType(Cs, b => b.CanControlShip && b.IsFunctional);

            Thrusters.Clear();
            GridTerminalSystem.GetBlocksOfType(Thrusters);

            Cs0 = Cs.Find(x => x.IsUnderControl);
            if (Cs0 == null && Cs.Count > 0) Cs0 = Cs[0];
        }

        void InitDrive()
        {
            if (Cs0 == null) { 驱动 = null; return; }
            驱动 = new GravDrive(Cs0, Thrusters, Grav, currentFacing);
        }

        // ── 主入口 ────────────────────────────────────────────────────────────
        public void Main(string arg, UpdateType updateSource)
        {
            if (timetick % skip == 0 || updateSource != UpdateType.Update1)
                Update();

            if (timetick % 参数.更新间隔 == 0 || arg == "UpdateBlocks")
            {
                GetBlocks();
                InitDrive();
            }

            // ── 指令 ──────────────────────────────────────────────────────────
            if (arg == "SwitchGrav")
                GravOn = !GravOn;

            if (arg == "ChangeSkip")
                skip = (skip == 参数.跳过帧) ? 参数.备选跳过帧 : 参数.跳过帧;
            else if (arg.StartsWith("SetSkip "))
            {
                int v;
                if (int.TryParse(arg.Substring(8).Trim(), out v) && v > 0) skip = v;
            }

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

            timetick++;
        }

        // ── 逐帧更新 ──────────────────────────────────────────────────────────
        void Update()
        {
            var active = Cs.Find(x => x.IsUnderControl);
            if (active != null && active != Cs0)
            {
                Cs0 = active;
                InitDrive();
            }

            if (驱动 == null || Cs0 == null)
            {
                Echo("错误：未找到驾驶舱\n请执行 UpdateBlocks 指令");
                return;
            }

            double speed = Cs0.GetShipSpeed();
            Echo($"重力引擎：{(GravOn ? "开启" : "关闭")}");
            Echo($"运行频率：{Math.Round(60.0 / skip, 1)}/s  (skip={skip})");
            Echo($"船体朝向：{currentFacing}");
            Echo($"当前速度：{Math.Round(speed, 1)} m/s");
            Echo($"重力发生器：{Grav.Count} 个 | 推进器：{Thrusters.Count} 个");

            if (!GravOn)
            {
                驱动.SetGravOverride(Vector3D.Zero);
                return;
            }

            Vector3D offset = 参数.额外重力;
            Vector3D vel    = Cs0.GetShipVelocities().LinearVelocity;
            double   s      = 参数.速度阈值;
            double   p      = 参数.比例阈值;
            double   mult   = 参数.推力倍率;

            // 速度越快倍率越高，从速度阈值线性爬升到比例阈值后达到最大
            Vector3D multiplier = new Vector3D(mult, mult, mult) *
                Vector3D.Clamp((Vector3D.Abs(vel) - s) / p, Vector3D.Zero, Vector3D.One);

            if (Cs0.MoveIndicator.Length() > 0.1f)
            {
                // 手动操控：叠加输入方向的额外推力
                Vector3D inputDir = Vector3D.Normalize(
                    new Vector3D(Cs0.MoveIndicator.Z, Cs0.MoveIndicator.X, -Cs0.MoveIndicator.Y));
                驱动.GravSyncWithThrusters(multiplier, offset + inputDir * 参数.操控增益);
            }
            else if (speed > s)
                驱动.GravSyncWithThrusters(multiplier, offset);
            else
                驱动.GravSyncWithThrusters(Vector3D.Zero, offset);
        }
    }
}
