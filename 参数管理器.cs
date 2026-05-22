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
            /// <summary>主循环跳过帧数（6 = 每秒 10 次）</summary>
            public int 跳过帧    { get; set; } = 6;
            /// <summary>ChangeSkip 切换到的备选跳过帧（1 = 每秒 60 次）</summary>
            public int 备选跳过帧 { get; set; } = 1;
            /// <summary>方块列表自动刷新间隔（ticks，600 = 每 10 秒）</summary>
            public int 更新间隔  { get; set; } = 600;
            #endregion

            #region 重力引擎参数
            /// <summary>飞船低于此速度(m/s)时重力引擎不参与推进</summary>
            public float 速度阈值 { get; set; } = 1f;
            /// <summary>从速度阈值到此速度(m/s)范围内重力引擎按比例出力</summary>
            public float 比例阈值 { get; set; } = 10f;
            /// <summary>推力同步到重力引擎的放大倍率（m/s²，建议 10~20）</summary>
            public float 推力倍率 { get; set; } = 10f;
            /// <summary>手动操控时额外叠加的重力方向增益（m/s²，建议 5~15）</summary>
            public float 操控增益 { get; set; } = 10f;
            /// <summary>始终叠加的额外重力向量：前后, 左右, 上下（m/s²，默认全 0）</summary>
            public Vector3D 额外重力 { get; set; } = Vector3D.Zero;
            #endregion

            #region 朝向预设
            /// <summary>默认飞船朝向（Front/Back/Left/Right/Up/Down）</summary>
            public string 默认朝向 { get; set; } = "Front";
            /// <summary>ChangeFacing 切换到的备选朝向</summary>
            public string 备选朝向 { get; set; } = "Left";
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

                注册("跳过帧",
                    () => 跳过帧.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 跳过帧 = x; },
                    "主循环跳过帧数（6 = 每秒 10 次）");

                注册("备选跳过帧",
                    () => 备选跳过帧.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 备选跳过帧 = x; },
                    "ChangeSkip 切换到的备选跳过帧（1 = 每秒 60 次）");

                注册("更新间隔",
                    () => 更新间隔.ToString(),
                    v  => { int x; if (int.TryParse(v, out x) && x > 0) 更新间隔 = x; },
                    "方块列表自动刷新间隔（ticks，600 = 每 10 秒）");

                注册("速度阈值",
                    () => 速度阈值.ToString(),
                    v  => { float x; if (float.TryParse(v, out x)) 速度阈值 = x; },
                    "飞船低于此速度(m/s)时重力引擎不参与推进");

                注册("比例阈值",
                    () => 比例阈值.ToString(),
                    v  => { float x; if (float.TryParse(v, out x) && x > 0) 比例阈值 = x; },
                    "从速度阈值到此速度(m/s)范围内重力引擎按比例出力");

                注册("推力倍率",
                    () => 推力倍率.ToString(),
                    v  => { float x; if (float.TryParse(v, out x) && x >= 0) 推力倍率 = x; },
                    "推力同步到重力引擎的放大倍率(m/s²，建议 10~20)");

                注册("操控增益",
                    () => 操控增益.ToString(),
                    v  => { float x; if (float.TryParse(v, out x) && x >= 0) 操控增益 = x; },
                    "手动操控时额外叠加的重力方向增益(m/s²，建议 5~15)");

                注册("额外重力",
                    () => FormatV3(额外重力),
                    v  => { Vector3D? r = ParseV3(v); if (r.HasValue) 额外重力 = r.Value; },
                    "始终叠加的额外重力向量：前后, 左右, 上下 (m/s²)，默认 0, 0, 0");

                注册("默认朝向",
                    () => 默认朝向,
                    v  => 默认朝向 = v.Trim(),
                    "默认飞船朝向（Front/Back/Left/Right/Up/Down）");

                注册("备选朝向",
                    () => 备选朝向,
                    v  => 备选朝向 = v.Trim(),
                    "ChangeFacing 切换到的备选朝向");
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

            // ── 5. 辅助方法 ───────────────────────────────────────────────────

            static string FormatV3(Vector3D v)
                => v.X + ", " + v.Y + ", " + v.Z;

            static Vector3D? ParseV3(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                string[] p = s.Split(',');
                if (p.Length != 3) return null;
                double x, y, z;
                if (double.TryParse(p[0].Trim(), out x) &&
                    double.TryParse(p[1].Trim(), out y) &&
                    double.TryParse(p[2].Trim(), out z))
                    return new Vector3D(x, y, z);
                return null;
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
