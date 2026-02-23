using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace Grabadora
{
    public class MixerForm : Form
    {
        private readonly AudioEngine _engine;
        private readonly Timer _updateTimer;
        private readonly FlowLayoutPanel _channelsPanel;
        private readonly List<ChannelStrip> _channels = new List<ChannelStrip>();

        private class ChannelStrip
        {
            public int TrackIndex { get; set; }
            public Label NameLabel { get; set; } = null!;
            public VerticalFaderControl Fader { get; set; } = null!;
            public KnobControl PanKnob { get; set; } = null!;
            public Button MuteButton { get; set; } = null!;
            public Button SoloButton { get; set; } = null!;
            public VuMeterControl VuMeter { get; set; } = null!;
            public Button MoveUpButton { get; set; } = null!;
            public Button MoveDownButton { get; set; } = null!;
        }

        public MixerForm(AudioEngine engine)
        {
            _engine = engine;

            Text = "Mixer";
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Size = new Size(800, 350);
            StartPosition = FormStartPosition.CenterParent;

            _channelsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(10)
            };

            Controls.Add(_channelsPanel);

            BuildChannels();

            _updateTimer = new Timer();
            _updateTimer.Interval = 50; // ~20 FPS
            _updateTimer.Tick += (s, e) => UpdateLevels();
            _updateTimer.Start();

            FormClosed += (s, e) => _updateTimer.Stop();
        }

        private void BuildChannels()
        {
            _channelsPanel.Controls.Clear();
            _channels.Clear();

            for (int i = 0; i < _engine.TrackCount; i++)
            {
                var strip = CreateChannelStrip(i);
                _channels.Add(strip);
            }
        }

        private ChannelStrip CreateChannelStrip(int trackIndex)
        {
            var stripPanel = new Panel
            {
                Width = 110,
                Height = 260,
                Margin = new Padding(5),
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var nameLabel = new Label
            {
                Text = _engine.GetTrackName(trackIndex),
                ForeColor = Color.White,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 24
            };

            var vu = new VuMeterControl
            {
                Width = 40,
                Height = 40,
                Location = new Point((stripPanel.Width - 40) / 2, 30)
            };

            var fader = new VerticalFaderControl
            {
                Location = new Point((stripPanel.Width - 20) / 2, 80),
                Size = new Size(20, 100),
                Value = _engine.GetTrackVolume(trackIndex)
            };
            fader.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _engine.RegisterUndoSnapshot(); };
            fader.ValueChanged += (s, e) => _engine.SetTrackVolume(trackIndex, fader.Value);

            var panKnob = new KnobControl
            {
                Minimum = -1.0f,
                Maximum = 1.0f,
                Value = _engine.GetTrackPan(trackIndex),
                Size = new Size(40, 40),
                Location = new Point((stripPanel.Width - 40) / 2, 190),
                IsBalance = true // Activar modo visual de balance
            };
            panKnob.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _engine.RegisterUndoSnapshot(); };
            panKnob.ValueChanged += (s, e) => _engine.SetTrackPan(trackIndex, panKnob.Value);

            var muteButton = new Button
            {
                Text = "M",
                Size = new Size(30, 22),
                Location = new Point(10, 230),
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            muteButton.FlatAppearance.BorderSize = 0;
            muteButton.Click += (s, e) =>
            {
                _engine.RegisterUndoSnapshot();
                bool isMuted = _engine.ToggleTrackMute(trackIndex);
                muteButton.BackColor = isMuted ? Color.IndianRed : Color.FromArgb(70, 70, 70);
            };

            var soloButton = new Button
            {
                Text = "S",
                Size = new Size(30, 22),
                Location = new Point(stripPanel.Width - 40, 230),
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            soloButton.FlatAppearance.BorderSize = 0;
            soloButton.Click += (s, e) =>
            {
                _engine.RegisterUndoSnapshot();
                bool isSolo = _engine.ToggleTrackSolo(trackIndex);
                soloButton.BackColor = isSolo ? Color.Gold : Color.FromArgb(70, 70, 70);
                soloButton.ForeColor = isSolo ? Color.Black : Color.White;
            };

            // Botones de mover pista (reordenar canales)
            var btnUp = new Button
            {
                Text = "▲",
                Size = new Size(22, 18),
                Location = new Point(5, 5),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnUp.FlatAppearance.BorderSize = 0;
            btnUp.Click += (s, e) =>
            {
                _engine.RegisterUndoSnapshot();
                _engine.MoveTrack(trackIndex, -1);
                BuildChannels();
            };

            var btnDown = new Button
            {
                Text = "▼",
                Size = new Size(22, 18),
                Location = new Point(stripPanel.Width - 27, 5),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnDown.FlatAppearance.BorderSize = 0;
            btnDown.Click += (s, e) =>
            {
                _engine.RegisterUndoSnapshot();
                _engine.MoveTrack(trackIndex, 1);
                BuildChannels();
            };

            stripPanel.Controls.Add(nameLabel);
            stripPanel.Controls.Add(btnUp);
            stripPanel.Controls.Add(btnDown);
            stripPanel.Controls.Add(vu);
            stripPanel.Controls.Add(fader);
            stripPanel.Controls.Add(panKnob);
            stripPanel.Controls.Add(muteButton);
            stripPanel.Controls.Add(soloButton);

            _channelsPanel.Controls.Add(stripPanel);

            return new ChannelStrip
            {
                TrackIndex = trackIndex,
                NameLabel = nameLabel,
                Fader = fader,
                PanKnob = panKnob,
                MuteButton = muteButton,
                SoloButton = soloButton,
                VuMeter = vu,
                MoveUpButton = btnUp,
                MoveDownButton = btnDown
            };
        }

        private void UpdateLevels()
        {
            foreach (var ch in _channels)
            {
                if (ch.TrackIndex < 0 || ch.TrackIndex >= _engine.TrackCount) continue;
                _engine.GetTrackLevels(ch.TrackIndex, out float left, out float right);
                ch.VuMeter.SetLevels(left, right);
                ch.NameLabel.Text = _engine.GetTrackName(ch.TrackIndex);
            }
        }
    }
}
