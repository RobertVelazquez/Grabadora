using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace Grabadora
{
    public class Equalizer : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BiQuadFilter[,] _filters;
        private readonly float[] _frequencies;
        private readonly float[] _gains;
        private readonly int _channels;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public Equalizer(ISampleProvider source, float[] frequencies)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _frequencies = frequencies;
            _filters = new BiQuadFilter[frequencies.Length, _channels];
            _gains = new float[frequencies.Length];

            // Inicializar filtros con ganancia 0dB
            for (int i = 0; i < frequencies.Length; i++)
                UpdateBand(i, 0);
        }

        public float[] GetGains()
        {
            return (float[])_gains.Clone();
        }

        public void UpdateBand(int bandIndex, float gainDb)
        {
            if (bandIndex < 0 || bandIndex >= _gains.Length) return;
            _gains[bandIndex] = gainDb;

            // Actualizamos el filtro para cada canal (Izquierdo/Derecho)
            for (int ch = 0; ch < _channels; ch++)
            {
                // Q=0.8 es un ancho de banda estándar para ecualizadores gráficos
                _filters[bandIndex, ch] = BiQuadFilter.PeakingEQ(_source.WaveFormat.SampleRate, _frequencies[bandIndex], 0.8f, gainDb);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            for (int i = 0; i < samplesRead; i++)
            {
                int ch = i % _channels;
                // Aplicar cada banda de frecuencia en serie
                for (int band = 0; band < _filters.GetLength(0); band++)
                {
                    buffer[offset + i] = _filters[band, ch].Transform(buffer[offset + i]);
                }
            }
            return samplesRead;
        }
    }
}