using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    public class TimelineControl : Control
    {
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public int LeftMargin { get; set; } = 170; // 140 (Panel) + 30 (Spacer)
        public int ContentWidth { get; set; } // Ancho real del contenido (para sincronizar con pistas)
        public double CurrentTimeSeconds { get; set; } // Tiempo actual de reproducción (segundos)

        // Evento para notificar que el usuario ha movido el cursor de tiempo
        public event Action<double>? PositionChanged; // parámetro: tiempo en segundos

        private bool _isDragging;

        public TimelineControl()
        {
            this.DoubleBuffered = true;
            this.Height = 30;
            this.Dock = DockStyle.Top;
            this.BackColor = Color.FromArgb(40, 40, 40);
            this.ForeColor = Color.LightGray;
            this.Font = new Font("Segoe UI", 8);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left)
            {
                UpdatePositionFromMouse(e.X);
                _isDragging = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDragging && e.Button == MouseButtons.Left)
            {
                UpdatePositionFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
        }

        private void UpdatePositionFromMouse(int mouseX)
        {
            double duration = EndTime - StartTime;
            if (duration <= 0) return;

            int totalDrawWidth = ContentWidth > 0 ? ContentWidth : Width;
            int waveformWidth = totalDrawWidth - LeftMargin;
            if (waveformWidth <= 0) return;

            // Convertir posición X (después del margen izquierdo) a tiempo
            double xRelative = mouseX - LeftMargin;
            double ratio = xRelative / waveformWidth;

            // Limitar entre 0 y 1 para no salir del rango visible
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            double newTime = StartTime + ratio * duration;
            CurrentTimeSeconds = newTime;
            Invalidate();

            PositionChanged?.Invoke(newTime);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            
            // Dibujar fondo del margen izquierdo (coincide con el panel de control de la pista)
            g.FillRectangle(new SolidBrush(Color.FromArgb(50, 50, 50)), 0, 0, LeftMargin, Height);
            
            // Línea separadora inferior
            g.DrawLine(Pens.Gray, 0, Height - 1, Width, Height - 1);

            double duration = EndTime - StartTime;
            if (duration <= 0) return;

            // Usamos ContentWidth si está definido (para compensar scrollbar vertical), sino el ancho total
            int totalDrawWidth = ContentWidth > 0 ? ContentWidth : Width;
            int waveformWidth = totalDrawWidth - LeftMargin;
            
            if (waveformWidth <= 0) return;

            // Calcular intervalo óptimo para los ticks
            double pixelsPerSecond = (double)waveformWidth / duration;
            double minPixelsPerTick = 60; // Espacio mínimo entre etiquetas
            double interval = 1.0;
            
            // Posibles intervalos: 0.1s, 0.5s, 1s, 2s, 5s, 10s, etc.
            double[] intervals = { 0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
            foreach (var val in intervals)
            {
                if (val * pixelsPerSecond >= minPixelsPerTick)
                {
                    interval = val;
                    break;
                }
            }

            // Dibujar ticks y etiquetas
            double firstTick = Math.Ceiling(StartTime / interval) * interval;
            
            using (Pen p = new Pen(Color.Gray))
            using (Brush b = new SolidBrush(ForeColor))
            {
                for (double t = firstTick; t <= EndTime; t += interval)
                {
                    float x = LeftMargin + (float)((t - StartTime) / duration * waveformWidth);
                    
                    // Tick grande
                    g.DrawLine(p, x, Height - 10, x, Height);
                    
                    // Texto de tiempo
                    TimeSpan ts = TimeSpan.FromSeconds(t);
                    string text = ts.TotalMinutes >= 1 ? ts.ToString(@"mm\:ss") : ts.ToString(@"ss\.f");
                    
                    SizeF size = g.MeasureString(text, Font);
                    g.DrawString(text, Font, b, x - (size.Width / 2), Height - 22);
                }
            }

            // Dibujar cursor de reproducción (línea roja en la regla superior)
            if (CurrentTimeSeconds >= StartTime && CurrentTimeSeconds <= EndTime && waveformWidth > 0)
            {
                float cursorX = LeftMargin + (float)((CurrentTimeSeconds - StartTime) / duration * waveformWidth);
                using (Pen cursorPen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(cursorPen, cursorX, 0, cursorX, Height);
                }
            }
        }
    }
}