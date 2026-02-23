using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Grabadora
{
    public class EffectsForm : Form
    {
        public enum ViewMode { Equalizer, Effects }

        private AudioEngine _engine;
        private int _trackIndex;
        private ViewMode _mode;
        private TimeSpan? _selectionStart;
        private TimeSpan? _selectionEnd;

        // Referencias a controles para actualizaciones
        private KnobControl _knobDelayTime, _knobDelayFeedback, _knobDelayMix;
        private KnobControl _knobCompThresh, _knobCompRatio, _knobCompAttack, _knobCompRelease;
        private KnobControl _knobReverbMix, _knobReverbSize;
        private KnobControl _knobDistDrive, _knobDistMix;
        private KnobControl _knobChorusMix, _knobChorusDepth, _knobChorusRate;
        private ComboBox _cbFilterType;
        private KnobControl _knobFilterCutoff;
        private KnobControl[] _eqKnobs = new KnobControl[5];

        public EffectsForm(AudioEngine engine, int trackIndex, ViewMode mode)
            : this(engine, trackIndex, mode, null, null)
        {
        }

        public EffectsForm(AudioEngine engine, int trackIndex, ViewMode mode, TimeSpan? selectionStart, TimeSpan? selectionEnd)
        {
            _engine = engine;
            _trackIndex = trackIndex;
            _mode = mode;
            _selectionStart = selectionStart;
            _selectionEnd = selectionEnd;
            InitializeComponent();
            LoadCurrentValues();
        }

        private void InitializeComponent()
        {
            string title = _mode == ViewMode.Equalizer ? "Ecualizador" : "Efectos";
            this.Text = $"{title} - Pista {_trackIndex + 1}";
            this.Size = _mode == ViewMode.Equalizer ? new Size(600, 250) : new Size(600, 750);
            
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };
            this.Controls.Add(layout);

            if (_mode == ViewMode.Equalizer)
            {
                // 1. EQ
                layout.Controls.Add(CreateHeader("Ecualizador"));
                var eqPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                string[] freqs = { "60Hz", "250Hz", "1kHz", "4kHz", "16kHz" };
                for (int i = 0; i < 5; i++)
                {
                    int band = i;
                    _eqKnobs[i] = CreateKnob(eqPanel, freqs[i], i, -12, 12, 0, (v) => _engine.SetEqualizerBand(_trackIndex, band, v));
                }
                layout.Controls.Add(eqPanel);
            }
            else
            {
                // 2. Delay
                layout.Controls.Add(CreateHeader("Delay"));
                var delayPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _knobDelayTime = CreateKnob(delayPanel, "Time (ms)", 0, 0, 2000, 250, (v) => UpdateDelay());
                _knobDelayFeedback = CreateKnob(delayPanel, "Feedback", 1, 0, 0.95f, 0.5f, (v) => UpdateDelay());
                _knobDelayMix = CreateKnob(delayPanel, "Mix", 2, 0, 1, 0.0f, (v) => UpdateDelay());
                AddApplyButton(delayPanel, async () => await _engine.ApplyDelayToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                layout.Controls.Add(delayPanel);

                // 3. Reverb
                layout.Controls.Add(CreateHeader("Reverb"));
                var reverbPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _knobReverbMix = CreateKnob(reverbPanel, "Mix", 0, 0, 1, 0, (v) => UpdateReverb());
                _knobReverbSize = CreateKnob(reverbPanel, "Size", 1, 0.1f, 0.99f, 0.5f, (v) => UpdateReverb());
                AddApplyButton(reverbPanel, async () => await _engine.ApplyReverbToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                layout.Controls.Add(reverbPanel);

                // 4. Compressor
                layout.Controls.Add(CreateHeader("Compresor"));
                var compPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _knobCompThresh = CreateKnob(compPanel, "Thresh", 0, -60, 0, 0, (v) => UpdateCompressor());
                _knobCompRatio = CreateKnob(compPanel, "Ratio", 1, 1, 20, 1, (v) => UpdateCompressor());
                _knobCompAttack = CreateKnob(compPanel, "Attack", 2, 1, 500, 10, (v) => UpdateCompressor());
                _knobCompRelease = CreateKnob(compPanel, "Release", 3, 10, 1000, 100, (v) => UpdateCompressor());
                AddApplyButton(compPanel, async () => await _engine.ApplyCompressorToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                layout.Controls.Add(compPanel);

                // 5. Filter
                layout.Controls.Add(CreateHeader("Filtro"));
                var filterPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _cbFilterType = new ComboBox { Location = new Point(20, 40), Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
                _cbFilterType.Items.AddRange(new object[] { "None", "LowPass", "HighPass" });
                _cbFilterType.SelectedIndexChanged += (s, e) =>
                {
                    _engine.RegisterUndoSnapshot();
                    UpdateFilter();
                };
                
                var lblType = new Label { Text = "Tipo", Location = new Point(20, 20), AutoSize = true, ForeColor = Color.LightGray };
                filterPanel.Controls.Add(lblType);
                filterPanel.Controls.Add(_cbFilterType);

                _knobFilterCutoff = CreateKnob(filterPanel, "Cutoff", 1, 20, 20000, 1000, (v) => UpdateFilter());
                _knobFilterCutoff.Location = new Point(150, 30); 
                AddApplyButton(filterPanel, async () => await _engine.ApplyFilterToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                
                layout.Controls.Add(filterPanel);

                // 6. Distortion (Nuevo)
                layout.Controls.Add(CreateHeader("Distorsión / Overdrive"));
                var distPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _knobDistDrive = CreateKnob(distPanel, "Drive", 0, 0, 1, 0, (v) => UpdateDistortion());
                _knobDistMix = CreateKnob(distPanel, "Mix", 1, 0, 1, 0, (v) => UpdateDistortion());
                AddApplyButton(distPanel, async () => await _engine.ApplyDistortionToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                layout.Controls.Add(distPanel);

                // 7. Chorus
                layout.Controls.Add(CreateHeader("Chorus / Flanger"));
                var chorusPanel = new Panel { Size = new Size(550, 100), BackColor = Color.FromArgb(50, 50, 50) };
                _knobChorusMix = CreateKnob(chorusPanel, "Mix", 0, 0, 1, 0, (v) => UpdateChorus());
                _knobChorusDepth = CreateKnob(chorusPanel, "Depth", 1, 0, 1, 0.2f, (v) => UpdateChorus());
                _knobChorusRate = CreateKnob(chorusPanel, "Rate (Hz)", 2, 0.1f, 5.0f, 1.0f, (v) => UpdateChorus());
                AddApplyButton(chorusPanel, async () => await _engine.ApplyChorusToSelectionAsync(_trackIndex, _selectionStart.Value, _selectionEnd.Value));
                layout.Controls.Add(chorusPanel);
            }
        }

        private Label CreateHeader(string text)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Orange, AutoSize = true, Margin = new Padding(0, 10, 0, 5) };
        }

        private KnobControl CreateKnob(Panel panel, string label, int index, float min, float max, float def, Action<float> onChange)
        {
            int x = 20 + (index * 100);
            var knob = new KnobControl { Location = new Point(x, 30), Size = new Size(50, 50), Minimum = min, Maximum = max, Value = def };
            var lbl = new Label { Text = label, Location = new Point(x, 10), AutoSize = true, ForeColor = Color.LightGray };
            var valLbl = new Label { Text = $"{def:F1}", Location = new Point(x, 85), AutoSize = true, ForeColor = Color.Cyan };
            
            knob.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left) _engine.RegisterUndoSnapshot();
            };
            
            knob.ValueChanged += (s, e) => {
                valLbl.Text = $"{knob.Value:F1}";
                onChange(knob.Value);
            };
            
            panel.Controls.Add(lbl);
            panel.Controls.Add(knob);
            panel.Controls.Add(valLbl);
            return knob;
        }

        private void AddApplyButton(Panel panel, Func<Task> applyAction)
        {
            var btnApply = new Button
            {
                Text = "Aplicar",
                Location = new Point(450, 40),
                Size = new Size(80, 30),
                BackColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat
            };
            
            bool hasSelection = _selectionStart.HasValue && _selectionEnd.HasValue && _selectionStart.Value != _selectionEnd.Value;
            btnApply.Enabled = hasSelection;
            
            btnApply.Click += async (s, e) => {
                await applyAction();
            };
            panel.Controls.Add(btnApply);
        }

        private void UpdateDelay() => _engine.SetDelayParameters(_trackIndex, (int)_knobDelayTime.Value, _knobDelayFeedback.Value, _knobDelayMix.Value);
        private void UpdateReverb() => _engine.SetReverbParameters(_trackIndex, _knobReverbMix.Value, _knobReverbSize.Value);
        private void UpdateCompressor() => _engine.SetCompressorParameters(_trackIndex, _knobCompThresh.Value, _knobCompRatio.Value, _knobCompAttack.Value, _knobCompRelease.Value);
        private void UpdateFilter() => _engine.SetFilterParameters(_trackIndex, _cbFilterType.SelectedIndex, _knobFilterCutoff.Value);
        private void UpdateDistortion() => _engine.SetDistortionParameters(_trackIndex, _knobDistDrive.Value, _knobDistMix.Value);
        private void UpdateChorus() => _engine.SetChorusParameters(_trackIndex, _knobChorusMix.Value, _knobChorusDepth.Value, _knobChorusRate.Value);

        private void LoadCurrentValues()
        {
            dynamic p = _engine.GetEffectParameters(_trackIndex);
            if (p == null) return;

            if (_mode == ViewMode.Equalizer)
            {
                float[] gains = p.EqGains;
                for(int i=0; i<5 && i<gains.Length; i++) 
                    if (_eqKnobs[i] != null) _eqKnobs[i].Value = gains[i];
            }
            else
            {
                if (_knobDelayTime != null)
                {
                    _knobDelayTime.Value = (float)p.Delay.Delay;
                    _knobDelayFeedback.Value = (float)p.Delay.Feedback;
                    _knobDelayMix.Value = (float)p.Delay.Mix;

                    _knobCompThresh.Value = (float)p.Compressor.Threshold;
                    _knobCompRatio.Value = (float)p.Compressor.Ratio;
                    _knobCompAttack.Value = (float)p.Compressor.Attack;
                    _knobCompRelease.Value = (float)p.Compressor.Release;

                    _cbFilterType.SelectedIndex = (int)p.Filter.Type;
                    _knobFilterCutoff.Value = (float)p.Filter.Cutoff;

                    _knobReverbMix.Value = (float)p.Reverb.Mix;
                    _knobReverbSize.Value = (float)p.Reverb.RoomSize;

                    if (p.GetType().GetProperty("Distortion") != null)
                    {
                        _knobDistDrive.Value = (float)p.Distortion.Drive;
                        _knobDistMix.Value = (float)p.Distortion.Mix;
                    }

                    if (p.GetType().GetProperty("Chorus") != null)
                    {
                        _knobChorusMix.Value = (float)p.Chorus.Mix;
                        _knobChorusDepth.Value = (float)p.Chorus.Depth;
                        _knobChorusRate.Value = (float)p.Chorus.Rate;
                    }
                }
            }
        }
    }
}