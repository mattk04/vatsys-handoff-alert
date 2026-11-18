using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace HandoffAlertPlugin
{
    public class VscsButtonInjector
    {
        private const string CustomButtonName = "btnPluginHandoffAlert";
        private const int ButtonWidth = 80;
        private const int ButtonHeight = 60;
        private const int Padding = 3;

        private readonly Action<bool> _onToggleChanged;
        private readonly Action _onTestAudioRequested;
        private readonly Timer _uiPollTimer;
        private bool _isEngaged = true;
        private Control _vscsPanel;
        private HandoffVscsButton _handoffButton;
        private FieldInfo _currentPageField;

        public VscsButtonInjector(Action<bool> onToggleChanged, Action onTestAudioRequested)
        {
            _onToggleChanged = onToggleChanged;
            _onTestAudioRequested = onTestAudioRequested;

            _uiPollTimer = new Timer();
            _uiPollTimer.Interval = 50;
            _uiPollTimer.Tick += OnUiPollTick;
        }

        public void Start()
        {
            _uiPollTimer.Start();
        }

        private void OnUiPollTick(object sender, EventArgs e)
        {
            try
            {
                Control panel = FindVscsPanel();
                if (panel == null || panel.IsDisposed)
                    return;

                if (panel != _vscsPanel || _handoffButton == null || _handoffButton.IsDisposed)
                    Attach(panel);

                PositionButton(panel);
                UpdateButtonVisibility();
                _handoffButton.BringToFront();
            }
            catch (Exception)
            {
                // Cosmetic injector; transient VSCS initialization failures are not plugin failures.
            }
        }

        private Control FindVscsPanel()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo windowField = typeof(vatsys.MMI).GetField("VSCSWindow", flags);
            if (windowField == null)
                return null;

            Form window = windowField.GetValue(null) as Form;
            if (window == null || window.IsDisposed)
                return null;

            Assembly vatSysAssembly = typeof(vatsys.MMI).Assembly;
            Type panelType = vatSysAssembly.GetType("vatsys.VSCSControlDX");
            if (panelType == null)
                return null;

            foreach (Control control in window.Controls)
            {
                if (control.GetType() == panelType)
                    return control;
            }

            return null;
        }

        private bool IsSetupPage(Control panel)
        {
            if (panel == null)
                return false;

            if (_currentPageField == null)
            {
                Type panelType = typeof(vatsys.MMI).Assembly.GetType("vatsys.VSCSControlDX");
                _currentPageField = panelType?.GetField("currentPageType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            object page = _currentPageField?.GetValue(panel);
            return page != null &&
                   string.Equals(page.ToString(), "Setup", StringComparison.OrdinalIgnoreCase);
        }

        private void Attach(Control panel)
        {
            if (_vscsPanel != null && !_vscsPanel.IsDisposed)
                _vscsPanel.Invalidated -= OnVscsPanelInvalidated;

            if (_handoffButton != null && !_handoffButton.IsDisposed)
                _handoffButton.Dispose();

            _vscsPanel = panel;
            _vscsPanel.Invalidated += OnVscsPanelInvalidated;
            _handoffButton = new HandoffVscsButton
            {
                Name = CustomButtonName,
                Text = "Handoff\nAlert",
                Visible = false
            };
            _handoffButton.Click += (s, args) =>
            {
                _isEngaged = !_isEngaged;
                _handoffButton.IsEngaged = _isEngaged;
                _onToggleChanged?.Invoke(_isEngaged);
            };
            _handoffButton.RightClicked += (s, args) => _onTestAudioRequested?.Invoke();
            panel.Controls.Add(_handoffButton);
        }

        private void OnVscsPanelInvalidated(object sender, InvalidateEventArgs e)
        {
            UpdateButtonVisibility();
        }

        private void UpdateButtonVisibility()
        {
            if (_vscsPanel == null || _vscsPanel.IsDisposed ||
                _handoffButton == null || _handoffButton.IsDisposed)
            {
                return;
            }

            bool visible = IsSetupPage(_vscsPanel);
            if (_handoffButton.Visible != visible)
                _handoffButton.Visible = visible;
        }

        private void PositionButton(Control panel)
        {
            int rows = Math.Max(1, (panel.ClientSize.Height - Padding - ButtonHeight) /
                (ButtonHeight + 2 * Padding) + 1);
            int frequencyCount = vatsys.Audio.VSCSFrequencies?.Count ?? 0;
            int lastFrequencyColumn = frequencyCount == 0 ? 0 : (frequencyCount - 1) / rows;
            int menuColumn = lastFrequencyColumn + 1;

            // Setup's Mute Input button is the fourth column after the menu column.
            _handoffButton.Bounds = new Rectangle(
                ColumnX(menuColumn + 4) - 1,
                RowY(1) - 1,
                ButtonWidth + 2,
                ButtonHeight + 2);
        }

        private static int ColumnX(int column)
        {
            return Padding + column * (ButtonWidth + 2 * Padding);
        }

        private static int RowY(int row)
        {
            return Padding + row * (ButtonHeight + 2 * Padding);
        }
    }

    internal sealed class HandoffVscsButton : Control
    {
        private bool _isEngaged = true;

        public bool IsEngaged
        {
            get { return _isEngaged; }
            set
            {
                if (_isEngaged != value)
                {
                    _isEngaged = value;
                    Invalidate();
                }
            }
        }

        public event EventHandler RightClicked;

        public HandoffVscsButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && ClientRectangle.Contains(e.Location))
                RightClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ClientRectangle;
            var backgroundColor = Colours.GetColour(_isEngaged
                ? Colours.Identities.VSCSButtonDepressed
                : Colours.Identities.VSCSBackground);
            using (var background = new SolidBrush(backgroundColor))
            {
                g.FillRectangle(background, rect);
            }

            var foreColor = Colours.GetColour(Colours.Identities.InteractiveText);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                rect,
                foreColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak |
                TextFormatFlags.NoPrefix);

            var bevel = Colours.GetColour(Colours.Identities.VSCSBackground);
            using (var topLeft = new Pen(_isEngaged
                       ? ControlPaint.DarkDark(bevel)
                       : ControlPaint.LightLight(bevel), 2f))
            using (var bottomRight = new Pen(_isEngaged
                       ? ControlPaint.LightLight(bevel)
                       : ControlPaint.DarkDark(bevel), 2f))
            {
                g.DrawLine(topLeft, rect.Right - 1, rect.Y + 1, rect.X + 1, rect.Y + 1);
                g.DrawLine(topLeft, rect.X + 1, rect.Y + 1, rect.X + 1, rect.Bottom - 1);
                g.DrawLine(bottomRight, rect.X + 1, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1);
                g.DrawLine(bottomRight, rect.Right - 1, rect.Bottom - 1, rect.Right - 1, rect.Y + 1);
            }
        }
    }
}