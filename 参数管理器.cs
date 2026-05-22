using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Text;
using VRageMath;

namespace IngameScript
{
    public partial class Program
    {
        /// <summary>
        /// 重力引擎驱动参数管理器。
        /// 所有可调参数均持久化到 PB 的 CustomData（key = value 格式）。
        /// 首次运行自动写入带注释的默认配置；修改 CustomData 后重新编译生效。
        /// </summary>
        public class 参数管理器
        {
            // ── 1. 参数属性（声明即注册，改默认值只需改这里）─────────────────

            #region 方块分组
            /// <summary>重力发生器分组名，留空则搜索全舰</summary>
            public string 重力引擎组 { get; set; } = "Grav";
            /// <summary>驾驶舱分组名，留空则搜索全舰</summary>
            public string 驾驶舱组  { get; set; } = "CP";
            #endregion

            #region 运行频率
            /// <summary>方块列表自动刷新间隔（ticks，600 = 每 10 秒）</summary>
            public int 更新间隔 { get; set; } = 600;
            #endregion

            #region 重力引擎参数
            /// <summary>重力发生器最大出力加速度（m/s²，建议 10~30）</summary>
            public float 最大出力加速度 { get; set; } = 20f;
            /// <summary>速度低于此值(m/s)时停止阻尼出力，防止低速抖振</summary>
            public float 停止阈值 { get; set; } = 3f;
            /// <summary>每帧最多写入的重力发生器数量（防止单帧尖峰，大船可调高）</summary>
            public int 每帧最大写入数 { get; set; } = 10;
            #endregion

            #region 朝向预设
            /// <summary>默认飞船朝向（Front/Back/Left/Right/Up/Down）</summary>
            public string 默认朝向 { get; set; } = "Front";
            /// <summary>ChangeFacing 切换到的备选朝向</summary>
            public string 备选朝向 { get; set; } = "Left";
            #endregion

            #region 性能显示
            /// <summary>显示器每隔多少 tick 刷新一次（6 ≈ 10 Hz）</summary>
            public int 显示刷新间隔 { get; set; } = 6;
            /// <summary>耗时滚动平均的窗口大小（帧数）</summary>
            public int 滚动窗口大小 { get; set; } = 60;
            /// <summary>性能面板名称（留空则仅用 Echo，非空则额外推送到对应 LCD）</summary>
            public string 性能显示面板名称 { get; set; } = "";
            #endregion

            // ── 2. 注册系统（框架层）──────────────────────────────────────────

            Dictionary<string, 参数描述符> 注册表;

            void 注册所有参数()
            {
                注册表 = new Dictionary<string, 参数描述符>();

                注册("重力引擎组",
                    () => 重力引擎组,
                    v  => 重力引擎组 = v,
                    "重力发生器所在分组名，留空则搜索全舰");

                注册("驾驶舱组",
                    () => 驾驶舱组,
                    v  => 驾驶舱组 = v,
                    "驾驶舱所在分组名，留空则搜索全舰");

                注册("更新间隔",
                    () => 更新间隔.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 更新间隔 = x; },
                    "方块列表自动刷新间隔（ticks，600 = 每 10 秒）");

                注册("最大出力加速度",
                    () => 最大出力加速度.ToString(),
                    v  => { float x; if (float.TryParse(v, out x) && x > 0) 最大出力加速度 = x; },
                    "单个重力发生器最大出力加速度(m/s²，一般为9.8)");

                注册("停止阈值",
                    () => 停止阈值.ToString(),
                    v  => { float x; if (float.TryParse(v, out x) && x >= 0) 停止阈值 = x; },
                    "速度低于此值(m/s)时停止阻尼出力，防止低速抖振");

                注册("每帧最大写入数",
                    () => 每帧最大写入数.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 每帧最大写入数 = x; },
                    "每帧最多写入的重力发生器数量");

                注册("默认朝向",
                    () => 默认朝向,
                    v  => 默认朝向 = v.Trim(),
                    "默认飞船朝向（Front/Back/Left/Right/Up/Down）");

                注册("备选朝向",
                    () => 备选朝向,
                    v  => 备选朝向 = v.Trim(),
                    "ChangeFacing 切换到的备选朝向");

                注册("显示刷新间隔",
                    () => 显示刷新间隔.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 显示刷新间隔 = x; },
                    "显示器每隔多少 tick 刷新一次（6 ≈ 10 Hz）");

                注册("滚动窗口大小",
                    () => 滚动窗口大小.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 滚动窗口大小 = x; },
                    "耗时滚动平均的窗口大小（帧数，60 = 最近 1 秒均值）");

                注册("性能显示面板名称",
                    () => 性能显示面板名称,
                    v  => 性能显示面板名称 = v.Trim(),
                    "性能面板名称（留空仅用 Echo，非空额外推送到对应 LCD）");
            }

            // ── 3. 构造函数（框架层）──────────────────────────────────────────

            public 参数管理器(IMyTerminalBlock block)
            {
                注册所有参数();
                string cd = block.CustomData;
                if (!string.IsNullOrWhiteSpace(cd))
                {
                    解析(cd);
                    block.CustomData = 生成(); // 补全新增参数
                }
                else
                    block.CustomData = 生成(); // 首次运行写入默认值
            }

            // ── 4. 序列化 / 反序列化（框架层）────────────────────────────────

            void 解析(string src)
            {
                foreach (string 行 in src.Split('\n'))
                {
                    string s = 行.Trim();
                    if (string.IsNullOrEmpty(s) || s.StartsWith("//") || s.StartsWith("#")) continue;
                    string[] kv = s.Split(new char[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    string key = kv[0].Trim(), val = kv[1].Trim();
                    参数描述符 desc;
                    if (注册表.TryGetValue(key, out desc))
                        try { desc.Set(val); } catch { }
                }
            }

            string 生成()
            {
                var sb = new StringBuilder();
                sb.AppendLine("// 重力引擎驱动参数配置");
                sb.AppendLine("// 修改后重新运行脚本或执行 UpdateBlocks 指令使其生效");
                sb.AppendLine();
                foreach (var kv in 注册表)
                {
                    if (!string.IsNullOrEmpty(kv.Value.Desc))
                        sb.AppendLine("// " + kv.Value.Desc);
                    sb.AppendLine(kv.Key + " = " + kv.Value.Get());
                    sb.AppendLine();
                }
                return sb.ToString();
            }

            void 注册(string key, Func<string> get, Action<string> set, string desc = "")
            {
                注册表[key] = new 参数描述符(get, set, desc);
            }
        }

        // ── 参数描述符（框架内部类）────────────────────────────────────────────
        class 参数描述符
        {
            public Func<string>   Get  { get; }
            public Action<string> Set  { get; }
            public string         Desc { get; }

            public 参数描述符(Func<string> get, Action<string> set, string desc)
            {
                Get = get; Set = set; Desc = desc;
            }
        }
    }
}
