using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;

namespace Grabadora
{
    public class MasterEqForm : Form
    {
        private AudioEngine _engine;
        private VerticalFaderControl[] _faders = new VerticalFaderControl[10];
        private Label[] _valLabels = new Label[10];
        private ComboBox _cbPresets;
        private readonly float[] _frequencies = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private readonly string[] _freqLabels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
        
        private readonly Dictionary<string, float[]> _presets = new Dictionary<string, float[]>
        {
            { "Flat", new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            { "Rock", new float[] { 5, 4, 3, 1, -1, -1, 1, 3, 4, 5 } },
            { "Pop", new float[] { 2, 3, 4, 3, 1, 0, 1, 2, 3, 2 } },
            { "Jazz", new float[] { 3, 3, 2, 2, 4, 4, 3, 2, 3, 4 } },
            { "Classical", new float[] { 0, 0, 0, 0, 0, 0, -2, -2, -2, -4 } },
            { "Dance", new float[] { 7, 6, 4, 0, 0, 0, 0, 2, 4, 5 } },
            { "Full Bass", new float[] { 9, 9, 9, 6, 3, 0, -3, -6, -9, -9 } },
            { "Bright", new float[] { -4, -3, -2, -1, 0, 2, 4, 6, 8, 9 } }
        };

        public MasterEqForm(AudioEngine engine)
        {
            _engine = engine;
            InitializeComponent();
            LoadValues();
        }

        private void InitializeComponent()
        {
            this.Text = "Ecualizador Maestro (10 Bandas)";
            this.Size = new Size(600, 350);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;

            int startX = 20;
            int spacing = 55;

            for (int i = 0; i < 10; i++)
            {
                int bandIndex = i;
                int x = startX + (i * spacing);

                // Etiqueta de Frecuencia (Arriba)
                var lblFreq = new Label
                {
                    Text = _freqLabels[i],
                    Location = new Point(x, 10),
                    AutoSize = false,
                    Width = 40,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.LightGray,
                    Font = new Font("Segoe UI", 8)
                };

                // Fader
                var fader = new VerticalFaderControl
                {
                    Location = new Point(x + 10, 35),
                    Size = new Size(20, 150),
                    Value = 0.5f // 0dB por defecto (Rango 0.0 a 1.0 mapeado a -12 a +12)
                };

                // Etiqueta de Valor (Abajo)
                var lblVal = new Label
                {
                    Text = "0.0",
                    Location = new Point(x, 195),
                    AutoSize = false,
                    Width = 40,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.Cyan,
                    Font = new Font("Segoe UI", 8)
                };

                fader.ValueChanged += (s, e) =>
                {
                    // Mapear 0.0-1.0 a -12dB - +12dB
                    float db = (fader.Value * 24.0f) - 12.0f;
                    lblVal.Text = $"{db:F1}";
                    _engine.SetMasterEqualizerBand(bandIndex, db);
                };

                _faders[i] = fader;
                _valLabels[i] = lblVal;

                this.Controls.Add(lblFreq);
                this.Controls.Add(fader);
                this.Controls.Add(lblVal);
            }

            // Presets
            var lblPresets = new Label { Text = "Preset:", Location = new Point(20, 240), AutoSize = true, ForeColor = Color.LightGray };
            _cbPresets = new ComboBox { Location = new Point(70, 237), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach(var key in _presets.Keys) _cbPresets.Items.Add(key);
            _cbPresets.SelectedIndexChanged += (s, e) => ApplyPreset(_cbPresets.SelectedItem.ToString());
            
            this.Controls.Add(lblPresets);
            this.Controls.Add(_cbPresets);
        }

        private void ApplyPreset(string name)
        {
            if (_presets.TryGetValue(name, out float[] gains))
            {
                for (int i = 0; i < 10; i++)
                {
                    // Mapear -12dB - +12dB a 0.0-1.0
                    float val = (gains[i] + 12.0f) / 24.0f;
                    if (_faders[i] != null) _faders[i].Value = val;
                }
            }
        }

        private void LoadValues()
        {
            float[] gains = _engine.GetMasterEqGains();
            if (gains == null) return;

            for (int i = 0; i < 10 && i < gains.Length; i++)
            {
                // Mapear -12dB - +12dB a 0.0-1.0
                float val = (gains[i] + 12.0f) / 24.0f;
                _faders[i].Value = val;
                _valLabels[i].Text = $"{gains[i]:F1}";
            }
        }
    }
}