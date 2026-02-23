using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Grabadora
{
    public class KnobControl : Control
    {
        private float _value = 0f;
        private float _minimum = 0f;
        private float _maximum = 1f;
        private Point _lastMousePos;
        private bool _isDragging;
        public bool IsBalance { get; set; } = false;

        public event EventHandler ValueChanged;

        public float Minimum
        {
            get => _minimum;
            set { _minimum = value; Invalidate(); }
        }

        public float Maximum
        {
            get => _maximum;
            set { _maximum = value; Invalidate(); }
        }

        public float Value
        {
            get => _value;
            set
            {
                float newVal = Math.Max(_minimum, Math.Min(_maximum, value));
                if (Math.Abs(_value - newVal) > 0.001f)
                {
                    _value = newVal;
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    Invalidate();
                }
            }
        }

        public KnobControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(40, 40);
            this.Cursor = Cursors.Hand;
            this.ForeColor = Color.Cyan; // Color del arco de progreso
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _lastMousePos = e.Location;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                // Arrastrar hacia arriba aumenta, hacia abajo disminuye
                int delta = _lastMousePos.Y - e.Y; 
                float range = _maximum - _minimum;
                float sensitivity = range / 150.0f; // Ajustar sensibilidad
                
                Value += delta * sensitivity;
                _lastMousePos = e.Location;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float w = this.Width;
            float h = this.Height;
            float padding = 4;
            float diameter = Math.Min(w, h) - (padding * 2);
            float radius = diameter / 2;
            float cx = w / 2;
            float cy = h / 2;

            RectangleF rect = new RectangleF(cx - radius, cy - radius, diameter, diameter);

            // 1. Fondo del knob
            using (var brush = new LinearGradientBrush(rect, Color.FromArgb(60, 60, 60), Color.FromArgb(30, 30, 30), 45f))
            {
                e.Graphics.FillEllipse(brush, rect);
            }
            using (var pen = new Pen(Color.FromArgb(20, 20, 20), 1.5f))
            {
                e.Graphics.DrawEllipse(pen, rect);
            }

            // 2. Calcular ángulo (de 135° a 405°)
            float range = _maximum - _minimum;
            float pct = (_value - _minimum) / (range > 0 ? range : 1);
            float startAngle = 135.0f;
            float sweepAngle = 270.0f;
            float currentAngle = startAngle + (pct * sweepAngle);

            // 3. Arco de progreso (Estilo moderno)
            using (var penBg = new Pen(Color.FromArgb(50, 50, 50), 3))
            {
                e.Graphics.DrawArc(penBg, rect, startAngle, sweepAngle);
            }
            using (var penFg = new Pen(this.ForeColor, 3))
            {
                if (IsBalance)
                {
                    // Modo Balance: Dibuja desde el centro (270°) hacia los lados
                    float centerAngle = startAngle + (sweepAngle / 2); // 270 grados
                    if (pct < 0.5f)
                    {
                        // Izquierda: Desde la posición actual hasta el centro
                        e.Graphics.DrawArc(penFg, rect, currentAngle, centerAngle - currentAngle);
                    }
                    else
                    {
                        // Derecha: Desde el centro hasta la posición actual
                        e.Graphics.DrawArc(penFg, rect, centerAngle, currentAngle - centerAngle);
                    }
                }
                else
                {
                    // Modo Normal (Volumen/Efecto): Dibuja desde el inicio (izquierda)
                    if (pct > 0) e.Graphics.DrawArc(penFg, rect, startAngle, pct * sweepAngle);
                }
            }

            // 4. Indicador (Línea blanca en la perilla)
            double angleRad = currentAngle * Math.PI / 180.0;
            float rStart = radius * 0.4f;
            float rEnd = radius * 0.9f;
            float x1 = cx + (float)(Math.Cos(angleRad) * rStart);
            float y1 = cy + (float)(Math.Sin(angleRad) * rStart);
            float x2 = cx + (float)(Math.Cos(angleRad) * rEnd);
            float y2 = cy + (float)(Math.Sin(angleRad) * rEnd);

            using (var penLine = new Pen(Color.White, 2))
            {
                e.Graphics.DrawLine(penLine, x1, y1, x2, y2);
            }
        }
    }
}