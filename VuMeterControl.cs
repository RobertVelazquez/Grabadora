using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    public class VuMeterControl : Control
    {
        private float _leftLevel;
        private float _rightLevel;
        
        // Variables para el indicador de Clipping
        private int _clipLeftTimer;
        private int _clipRightTimer;
        private const int ClipHoldFrames = 20; // Mantener encendido por ~20 actualizaciones
        
        public VuMeterControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(40, 40);
            this.BackColor = Color.FromArgb(30, 30, 30); // Fondo oscuro
        }

        /// <summary>
        /// Establece los niveles actuales (0.0 a 1.0)
        /// </summary>
        public void SetLevels(float left, float right)
        {
            // Detectar Clipping (> 1.0) antes de clampear
            if (left >= 1.0f) _clipLeftTimer = ClipHoldFrames;
            else if (_clipLeftTimer > 0) _clipLeftTimer--;

            if (right >= 1.0f) _clipRightTimer = ClipHoldFrames;
            else if (_clipRightTimer > 0) _clipRightTimer--;

            _leftLevel = Math.Min(1.0f, Math.Max(0f, left));
            _rightLevel = Math.Min(1.0f, Math.Max(0f, right));
            this.Invalidate(); // Redibujar
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            
            // Configuración de dimensiones
            int padding = 2;
            int barWidth = (this.Width - (padding * 3)) / 2; // 2 barras con separación
            
            // Espacio para el indicador de Clip
            int clipHeight = 4;
            int clipSpacing = 2;
            int meterTop = padding + clipHeight + clipSpacing;
            int meterHeight = this.Height - meterTop - padding;

            int totalLeds = 12; // Cantidad de segmentos LED
            int ledHeight = Math.Max(1, meterHeight / totalLeds);
            
            // Dibujar Indicadores de Clip
            DrawClip(g, padding, padding, barWidth, clipHeight, _clipLeftTimer > 0);
            DrawClip(g, padding + barWidth + padding, padding, barWidth, clipHeight, _clipRightTimer > 0);

            // Dibujar barra Izquierda (L)
            DrawBar(g, padding, meterTop, barWidth, ledHeight, totalLeds, _leftLevel);
            
            // Dibujar barra Derecha (R)
            DrawBar(g, padding + barWidth + padding, meterTop, barWidth, ledHeight, totalLeds, _rightLevel);
        }

        private void DrawClip(Graphics g, int x, int y, int width, int height, bool active)
        {
            // Rojo brillante si hay clipping, rojo muy oscuro si no
            Color c = active ? Color.Red : Color.FromArgb(50, 20, 20);
            using (var b = new SolidBrush(c))
            {
                g.FillRectangle(b, x, y, width, height);
            }
        }

        private void DrawBar(Graphics g, int x, int yStart, int width, int ledHeight, int totalLeds, float level)
        {
            int activeLeds = (int)(level * totalLeds);
            
            for (int i = 0; i < totalLeds; i++)
            {
                // Invertimos el índice para dibujar de abajo hacia arriba
                int ledIndex = totalLeds - 1 - i;
                int y = yStart + (i * ledHeight);
                int h = Math.Max(1, ledHeight - 1); // -1 para dejar un pequeño espacio negro entre LEDs

                // Determinar color según la altura (Verde -> Amarillo -> Rojo)
                Color color;
                if (ledIndex >= totalLeds - 2) color = Color.Red;        // Últimos 2 (Pico)
                else if (ledIndex >= totalLeds - 5) color = Color.Yellow; // Medios
                else color = Color.LimeGreen;                             // Bajos

                // Si el LED no está activo, lo oscurecemos mucho (efecto apagado)
                if (ledIndex >= activeLeds)
                {
                    color = Color.FromArgb(40, color.R, color.G, color.B);
                }

                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, x, y, width, h);
                }
            }
        }
    }
}