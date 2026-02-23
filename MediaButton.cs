using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Grabadora
{
    public enum MediaButtonType { Play, Stop, Record, Loop, Microphone }

    public class MediaButton : Control
    {
        private bool _isPressed = false;
        private bool _isActive = false;

        public bool IsActive 
        { 
            get => _isActive; 
            set { _isActive = value; Invalidate(); } 
        }

        public MediaButtonType ButtonType { get; set; } = MediaButtonType.Play;

        public MediaButton()
        {
            this.SetStyle(ControlStyles.SupportsTransparentBackColor, true); // Habilitar transparencia
            this.DoubleBuffered = true;
            this.Size = new Size(50, 50);
            this.Cursor = Cursors.Hand;
            this.BackColor = Color.Transparent;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isPressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isPressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            
            float w = this.Width;
            float h = this.Height;
            RectangleF rect = new RectangleF(2, 2, w - 4, h - 4);

            // 1. Fondo con gradiente (Estilo metálico oscuro)
            // Si está activo (Loop), usar tonos verdes, si no, grises
            Color baseC1 = _isActive ? Color.FromArgb(0, 180, 0) : Color.FromArgb(70, 70, 70);
            Color baseC2 = _isActive ? Color.FromArgb(0, 100, 0) : Color.FromArgb(40, 40, 40);

            Color c1 = _isPressed ? Color.FromArgb(30, 30, 30) : baseC1;
            Color c2 = _isPressed ? Color.FromArgb(10, 10, 10) : baseC2;
            
            using (var brush = new LinearGradientBrush(rect, c1, c2, 45f))
            {
                e.Graphics.FillEllipse(brush, rect);
            }
            
            // 2. Borde
            using (var pen = new Pen(Color.FromArgb(20, 20, 20), 2))
            {
                e.Graphics.DrawEllipse(pen, rect);
            }

            // 3. Icono
            using (var brush = new SolidBrush(Color.WhiteSmoke))
            {
                float cx = w / 2;
                float cy = h / 2;
                float size = w * 0.35f; // Tamaño del icono relativo al botón

                if (ButtonType == MediaButtonType.Play)
                {
                    // Triángulo de Play (centrado visualmente)
                    float r = size / 1.5f; 
                    PointF[] pts = {
                        new PointF(cx - r/2 + 2, cy - r),
                        new PointF(cx - r/2 + 2, cy + r),
                        new PointF(cx + r + 2, cy)
                    };
                    e.Graphics.FillPolygon(brush, pts);
                }
                else if (ButtonType == MediaButtonType.Stop)
                {
                    // Cuadrado de Stop
                    float r = size / 1.5f;
                    e.Graphics.FillRectangle(brush, cx - r, cy - r, r * 2, r * 2);
                }
                else if (ButtonType == MediaButtonType.Record)
                {
                    // Círculo de Record
                    float r = size / 1.5f;
                    using (var recBrush = new SolidBrush(Color.Red))
                    {
                        e.Graphics.FillEllipse(recBrush, cx - r, cy - r, r * 2, r * 2);
                    }
                }
                else if (ButtonType == MediaButtonType.Loop)
                {
                    // Icono de Loop (Flechas circulares simplificadas)
                    using (var pen = new Pen(Color.WhiteSmoke, 2))
                    {
                        float r = size / 1.8f;
                        e.Graphics.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 45, 270);
                        // Punta de flecha
                        float arrowSize = 4;
                        e.Graphics.FillPolygon(brush, new PointF[] { new PointF(cx + r, cy - arrowSize), new PointF(cx + r + arrowSize, cy + arrowSize), new PointF(cx + r - arrowSize, cy + arrowSize) });
                    }
                }
                else if (ButtonType == MediaButtonType.Microphone)
                {
                    // Icono de Micrófono
                    float scale = w * 0.4f;
                    using (var pen = new Pen(Color.WhiteSmoke, 2))
                    {
                        // Cabeza
                        RectangleF head = new RectangleF(cx - scale / 3, cy - scale / 1.5f, scale / 1.5f, scale);
                        e.Graphics.FillEllipse(brush, head);
                        
                        // Cuerpo/Soporte
                        e.Graphics.DrawArc(pen, cx - scale / 2, cy - scale / 2, scale, scale, 0, 180);
                        e.Graphics.DrawLine(pen, cx, cy + scale / 2, cx, cy + scale);
                        e.Graphics.DrawLine(pen, cx - scale / 2, cy + scale, cx + scale / 2, cy + scale);
                    }
                }
            }
        }
    }
}