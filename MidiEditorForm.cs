using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    /// <summary>
    /// Editor sencillo de MIDI basado en PianoRollControl para una pista concreta.
    /// Por ahora solo edita una lista de notas en memoria y usa el sintetizador
    /// de AudioEngine para previsualizar al hacer clic.
    /// </summary>
    public class MidiEditorForm : Form
    {
        private readonly AudioEngine _engine;
        private readonly int _trackIndex;
        private readonly PianoRollControl _pianoRoll;
        private readonly Button _btnApply;

        public MidiEditorForm(AudioEngine engine, int trackIndex)
        {
            _engine = engine;
            _trackIndex = trackIndex;

            Text = $"Editor MIDI - Pista {_engine.GetTrackName(trackIndex)}";
            BackColor = Color.FromArgb(30, 30, 30);
            Size = new Size(900, 400);
            StartPosition = FormStartPosition.CenterParent;

            _pianoRoll = new PianoRollControl
            {
                Dock = DockStyle.Fill
            };

            // Barra superior con botón para enviar/guardar las notas en la pista
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(40, 40, 40)
            };

            _btnApply = new Button
            {
                Text = "Enviar a pista MIDI",
                Dock = DockStyle.Right,
                Width = 150,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            _btnApply.FlatAppearance.BorderSize = 0;
            _btnApply.Click += (s, e) =>
            {
                ApplyNotesToTrack();
                // Pequeño feedback visual
                _btnApply.Text = "Enviado";
                var t = new System.Windows.Forms.Timer { Interval = 800 };
                t.Tick += (s2, e2) => { _btnApply.Text = "Enviar a pista MIDI"; t.Stop(); t.Dispose(); };
                t.Start();
            };

            topPanel.Controls.Add(_btnApply);

            // Conectar eventos de previsualización con el AudioEngine
            _pianoRoll.NoteOn += (note, vel) => _engine.PreviewNoteOn(_trackIndex, note, vel);
            _pianoRoll.NoteOff += (note) => _engine.PreviewNoteOff(_trackIndex, note);

            // Orden de docking: primero el piano roll (Fill) y luego la barra superior (Top)
            Controls.Add(_pianoRoll);
            Controls.Add(topPanel);

            // Tomar un snapshot para permitir deshacer cambios en la edición MIDI
            _engine.RegisterUndoSnapshot();

            // Cargar notas existentes de la pista MIDI (si las hay)
            LoadNotesFromTrack();
        }

        private void LoadNotesFromTrack()
        {
            var track = _engine.GetTrack(_trackIndex);
            if (track == null) return;

            // Copiar las notas MIDI almacenadas en la pista al piano roll
            var list = new List<MidiNote>();
            foreach (var n in track.MidiNotes)
            {
                list.Add(new MidiNote
                {
                    NoteNumber = n.NoteNumber,
                    StartBeat = n.StartBeat,
                    DurationBeats = n.DurationBeats,
                    Velocity = n.Velocity
                });
            }
            _pianoRoll.Notes = list;
            _pianoRoll.Invalidate();
        }

        private void ApplyNotesToTrack()
        {
            var track = _engine.GetTrack(_trackIndex);
            if (track == null) return;

            track.MidiNotes.Clear();
            foreach (var n in _pianoRoll.Notes)
            {
                track.MidiNotes.Add(new MidiNote
                {
                    NoteNumber = n.NoteNumber,
                    StartBeat = n.StartBeat,
                    DurationBeats = n.DurationBeats,
                    Velocity = n.Velocity
                });
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            // Al cerrar el editor, asegurarse de volcar lo editado en la pista MIDI
            ApplyNotesToTrack();
        }
    }
}
