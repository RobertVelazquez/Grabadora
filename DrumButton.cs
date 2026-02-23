using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Grabadora
{
    public class DrumButton : Control
    {
        private bool _isPressed = false;

        public DrumButton()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(50, 50);
            this.Cursor = Cursors.Hand;
            this.Text = "KICK";
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
            
            // Ajustar rectángulo si está presionado (efecto visual)
            float offset = _isPressed ? 2 : 0;
            RectangleF rect = new RectangleF(2 + offset, 2 + offset, w - 4 - (offset * 2), h - 4 - (offset * 2));

            // 1. Aro exterior (Metálico)
            using (var brush = new LinearGradientBrush(rect, Color.FromArgb(180, 180, 180), Color.FromArgb(80, 80, 80), 60f))
            {
                e.Graphics.FillEllipse(brush, rect);
            }
            
            // 2. Parche interior (Blanco/Beige)
            float rimSize = w * 0.15f; // Grosor del aro
            RectangleF skinRect = new RectangleF(rect.X + rimSize, rect.Y + rimSize, rect.Width - (2 * rimSize), rect.Height - (2 * rimSize));
            
            using (var brush = new LinearGradientBrush(skinRect, Color.WhiteSmoke, Color.FromArgb(240, 230, 200), -45f))
            {
                e.Graphics.FillEllipse(brush, skinRect);
            }

            // 3. Tornillos decorativos (Lugs) en el aro
            int lugCount = 8;
            float radius = rect.Width / 2 - (rimSize / 2);
            float cx = rect.X + rect.Width / 2;
            float cy = rect.Y + rect.Height / 2;
            
            using (var brush = new SolidBrush(Color.FromArgb(50, 50, 50)))
            {
                for (int i = 0; i < lugCount; i++)
                {
                    double angle = i * (2 * Math.PI / lugCount);
                    float lx = cx + (float)(Math.Cos(angle) * radius);
                    float ly = cy + (float)(Math.Sin(angle) * radius);
                    e.Graphics.FillEllipse(brush, lx - 2, ly - 2, 4, 4);
                }
            }

            // 4. Texto central
            using (var brush = new SolidBrush(Color.FromArgb(60, 60, 60)))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(this.Text, this.Font, brush, cx, cy, sf);
            }
        }
    }
}