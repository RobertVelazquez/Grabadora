using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    public class VerticalFaderControl : Control
    {
        private float _value = 1.0f;
        private bool _isDragging;
        private int _handleHeight = 10;

        public event EventHandler ValueChanged;

        public float Value
        {
            get => _value;
            set
            {
                _value = Math.Max(0f, Math.Min(1f, value));
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public VerticalFaderControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(15, 80);
            this.Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                UpdateValue(e.Y);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                UpdateValue(e.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
        }

        private void UpdateValue(int mouseY)
        {
            int trackHeight = this.Height - _handleHeight;
            int y = Math.Max(0, Math.Min(trackHeight, mouseY - (_handleHeight / 2)));
            // Invertir: Y=0 es Max(1.0), Y=Height es Min(0.0)
            Value = 1.0f - ((float)y / trackHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            // Track (Ranura)
            int slotWidth = 2;
            int slotX = (this.Width - slotWidth) / 2;
            using (var brush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            {
                e.Graphics.FillRectangle(brush, slotX, _handleHeight/2, slotWidth, this.Height - _handleHeight);
            }

            // Handle (Botón)
            int trackHeight = this.Height - _handleHeight;
            int handleY = (int)((1.0f - _value) * trackHeight);
            Rectangle handleRect = new Rectangle(1, handleY, this.Width - 2, _handleHeight);

            using (var brush = new SolidBrush(Color.Silver))
            {
                e.Graphics.FillRectangle(brush, handleRect);
            }
        }
    }
}