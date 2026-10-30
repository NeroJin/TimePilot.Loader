using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TimePilot.Unpack;

namespace TimePilot.Loader
{
    /// <summary>
    /// 启动器主窗体：解包资源 / 改画质设置 / 拉起游戏。
    ///
    /// 布局全部用 TableLayoutPanel + AutoSize 构建（不用像素坐标），
    /// 配合 AutoScaleMode.Font，在 125% / 150% / 200% 缩放下按比例放大而不错位。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---- 固定约定：loader 与游戏同目录 ----
        private const string GameExeName = "TimePilot.exe";
        private const string TomlName = "TimePilot.toml";
        private const string DataFolderName = "game";
        private const string ManagedAaIgnored = "none";

        /// <summary>
        /// 分辨率下拉的候选：内部渲染尺寸 = 720p 视频模式 × 整数倍。
        /// resolution_scale 是 int32_t，做不出 1.5x，所以只有整数倍。
        /// 窗口模式下可再勾「窗口大小随渲染分辨率」：窗口 = 渲染尺寸时画面 1:1
        /// 呈现（整数倍缩放，像素硬边无损）；不勾则 720p 窗口，渲染大了再缩回
        /// 来，相当于超采样。两条路都是整数倍缩放，不会糊。
        /// </summary>
        private static readonly ScaleOption[] ScaleOptions =
        {
            new ScaleOption(1, 1280,  720),
            new ScaleOption(2, 2560, 1440),
            new ScaleOption(3, 3840, 2160),
            new ScaleOption(4, 5120, 2880),
        };

        /// <summary>抗锯齿 cvar present_effect 的取值。</summary>
        private static readonly string[] AaValues = { "none", "fxaa", "fxaa_extreme" };
        private static readonly string[] AaKeys = { "AaNone", "AaFxaa", "AaFxaaExtreme" };

        private readonly string _baseDir;
        private readonly string _exePath;
        private readonly string _dataDir;
        private readonly TomlConfig _toml;

        /// <summary>布局根容器。窗体高度要按它的实际需求校准，否则加了新分组就会挤掉按钮行。</summary>
        private TableLayoutPanel _root;

        private ComboBox _scaleCombo;
        private ComboBox _aaCombo;
        private CheckBox _fullscreenCheck;
        /// <summary>仅窗口模式可用：勾上后窗口客户区 = 渲染尺寸（1:1 像素映射）。</summary>
        private CheckBox _matchWindowCheck;
        /// <summary>mnk_mode：键盘是否模拟成 1P 手柄。关掉后键盘完全不参与游戏。</summary>
        private CheckBox _keyboardCheck;
        private Label _dataStatus;
        private Label _hint;
        private ProgressBar _progress;
        private Button _extractButton;
        private Button _keyMapButton;
        private Button _cancelButton;
        private Button _launchButton;

        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _suspendSave;
        private bool _saveErrorShown;

        public MainForm(string baseDir)
        {
            _baseDir = baseDir;
            _exePath = Path.Combine(baseDir, GameExeName);
            _dataDir = Path.Combine(baseDir, DataFolderName);

            _toml = TomlConfig.LoadOrCreate(Path.Combine(baseDir, TomlName), TomlConfig.DefaultTemplate);

            BuildUi();
            LoadSettings();
            RefreshDataStatus();
        }

        // ================================================================== UI

        private void BuildUi()
        {
            var designFont = new Font(L10n.UiFontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

            // Font 模式下 AutoScaleDimensions 必须是"设计时 96 DPI"下该字体的度量，
            // 否则 150% 屏上会二次放大。这里现算一遍，换字体也不用改常数。
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = MeasureDesignFont(designFont);
            Font = designFont;

            Text = L10n.Get("AppTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ShowIcon = false;
            // 96 DPI 设计基准。三组内容实需约 388，留一点余量；其它 DPI 由 AutoScale
            // 按字体比例整体放大，OnLoad 里还会再按真实内容兜一次底。
            ClientSize = new Size(560, 400);
            Padding = new Padding(0);

            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12, 10, 12, 10)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 画质
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 输入
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 游戏数据
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 留白
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 按钮行

            _root.Controls.Add(BuildGraphicsGroup(), 0, 0);
            _root.Controls.Add(BuildInputGroup(), 0, 1);
            _root.Controls.Add(BuildDataGroup(), 0, 2);
            _root.Controls.Add(BuildButtonRow(), 0, 4);

            Controls.Add(_root);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToContent();
        }

        /// <summary>
        /// 按内容真实需要的高度校准窗体。必须在 AutoScale 完成之后调用（OnLoad 满足），
        /// 这样量到的是当前 DPI 下的实际像素，直接补差值即可，不会再被缩放一次。
        /// 字体比例缩放取整后常常比设计值高几像素，靠这一步兜住，避免按钮行被挤出窗口。
        /// </summary>
        private void FitToContent()
        {
            if (_root == null)
            {
                return;
            }

            int needed = _root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            if (needed > ClientSize.Height)
            {
                ClientSize = new Size(ClientSize.Width, needed);
            }
        }

        private GroupBox BuildGraphicsGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupGraphics"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 4
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            for (int i = 0; i < 4; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            // 分辨率
            grid.Controls.Add(MakeLabel("LabelScale"), 0, 0);
            _scaleCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 0, 3)
            };
            foreach (ScaleOption option in ScaleOptions)
            {
                _scaleCombo.Items.Add(option);
            }
            _scaleCombo.SelectedIndexChanged += OnScaleChanged;
            grid.Controls.Add(_scaleCombo, 1, 0);

            // 抗锯齿
            grid.Controls.Add(MakeLabel("LabelAa"), 0, 1);
            _aaCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 0, 3)
            };
            for (int i = 0; i < AaValues.Length; i++)
            {
                _aaCombo.Items.Add(new AaOption(AaValues[i], L10n.Get(AaKeys[i])));
            }
            _aaCombo.SelectedIndexChanged += OnSettingChanged;
            grid.Controls.Add(_aaCombo, 1, 1);

            // 抗锯齿的建议：像素画被后处理抹一遍会丢掉硬边，锯齿交给上面的超采样解决。
            var aaHint = new Label
            {
                Text = L10n.Get("HintAa"),
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 4)
            };
            grid.Controls.Add(aaHint, 0, 2);
            grid.SetColumnSpan(aaHint, 2);

            // 全屏（无边框 + 桌面分辨率；fullscreen_exclusive 保持默认关闭）
            _fullscreenCheck = new CheckBox
            {
                Text = L10n.Get("Fullscreen"),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            _fullscreenCheck.CheckedChanged += OnSettingChanged;
            grid.Controls.Add(_fullscreenCheck, 0, 3);

            // 窗口 = 渲染分辨率。只在窗口模式且渲染尺寸不超过桌面时可选。
            _matchWindowCheck = new CheckBox
            {
                Text = L10n.Get("MatchWindow"),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            _matchWindowCheck.CheckedChanged += OnSettingChanged;
            grid.Controls.Add(_matchWindowCheck, 1, 3);

            group.Controls.Add(grid);
            return group;
        }

        /// <summary>
        /// 输入组：只放一个开关（mnk_mode）。默认开着——和以前"强制打开"的行为一致；
        /// 只用实体手柄的人可以关掉，免得误碰键盘（尤其空格/分号）触发游戏动作。
        /// 键位本身在按钮行的「键盘映射…」里改。
        /// </summary>
        private GroupBox BuildInputGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupInput"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _keyboardCheck = new CheckBox
            {
                Text = L10n.Get("KeyboardOn"),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
            _keyboardCheck.CheckedChanged += OnSettingChanged;
            stack.Controls.Add(_keyboardCheck, 0, 0);

            var hint = new Label
            {
                Text = L10n.Get("HintKeyboard"),
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(18, 0, 0, 0)
            };
            stack.Controls.Add(hint, 0, 1);

            group.Controls.Add(stack);
            return group;
        }

        private GroupBox BuildDataGroup()
        {
            var group = new GroupBox
            {
                Text = L10n.Get("GroupData"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _dataStatus = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 4)
            };
            stack.Controls.Add(_dataStatus, 0, 0);

            _progress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 1000,
                Style = ProgressBarStyle.Continuous,
                Margin = new Padding(0, 2, 0, 4)
            };
            stack.Controls.Add(_progress, 0, 1);

            _hint = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = L10n.Get("HintIdle"),
                Margin = new Padding(0)
            };
            stack.Controls.Add(_hint, 0, 2);

            group.Controls.Add(stack);
            return group;
        }

        private Control BuildButtonRow()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0)
            };

            _launchButton = MakeButton("Launch", 132);
            _launchButton.Font = new Font(Font, FontStyle.Bold);
            _launchButton.Click += OnLaunchClick;

            _cancelButton = MakeButton("Cancel", 88);
            _cancelButton.Enabled = false;
            _cancelButton.Click += OnCancelClick;

            _extractButton = MakeButton("Extract", 132);
            _extractButton.Click += OnExtractClick;

            // 键盘映射：写 keybind_* 那一段。放在按钮行而不是新开一组，
            // 是因为它和"解包"一样是低频操作，不值得占主面板一行的高度。
            _keyMapButton = MakeButton("KeyMap", 116);
            _keyMapButton.Click += OnKeyMapClick;

            // RightToLeft 流式布局：先加的在最右，所以顺序是"启动 | 取消 | 解包 | 键盘映射"。
            flow.Controls.Add(_launchButton);
            flow.Controls.Add(_cancelButton);
            flow.Controls.Add(_extractButton);
            flow.Controls.Add(_keyMapButton);
            return flow;
        }

        private static Label MakeLabel(string key)
        {
            return new Label
            {
                Text = L10n.Get(key),
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 3, 6, 3)
            };
        }

        private static Button MakeButton(string key, int minWidth)
        {
            return new Button
            {
                Text = L10n.Get(key),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(minWidth, 30),
                Margin = new Padding(6, 0, 0, 0),
                UseVisualStyleBackColor = true
            };
        }

        /// <summary>按 96 DPI 计算字体的自动缩放基准尺寸（与 WinForms 内部度量方式一致）。</summary>
        internal static SizeF MeasureDesignFont(Font font)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                Size size = TextRenderer.MeasureText(
                    graphics,
                    "The quick brown fox jumped over the lazy dog",
                    font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding);
                return new SizeF(size.Width, size.Height);
            }
        }

        // ============================================================ 设置读写

        private void LoadSettings()
        {
            _suspendSave = true;
            try
            {
                int scale = _toml.GetInt("resolution_scale", 1);
                _scaleCombo.SelectedIndex = ScaleIndexFor(scale);
                _aaCombo.SelectedIndex = AaIndexOf(_toml.GetString("present_effect", ManagedAaIgnored));
                bool fullscreen = _toml.GetBool("fullscreen", false);
                _fullscreenCheck.Checked = fullscreen;
                // 缺这个键时按"开"处理：老 toml 里没有 mnk_mode，而以前启动器
                // 每次保存都会强写 true，所以缺省视为开才不会把人的键盘"弄没了"。
                _keyboardCheck.Checked = _toml.GetBool("mnk_mode", true);
                // 上次的窗口策略从文件反推：window_width 活动行 = 窗口随渲染分辨率。
                _matchWindowCheck.Checked = !fullscreen
                    && _toml.HasActive("window_width")
                    && _toml.HasActive("window_height");
            }
            finally
            {
                _suspendSave = false;
            }
            UpdateMatchEnabled();
        }

        private void OnSettingChanged(object sender, EventArgs e)
        {
            if (_suspendSave || _busy) return;
            UpdateMatchEnabled();
            SaveSettings(false);
        }

        /// <summary>分辨率下拉：换挡后「窗口随渲染分辨率」的可用性会变（3x/4x 可能超桌面）。</summary>
        private void OnScaleChanged(object sender, EventArgs e)
        {
            if (_suspendSave || _busy) return;
            UpdateMatchEnabled();
            SaveSettings(false);
        }

        /// <summary>
        /// 「窗口大小随渲染分辨率」的可用性：全屏时没意义（禁用但保留勾选状态，
        /// 退回窗口模式自动恢复）；渲染尺寸超过桌面时强制取消勾选——
        /// 否则写出的窗口比屏幕还大，标题栏按钮点不到。
        /// </summary>
        private void UpdateMatchEnabled()
        {
            bool fits = FitsDesktop(SelectedOption);
            _matchWindowCheck.Enabled = !_busy && !_fullscreenCheck.Checked && fits;
            if (!fits && _matchWindowCheck.Checked)
            {
                _suspendSave = true;
                try
                {
                    _matchWindowCheck.Checked = false;
                }
                finally
                {
                    _suspendSave = false;
                }
            }
        }

        private static bool FitsDesktop(ScaleOption option)
        {
            var screen = Screen.PrimaryScreen;
            if (screen == null) return true;
            return option.Width <= screen.Bounds.Width && option.Height <= screen.Bounds.Height;
        }

        /// <summary>把当前界面值写进 toml。silent=true 时不弹错误框（启动流程里自己处理）。</summary>
        private bool SaveSettings(bool silent)
        {
            // 用户手改过 gpu_plugin 就尊重；缺失/为空时补上，否则游戏会黑屏。
            if (_toml.GetString("gpu_plugin", string.Empty).Trim().Length == 0)
            {
                _toml.SetString("gpu_plugin", "xenos");
            }

            ScaleOption option = SelectedOption;

            _toml.SetInt("resolution_scale", option.Scale);
            _toml.SetString("present_effect", SelectedAa);
            _toml.SetBool("fullscreen", _fullscreenCheck.Checked);

            // 窗口策略：三条路互斥，靠注释/解注释 window_* 和 resolution 切换。
            if (_fullscreenCheck.Checked)
            {
                // 全屏走桌面分辨率，窗口尺寸键原样保留，不替用户做主。
            }
            else if (_matchWindowCheck.Checked)
            {
                // 窗口 = 渲染分辨率：1:1 像素映射，整数倍缩放最锐利。
                // resolution 预设的优先级高于 window_*，必须注释掉才轮得到下面两行。
                _toml.CommentOut("resolution");
                _toml.SetInt("window_width", option.Width);
                _toml.SetInt("window_height", option.Height);
            }
            else
            {
                // 固定 720p 窗口超采样：注释掉 window_*，窗口尺寸交给
                // resolution 预设（或运行时默认），F4 改过的预设不受影响。
                _toml.CommentOut("window_width");
                _toml.CommentOut("window_height");
            }

            // 强制项，不给界面开关：
            // 撕裂/可变刷新率会让呈现用 0 同步间隔，游戏速度会失控；
            // 垂直同步必须开着（当前运行时还没注册这个 cvar，写上是为了将来生效）。
            _toml.SetBool("d3d12_allow_variable_refresh_rate_and_tearing", false);
            _toml.SetBool("vsync", true);

            // 键盘：mnk_mode 开 = 键盘模拟成一个虚拟手柄喂 1P（键位见「键盘映射…」）。
            // 关掉后键盘完全不参与游戏，适合只用实体手柄、怕误碰键盘触发动作的人。
            // 鼠标驱动右摇杆，本作不用右摇杆，固定关掉免得误操作。
            _toml.SetBool("mnk_mode", _keyboardCheck.Checked);
            _toml.SetBool("mnk_mouse", false);

            try
            {
                _toml.Save();
                return true;
            }
            catch (Exception ex)
            {
                if (!silent && !_saveErrorShown)
                {
                    _saveErrorShown = true;
                    MessageBox.Show(this, L10n.Format("SaveFailed", ex.Message), Text,
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }
        }

        private ScaleOption SelectedOption
        {
            get
            {
                var option = _scaleCombo.SelectedItem as ScaleOption;
                return option ?? ScaleOptions[0];
            }
        }

        private string SelectedAa
        {
            get
            {
                var option = _aaCombo.SelectedItem as AaOption;
                return option != null ? option.Value : ManagedAaIgnored;
            }
        }

        /// <summary>把 toml 里的 resolution_scale 映射到下拉项；越界往最近的一挡收。</summary>
        private static int ScaleIndexFor(int scale)
        {
            if (scale <= 1) return 0;
            if (scale >= ScaleOptions.Length) return ScaleOptions.Length - 1;
            return scale - 1;
        }

        private static int AaIndexOf(string value)
        {
            for (int i = 0; i < AaValues.Length; i++)
            {
                if (string.Equals(AaValues[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        // ================================================================ 解包

        private async void OnExtractClick(object sender, EventArgs e)
        {
            await RunExtractAsync();
        }

        /// <summary>
        /// 让用户挑包。启动器目录里如果能自动找到包，就把它预选好——
        /// 常见情况下直接回车即可；找不到也无所谓，用户可以在别处挑。
        /// 默认筛选"所有文件"，因为 STFS 包名字通常是一串十六进制、没有扩展名。
        /// </summary>
        private string PickPackage()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = L10n.Get("PickPackageTitle"),
                Filter = L10n.Get("PickPackageFilter"),
                FilterIndex = 2,
                CheckFileExists = true,
                RestoreDirectory = true,
                Multiselect = false
            })
            {
                string found = TimePilotPack.FindPackage(_baseDir);
                if (found != null)
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(found);
                    dialog.FileName = Path.GetFileName(found);
                }
                else
                {
                    dialog.InitialDirectory = _baseDir;
                }

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private async Task<bool> RunExtractAsync()
        {
            if (_busy) return false;

            string package = PickPackage();
            if (package == null) return false;

            if (TimePilotPack.IsDataInstalled(_dataDir))
            {
                if (MessageBox.Show(this, L10n.Get("ConfirmOverwrite"), Text,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return false;
                }
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true);
            _progress.Value = 0;

            // Progress<T> 会回到创建它的 UI 线程，所以回调里可以安全碰控件。
            var progress = new Progress<ExtractProgress>(OnExtractProgress);

            try
            {
                ExtractResult result = await Task.Run(
                    () => TimePilotPack.Install(package, _dataDir, null, progress, cts.Token));

                _progress.Value = _progress.Maximum;
                _hint.Text = L10n.Format("ExtractDone", result.FileCount, result.ByteCount,
                                         result.Elapsed.TotalSeconds);
                RefreshDataStatus();
                return true;
            }
            catch (OperationCanceledException)
            {
                _hint.Text = L10n.Get("Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _hint.Text = L10n.Get("HintIdle");
                MessageBox.Show(this, L10n.Format("ExtractFailed", ex.Message), Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                SetBusy(false);
            }
        }

        private void OnExtractProgress(ExtractProgress progress)
        {
            int value = (int)Math.Round(progress.Percent * 10.0);
            _progress.Value = Math.Max(0, Math.Min(_progress.Maximum, value));
            _hint.Text = L10n.Format("ProgressItem", progress.FilesDone, progress.FilesTotal,
                                     progress.CurrentFile);
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cancelButton.Enabled = false;
            }
        }

        /// <summary>
        /// 键盘映射。改动由对话框自己写盘，这里不重复保存——
        /// 否则用一个旧界面的状态去 Save，反而可能把刚绑的键冲掉。
        /// </summary>
        private void OnKeyMapClick(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var dialog = new KeyBindDialog(_toml))
            {
                dialog.ShowDialog(this);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _scaleCombo.Enabled = !busy;
            _aaCombo.Enabled = !busy;
            _fullscreenCheck.Enabled = !busy;
            _keyboardCheck.Enabled = !busy;
            UpdateMatchEnabled();   // 匹配勾还要看全屏状态和桌面尺寸，单独算
            _extractButton.Enabled = !busy;
            _keyMapButton.Enabled = !busy;
            _launchButton.Enabled = !busy;
            _cancelButton.Enabled = busy;
            UseWaitCursor = busy;
            if (!busy && _hint.Text.Length == 0)
            {
                _hint.Text = L10n.Get("HintIdle");
            }
        }

        private void RefreshDataStatus()
        {
            if (TimePilotPack.IsDataInstalled(_dataDir))
            {
                long size = 0;
                try
                {
                    size = new FileInfo(Path.Combine(_dataDir, TimePilotPack.ExecutableName)).Length;
                }
                catch (IOException)
                {
                    // 只用来显示，读不到就当 0。
                }
                catch (UnauthorizedAccessException)
                {
                }
                _dataStatus.Text = L10n.Format("DataReady", size);
                _dataStatus.ForeColor = SystemColors.ControlText;
            }
            else
            {
                _dataStatus.Text = L10n.Get("DataMissing");
                _dataStatus.ForeColor = Color.FromArgb(176, 48, 32);
            }
        }

        // ================================================================ 启动

        private async void OnLaunchClick(object sender, EventArgs e)
        {
            if (_busy) return;

            if (!TimePilotPack.IsDataInstalled(_dataDir))
            {
                DialogResult answer = MessageBox.Show(this, L10n.Get("LaunchNeedsData"), Text,
                                                       MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                if (!await RunExtractAsync()) return;
            }

            if (!SaveSettings(false)) return;
            LaunchGame();
        }

        private void LaunchGame()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_exePath)
                {
                    // 工作目录必须是 exe 所在目录：日志和 toml 都按它定位。
                    WorkingDirectory = _baseDir,
                    UseShellExecute = true
                });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L10n.Format("LaunchFailed", ex.Message), Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================================================================ 选项类型

        /// <summary>下拉框里的一项，显示成 "2560x1440  2x 超采样"（1x 显示"原生"）。</summary>
        private sealed class ScaleOption
        {
            public readonly int Scale;
            public readonly int Width;
            public readonly int Height;

            public ScaleOption(int scale, int width, int height)
            {
                Scale = scale;
                Width = width;
                Height = height;
            }

            public override string ToString()
            {
                string size = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", Width, Height);
                string suffix = Scale == 1 ? L10n.Get("ScaleNative") : L10n.Format("ScaleSuper", Scale);
                return size + "  " + suffix;
            }
        }

        private sealed class AaOption
        {
            public readonly string Value;
            private readonly string _label;

            public AaOption(string value, string label)
            {
                Value = value;
                _label = label;
            }

            public override string ToString()
            {
                return _label;
            }
        }
    }
}
