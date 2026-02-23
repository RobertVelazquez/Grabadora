using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    public class MasterLimiterForm : Form
    {
        private AudioEngine _engine;
        private KnobControl _knobThresh, _knobRatio, _knobAttack, _knobRelease;

        public MasterLimiterForm(AudioEngine engine)
        {
            _engine = engine;
            InitializeComponent();
            LoadValues();
        }

        private void InitializeComponent()
        {
            this.Text = "Limitador Maestro";
            this.Size = new Size(450, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;

            var panel = new Panel { Dock = DockStyle.Fill };
            
            _knobThresh = CreateKnob(panel, "Thresh (dB)", 0, -60, 0, -0.2f);
            _knobRatio = CreateKnob(panel, "Ratio", 1, 1, 50, 20f);
            _knobAttack = CreateKnob(panel, "Attack (ms)", 2, 0.1f, 500, 1.0f);
            _knobRelease = CreateKnob(panel, "Release (ms)", 3, 10, 1000, 50f);

            this.Controls.Add(panel);
        }

        private KnobControl CreateKnob(Panel panel, string label, int index, float min, float max, float def)
        {
            int x = 20 + (index * 100);
            var knob = new KnobControl { Location = new Point(x, 40), Size = new Size(60, 60), Minimum = min, Maximum = max, Value = def };
            var lbl = new Label { Text = label, Location = new Point(x, 15), AutoSize = true, ForeColor = Color.LightGray };
            var valLbl = new Label { Text = $"{def:F1}", Location = new Point(x, 105), AutoSize = true, ForeColor = Color.Cyan };
            
            knob.ValueChanged += (s, e) => {
                valLbl.Text = $"{knob.Value:F1}";
                UpdateLimiter();
            };
            
            panel.Controls.Add(lbl);
            panel.Controls.Add(knob);
            panel.Controls.Add(valLbl);
            return knob;
        }

        private void UpdateLimiter()
        {
            if (_engine.MasterLimiter != null)
            {
                _engine.MasterLimiter.UpdateParameters(
                    _knobThresh.Value, 
                    _knobRatio.Value, 
                    _knobAttack.Value, 
                    _knobRelease.Value
                );
            }
        }

        private void LoadValues()
        {
            if (_engine.MasterLimiter != null)
            {
                _knobThresh.Value = _engine.MasterLimiter.GetThreshold();
                _knobRatio.Value = _engine.MasterLimiter.GetRatio();
                _knobAttack.Value = _engine.MasterLimiter.GetAttack();
                _knobRelease.Value = _engine.MasterLimiter.GetRelease();
            }
        }
    }
}