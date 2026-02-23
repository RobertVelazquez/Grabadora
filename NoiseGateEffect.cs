using System;
using NAudio.Wave;

namespace Grabadora
{
    public class NoiseGateEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _envelope;
        private float _currentGain = 1.0f;
        private int _holdCounter;
        
        public float Threshold { get; set; } // 0.0 to 1.0
        public float Ratio { get; set; } = 10.0f; // Relación de reducción
        public float Attack { get; set; } = 0.01f;
        public float Release { get; set; } = 0.1f;
        // Nuevo: Hold time en segundos para evitar "chattering"
        public float Hold { get; set; } = 0.0f; 

        public WaveFormat WaveFormat => _source.WaveFormat;

        public NoiseGateEffect(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int sampleRate = WaveFormat.SampleRate;
            int channels = WaveFormat.Channels;
            
            // Coeficientes para el seguidor de envolvente (Envelope Follower)
            float attackCoef = (float)Math.Exp(-1.0 / (Attack * sampleRate));
            float releaseCoef = (float)Math.Exp(-1.0 / (Release * sampleRate));
            int holdSamples = (int)(Hold * sampleRate);

            for (int n = 0; n < read / channels; n++)
            {
                // Detectar pico del frame (para estéreo)
                float maxAbs = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    float abs = Math.Abs(buffer[offset + n * channels + ch]);
                    if (abs > maxAbs) maxAbs = abs;
                }

                // Envelope Follower (Detectar nivel actual suavizado)
                if (maxAbs > _envelope)
                    _envelope = attackCoef * _envelope + (1 - attackCoef) * maxAbs;
                else
                    _envelope = releaseCoef * _envelope + (1 - releaseCoef) * maxAbs;

                // Lógica del Gate
                if (_envelope < Threshold)
                {
                    if (_holdCounter > 0) _holdCounter--;
                    else
                    {
                        // Release: La ganancia se reduce suavemente hacia 0
                        _currentGain = releaseCoef * _currentGain + (1 - releaseCoef) * 0.0f;
                    }
                }
                else
                {
                    // Attack: La ganancia sube suavemente hacia 1.0
                    _currentGain = attackCoef * _currentGain + (1 - attackCoef) * 1.0f;
                    _holdCounter = holdSamples;
                }
                
                // Aplicar ganancia al frame
                for (int ch = 0; ch < channels; ch++)
                {
                    buffer[offset + n * channels + ch] *= _currentGain;
                }
            }
            return read;
        }
    }
}