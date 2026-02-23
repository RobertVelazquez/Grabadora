using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grabadora
{
    public class SpectrumAnalyzerControl : Control
    {
        private float[] _fftData;
        private readonly int _barCount = 20; // Cantidad de barras

        public SpectrumAnalyzerControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(100, 40);
            this.BackColor = Color.Black;
        }

        public void SetFftData(float[] fftData)
        {
            _fftData = fftData;
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_fftData == null || _fftData.Length == 0) return;

            Graphics g = e.Graphics;
            float width = this.Width;
            float height = this.Height;
            float barWidth = width / _barCount;

            // Agrupar los bins de FFT en barras
            // Usamos solo la primera mitad de los datos FFT (frecuencias útiles)
            int usefulDataCount = _fftData.Length / 2; 
            int samplesPerBar = Math.Max(1, usefulDataCount / _barCount);

            using (var brush = new SolidBrush(Color.Cyan))
            {
                for (int i = 0; i < _barCount; i++)
                {
                    float max = 0;
                    for (int j = 0; j < samplesPerBar; j++)
                    {
                        int index = i * samplesPerBar + j;
                        if (index < _fftData.Length) max = Math.Max(max, _fftData[index]);
                    }
                    
                    // Escalar para visualización (multiplicador empírico para que se vea bien)
                    float barHeight = Math.Min(height, max * height * 100); 

                    g.FillRectangle(brush, i * barWidth, height - barHeight, barWidth - 1, barHeight);
                }
            }
        }
    }
}