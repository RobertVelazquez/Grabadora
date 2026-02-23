using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Grabadora
{
    // Clase simple para representar una nota MIDI
    public class MidiNote
    {
        public int NoteNumber { get; set; } // 0-127 (60 = Do Central / C4)
        public double StartBeat { get; set; }
        public double DurationBeats { get; set; }
        public int Velocity { get; set; } = 100;
        public bool Selected { get; set; }
    }

    public class PianoRollControl : Control
    {
        public List<MidiNote> Notes { get; set; } = new List<MidiNote>();
        
        // Eventos para conectar con el AudioEngine
        public event Action<int, int>? NoteOn;  // (NoteNumber, Velocity)
        public event Action<int>? NoteOff;      // (NoteNumber)

        private int _keyHeight = 20;
        private int _pianoWidth = 60;
        private float _pixelsPerBeat = 40f;
        
        // Scroll
        private VScrollBar _vScrollBar;
        private HScrollBar _hScrollBar;
        private int _viewOffsetY = 0;
        private int _viewOffsetX = 0;

        // Interacción
        private bool _isDraggingNote;
        private bool _isResizingNote;
        private MidiNote? _interactingNote;
        private Point _lastMousePos;
        private int _resizeHandleSize = 8;

        public float PixelsPerBeat 
        { 
            get => _pixelsPerBeat; 
            set { _pixelsPerBeat = Math.Max(10, value); Invalidate(); UpdateScrollBars(); } 
        }

        public PianoRollControl()
        {
            this.DoubleBuffered = true;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.Cursor = Cursors.Default;

            _vScrollBar = new VScrollBar { Dock = DockStyle.Right, SmallChange = _keyHeight, LargeChange = _keyHeight * 5 };
            _vScrollBar.Scroll += (s, e) => { _viewOffsetY = _vScrollBar.Value; Invalidate(); };

            _hScrollBar = new HScrollBar { Dock = DockStyle.Bottom, SmallChange = 10, LargeChange = 100 };
            _hScrollBar.Scroll += (s, e) => { _viewOffsetX = _hScrollBar.Value; Invalidate(); };

            this.Controls.Add(_vScrollBar);
            this.Controls.Add(_hScrollBar);
            
            // Configurar scrollbars con el tamaño inicial del control
            UpdateScrollBars();

            // Scroll inicial aproximado al Do central (C4 = 60),
            // asegurándonos de no salirnos del rango válido del scrollbar
            int centerNoteY = (127 - 60) * _keyHeight;
            int visibleHeight = Math.Max(0, this.Height - _hScrollBar.Height);
            int initialScroll = centerNoteY - (visibleHeight / 2);

            int min = _vScrollBar.Minimum;
            int max = _vScrollBar.Maximum - _vScrollBar.LargeChange + 1;
            if (max < min) max = min;

            initialScroll = Math.Max(min, Math.Min(max, initialScroll));
            _vScrollBar.Value = initialScroll;
            _viewOffsetY = _vScrollBar.Value;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollBars();
        }

        private void UpdateScrollBars()
        {
            int totalHeight = 128 * _keyHeight;
            int visibleHeight = this.Height - _hScrollBar.Height;
            
            _vScrollBar.Maximum = Math.Max(0, totalHeight - visibleHeight + _vScrollBar.LargeChange - 1);
            _vScrollBar.Enabled = totalHeight > visibleHeight;

            // Ancho estimado (ej. 100 compases * 4 beats)
            int totalWidth = (int)(400 * _pixelsPerBeat); 
            int visibleWidth = this.Width - _pianoWidth - _vScrollBar.Width;

            _hScrollBar.Maximum = Math.Max(0, totalWidth - visibleWidth + _hScrollBar.LargeChange - 1);
            _hScrollBar.Enabled = totalWidth > visibleWidth;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            int w = this.Width - _vScrollBar.Width;
            int h = this.Height - _hScrollBar.Height;
            
            // Definir área de recorte para la rejilla (excluyendo el piano)
            Rectangle gridRect = new Rectangle(_pianoWidth, 0, w - _pianoWidth, h);
            g.SetClip(gridRect);
            
            // Aplicar transformación para el scroll
            g.TranslateTransform(-_viewOffsetX + _pianoWidth, -_viewOffsetY);

            // 1. Dibujar Grid y Fondo de filas
            // Calculamos qué notas son visibles para no dibujar las 128
            int firstNoteIndex = Math.Max(0, 127 - ((_viewOffsetY + h) / _keyHeight));
            int lastNoteIndex = Math.Min(127, 127 - (_viewOffsetY / _keyHeight));

            for (int i = firstNoteIndex; i <= lastNoteIndex; i++)
            {
                int y = (127 - i) * _keyHeight;
                bool isBlack = IsBlackKey(i);
                
                // Fondo de fila (más oscuro para teclas negras)
                Color rowColor = isBlack ? Color.FromArgb(25, 25, 25) : Color.FromArgb(35, 35, 35);
                using (var brush = new SolidBrush(rowColor))
                    g.FillRectangle(brush, _viewOffsetX, y, w + _viewOffsetX, _keyHeight);

                // Línea horizontal divisoria
                using (var pen = new Pen(Color.FromArgb(50, 50, 50)))
                    g.DrawLine(pen, _viewOffsetX, y, w + _viewOffsetX + _pianoWidth, y);
            }

            // Líneas verticales (Beats)
            int startBeat = (int)(_viewOffsetX / _pixelsPerBeat);
            int endBeat = (int)((_viewOffsetX + w) / _pixelsPerBeat) + 1;

            using (var penBeat = new Pen(Color.FromArgb(60, 60, 60)))
            using (var penBar = new Pen(Color.FromArgb(90, 90, 90)))
            {
                for (int b = startBeat; b <= endBeat; b++)
                {
                    float x = b * _pixelsPerBeat;
                    // Resaltar cada 4 beats (inicio de compás)
                    g.DrawLine(b % 4 == 0 ? penBar : penBeat, x, _viewOffsetY, x, _viewOffsetY + h);
                }
            }

            // 2. Dibujar Notas
            foreach (var note in Notes)
            {
                int y = (127 - note.NoteNumber) * _keyHeight;
                
                // Optimización: No dibujar si está fuera de la vista vertical
                if (y + _keyHeight < _viewOffsetY || y > _viewOffsetY + h) continue;

                float x = (float)(note.StartBeat * _pixelsPerBeat);
                float width = (float)(note.DurationBeats * _pixelsPerBeat);

                // Optimización: No dibujar si está fuera de la vista horizontal
                if (x + width < _viewOffsetX || x > _viewOffsetX + w) continue;

                RectangleF noteRect = new RectangleF(x, y + 1, width, _keyHeight - 2);
                
                Color c = note.Selected ? Color.LightBlue : Color.LightGreen;
                using (var brush = new SolidBrush(Color.FromArgb(200, c)))
                using (var pen = new Pen(c))
                {
                    g.FillRectangle(brush, noteRect);
                    g.DrawRectangle(pen, noteRect.X, noteRect.Y, noteRect.Width, noteRect.Height);
                }
            }

            // Resetear transformación para dibujar el piano estático a la izquierda
            g.ResetTransform();
            g.SetClip(new Rectangle(0, 0, _pianoWidth, h));
            g.TranslateTransform(0, -_viewOffsetY);

            // 3. Dibujar Teclas de Piano (Izquierda)
            for (int i = firstNoteIndex; i <= lastNoteIndex; i++)
            {
                int y = (127 - i) * _keyHeight;
                bool isBlack = IsBlackKey(i);
                
                Rectangle keyRect = new Rectangle(0, y, _pianoWidth, _keyHeight);
                
                if (isBlack)
                {
                    using (var b = new LinearGradientBrush(keyRect, Color.FromArgb(60, 60, 60), Color.Black, 0f))
                        g.FillRectangle(b, keyRect);
                }
                else
                {
                    using (var b = new LinearGradientBrush(keyRect, Color.White, Color.LightGray, 0f))
                        g.FillRectangle(b, keyRect);
                }
                
                g.DrawRectangle(Pens.Gray, keyRect);

                // Etiqueta de nota (solo en Do/C)
                if (i % 12 == 0)
                {
                    string noteName = $"C{i / 12 - 1}";
                    using (var font = new Font("Segoe UI", 7))
                    using (var brush = new SolidBrush(isBlack ? Color.White : Color.Black))
                    {
                        g.DrawString(noteName, font, brush, 2, y + 4);
                    }
                }
            }
        }

        private bool IsBlackKey(int noteNumber)
        {
            int n = noteNumber % 12;
            // 1=C#, 3=D#, 6=F#, 8=G#, 10=A#
            return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
        }

        // --- Manejo del Mouse ---

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _lastMousePos = e.Location;
            
            // Si el clic es en el área de la rejilla (no en el piano)
            if (e.X > _pianoWidth)
            {
                // Convertir coordenadas de pantalla a coordenadas lógicas (Grid)
                float gridX = e.X - _pianoWidth + _viewOffsetX;
                float gridY = e.Y + _viewOffsetY;
                
                double beat = gridX / _pixelsPerBeat;
                int noteNum = 127 - (int)(gridY / _keyHeight);

                // 1. Buscar si hicimos clic sobre una nota existente
                _interactingNote = null;
                foreach (var n in Notes)
                {
                    int ny = (127 - n.NoteNumber) * _keyHeight;
                    float nx = (float)(n.StartBeat * _pixelsPerBeat);
                    float nw = (float)(n.DurationBeats * _pixelsPerBeat);
                    
                    // Hit test simple
                    if (gridY >= ny && gridY < ny + _keyHeight && gridX >= nx && gridX < nx + nw)
                    {
                        _interactingNote = n;
                        // Verificar si estamos en el borde derecho para redimensionar
                        if (gridX > nx + nw - _resizeHandleSize) _isResizingNote = true;
                        else _isDraggingNote = true;
                        break;
                    }
                }

                // 2. Si no hay nota, crear una nueva
                if (_interactingNote == null && e.Button == MouseButtons.Left)
                {
                    var newNote = new MidiNote 
                    { 
                        NoteNumber = Math.Max(0, Math.Min(127, noteNum)), 
                        StartBeat = Math.Floor(beat), // Snap al beat más cercano
                        DurationBeats = 1.0 
                    };
                    Notes.Add(newNote);
                    _interactingNote = newNote;
                    _isResizingNote = true; // Permitir ajustar duración inmediatamente al arrastrar
                    Invalidate();
                    
                    // Disparar sonido de previsualización
                    NoteOn?.Invoke(newNote.NoteNumber, newNote.Velocity);
                }
                else if (_interactingNote != null)
                {
                    // Seleccionar nota
                    foreach(var n in Notes) n.Selected = false;
                    _interactingNote.Selected = true;
                    Invalidate();
                    
                    // Disparar sonido al seleccionar
                    NoteOn?.Invoke(_interactingNote.NoteNumber, _interactingNote.Velocity);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            
            if (_interactingNote != null && e.Button == MouseButtons.Left)
            {
                float dx = e.X - _lastMousePos.X;
                
                if (_isResizingNote)
                {
                    double beatChange = dx / _pixelsPerBeat;
                    _interactingNote.DurationBeats = Math.Max(0.25, _interactingNote.DurationBeats + beatChange);
                }
                else if (_isDraggingNote)
                {
                    double beatChange = dx / _pixelsPerBeat;
                    _interactingNote.StartBeat = Math.Max(0, _interactingNote.StartBeat + beatChange);
                    
                    // Cambio de tono (Pitch) al arrastrar verticalmente
                    float gridY = e.Y + _viewOffsetY;
                    int newNoteNum = 127 - (int)(gridY / _keyHeight);
                    _interactingNote.NoteNumber = Math.Max(0, Math.Min(127, newNoteNum));
                    
                    // Opcional: Repetir nota al cambiar de tono (puede ser ruidoso si se mueve rápido)
                    // NoteOff?.Invoke(...);
                    // NoteOn?.Invoke(...);
                }
                
                _lastMousePos = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            
            // Detener sonido si estábamos interactuando con una nota
            if (_interactingNote != null)
            {
                NoteOff?.Invoke(_interactingNote.NoteNumber);
            }

            _isDraggingNote = false;
            _isResizingNote = false;
            _interactingNote = null;
        }
    }
}