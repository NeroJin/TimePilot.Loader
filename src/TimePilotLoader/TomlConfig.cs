using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TimePilot.Loader
{
    /// <summary>
    /// 极简 TOML 键值编辑器：只认顶层的 <c>key = value</c>，不做完整 TOML 解析。
    ///
    /// 为什么不用完整的 TOML 库：我们只需要改四个标量键，而完整解析再序列化会
    /// 丢掉用户文件里的注释和顺序——ReXGlue 的 TimePilot.toml 恰恰是用注释写文档的
    /// （每个 cvar 上面一段说明），重排一次就没法读了。
    ///
    /// 行为约定：
    ///   · 已存在的活动行  → 原地改值（保留它在文件里的位置）
    ///   · 只有被注释掉的行（如 <c># resolution_scale = 1</c>）→ 就地解注释并改值
    ///   · 完全没有        → 追加到文件末尾
    ///   · 其它所有行、注释、空行一律原样保留
    /// </summary>
    internal sealed class TomlConfig
    {
        private static readonly Regex ActiveLine = new Regex(
            @"^(?<lead>[ \t]*)(?<key>[A-Za-z0-9_.\-]+)[ \t]*=[ \t]*(?<val>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex CommentedLine = new Regex(
            @"^(?<lead>[ \t]*)#[ \t]*(?<key>[A-Za-z0-9_.\-]+)[ \t]*=[ \t]*(?<val>.*)$",
            RegexOptions.Compiled);

        private readonly List<string> _lines;
        private readonly string _path;

        /// <summary>上一次 Set 是"追加到末尾"——连续追加时不再插空行，避免写出三段空行。</summary>
        private bool _lastWasAppend;

        private TomlConfig(string path, List<string> lines)
        {
            _path = path;
            _lines = lines;
        }

        /// <summary>
        /// 打开配置文件；文件不存在（或读不出来）时按模板建立一份。
        /// 模板只包含"能保证游戏正常跑起来 + 本启动器会控制"的键，不含任何调试/作弊类选项。
        /// </summary>
        public static TomlConfig LoadOrCreate(string path, string template)
        {
            if (!File.Exists(path))
            {
                var created = new TomlConfig(path, TemplateLines(template));
                created.Save();
                return created;
            }

            string[] raw;
            try
            {
                raw = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 读失败（被占用/权限）不能当成空文件——那样下次保存会把用户配置整个冲掉。
                raw = null;
            }

            // 读失败或文件是空的：用模板顶上，避免一保存就把文件写没。
            if (raw == null || raw.Length == 0)
            {
                return new TomlConfig(path, TemplateLines(template));
            }
            return new TomlConfig(path, new List<string>(raw));
        }

        private static List<string> TemplateLines(string template)
        {
            var lines = new List<string>(SplitLines(template));
            // 模板本身以换行结尾，Split 会留下一个空尾巴，去掉免得每次多一行空行。
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return lines;
        }

        public string GetString(string key, string fallback)
        {
            string raw;
            if (!TryGetRaw(key, out raw)) return fallback;
            return Unquote(raw);
        }

        public int GetInt(string key, int fallback)
        {
            string raw;
            if (!TryGetRaw(key, out raw)) return fallback;
            int value;
            if (int.TryParse(Unquote(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string text = GetString(key, null);
            if (text == null) return fallback;
            text = text.Trim();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
            bool value;
            if (bool.TryParse(text, out value)) return value;
            return fallback;
        }

        /// <summary>键是否存在且未注释（用于判断用户/上次的窗口策略）。</summary>
        public bool HasActive(string key)
        {
            foreach (var line in _lines)
            {
                Match m = ActiveLine.Match(line);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把键注释掉（保留原缩进和值，写成 "# key = value"）。
        /// 键本来就不存在或已注释时什么都不做。用于"把决定权还给运行时默认"。
        /// </summary>
        public void CommentOut(string key)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                Match m = ActiveLine.Match(_lines[i]);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    _lines[i] = m.Groups["lead"].Value + "# " + _lines[i].Trim();
                    return;
                }
            }
        }

        public void SetString(string key, string value)
        {
            Set(key, "\"" + Escape(value) + "\"");
        }

        public void SetInt(string key, int value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public void SetBool(string key, bool value)
        {
            Set(key, value ? "true" : "false");
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var line in _lines)
            {
                sb.Append(line);
                sb.Append("\r\n");
            }
            // UTF-8 无 BOM：和 ReXGlue 自带的那份配置保持一致，避免解析器把 BOM 当键名。
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------- 内部实现

        private void Set(string key, string serializedValue)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                Match m = ActiveLine.Match(_lines[i]);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    _lines[i] = key + " = " + serializedValue;
                    return;
                }
            }

            for (int i = 0; i < _lines.Count; i++)
            {
                Match m = CommentedLine.Match(_lines[i]);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    // 保留原缩进，去掉注释后写入新值。
                    _lines[i] = m.Groups["lead"].Value + key + " = " + serializedValue;
                    return;
                }
            }

            if (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length != 0 && !_lastWasAppend)
            {
                _lines.Add(string.Empty);
            }
            _lines.Add(key + " = " + serializedValue);
            _lastWasAppend = true;
        }

        private bool TryGetRaw(string key, out string raw)
        {
            raw = null;
            foreach (var line in _lines)
            {
                Match m = ActiveLine.Match(line);
                if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    raw = m.Groups["val"].Value;
                    return true;
                }
            }
            return false;
        }

        private static string Unquote(string raw)
        {
            string text = raw.Trim();
            if (text.Length >= 2 && text[0] == '"')
            {
                int end = text.LastIndexOf('"');
                if (end > 0)
                {
                    string inner = text.Substring(1, end - 1);
                    return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
                }
            }

            // 未加引号的值：# 之后是注释。
            int hash = text.IndexOf('#');
            if (hash >= 0) text = text.Substring(0, hash);
            return text.Trim();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>
        /// 新建配置时的模板。只写"游戏能启动"和"启动器会改"的键：
        /// gpu_plugin 缺失会黑屏；license_mask 决定 XBLA 的试玩限制；
        /// 其余由界面控制。不包含任何调试/作弊类选项。
        /// </summary>
        public static readonly string DefaultTemplate = string.Join(Environment.NewLine, new[]
        {
            "# TimePilot 运行时配置（ReXGlue 会读取 exe 同目录的这个文件）",
            "# 键名即 ReXGlue 的 cvar 名；改完之后需要重启游戏才生效。",
            "# 这个文件由 TimePilot 启动器维护，你也可以用记事本直接改，两边不冲突。",
            "",
            "# GPU 模拟插件（Xenos GPU -> D3D12）。删掉这项游戏能启动但黑屏。",
            "gpu_plugin = \"xenos\"",
            "",
            "# 许可证掩码，全 1 = 视为已购买完整版。",
            "# 游戏用 XamContentGetLicenseMask 向运行时询问购买状态，返回 0 就进试玩模式。",
            "# 必须写成十进制，写成 0xFFFFFFFF 解析会失败。",
            "license_mask = 4294967295",
            "",
            "# 输出分辨率预设（决定窗口大小，默认 720p）。勾选「窗口大小随渲染分辨率」时",
            "# 启动器会注释掉这一行（预设优先级高于 window_width/height），其余情况不碰它。",
            "resolution = \"720p\"",
            "",
            "# 全屏（无边框 + 桌面分辨率）。启动器里的「全屏」勾选框写的就是这一行。",
            "fullscreen = false",
            "",
            "# 内部渲染倍率：1 = 原生 1280x720，2/3/4 = 整数倍超采样",
            "resolution_scale = 1",
            "",
            "# 后期抗锯齿：none / fxaa / fxaa_extreme",
            "# 像素画推荐 none：FXAA 会把硬边糊掉，锯齿交给 resolution_scale 的超采样解决。",
            "present_effect = \"none\"",
            "",
            "# 键盘操作：把键盘模拟成一个 Xbox 手柄，固定喂给 1P，可以和实体手柄同时用。",
            "mnk_mode = true",
            "",
            "# 鼠标驱动右摇杆。本作不用右摇杆，保持关闭免得误操作。",
            "mnk_mouse = false",
            "",
            "# 键位用 keybind_* 配置（keybind_a / keybind_lstick_up / keybind_dpad_left ...）。",
            "# 键名由运行时解析，写错会静默失效：回车是 Enter、反引号是 GraveAccent、",
            "# 引号是 Apostrophe、方向键是 UpArrow/DownArrow。改键请用启动器的「键盘映射…」。",
            "",
            "# 垂直同步，必须保持开启。",
            "vsync = true",
            "",
            "# 撕裂 / 可变刷新率，必须保持关闭，否则游戏速度会失控。",
            "d3d12_allow_variable_refresh_rate_and_tearing = false",
        });
    }
}
