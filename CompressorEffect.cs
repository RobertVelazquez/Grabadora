using System;
using NAudio.Wave;

namespace Grabadora
{
    public class CompressorEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WdlDynamicRangeCompressor _compressor;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public CompressorEffect(ISampleProvider source)
        {
            _source = source;
            _compressor = new WdlDynamicRangeCompressor();
            _compressor.SampleRate = source.WaveFormat.SampleRate;
        }

        public void UpdateParameters(float threshold, float ratio, float attack, float release)
        {
            _compressor.Threshold = threshold;
            _compressor.Ratio = ratio;
            _compressor.Attack = attack;
            _compressor.Release = release;
        }

        public float GetThreshold() => _compressor.Threshold;
        public float GetRatio() => _compressor.Ratio;
        public float GetAttack() => _compressor.Attack;
        public float GetRelease() => _compressor.Release;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            // WdlDynamicRangeCompressor tiene un método Process que opera sobre L y R.
            // Es perfecto para el audio estéreo que estamos manejando.
            if (WaveFormat.Channels == 2)
            {
                // Aseguramos que procesamos pares completos para evitar IndexOutOfRangeException
                int samplesToProcess = samplesRead & ~1; // Elimina el último bit para hacerlo par
                for (int i = 0; i < samplesToProcess; i += 2)
                {
                    float left = buffer[offset + i];
                    float right = buffer[offset + i + 1];
                    _compressor.Process(ref left, ref right);
                    buffer[offset + i] = left;
                    buffer[offset + i + 1] = right;
                }
            }
            else if (WaveFormat.Channels == 1)
            {
                // Para audio mono, procesamos cada muestra individualmente.
                // El método Process puede manejar esto pasando la misma muestra a ambos parámetros.
                for (int i = 0; i < samplesRead; i++)
                {
                    float sample = buffer[offset + i];
                    _compressor.Process(ref sample, ref sample);
                    buffer[offset + i] = sample;
                }
            }
            // Nota: El audio con más de 2 canales no será procesado por este efecto.

            return samplesRead;
        }
    }
}