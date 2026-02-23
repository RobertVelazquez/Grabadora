using System;
using NAudio.Wave;

namespace Grabadora
{
    public class ChorusEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _delayBuffer;
        private int _writePos;
        private float _lfoPhase;

        // Parámetros
        public float DelayMs { get; set; } = 20f; // Retardo base (15-25ms para Chorus, 1-10ms para Flanger)
        public float Depth { get; set; } = 0.2f;  // Profundidad de modulación (0.0 a 1.0)
        public float Rate { get; set; } = 1.0f;   // Velocidad del LFO en Hz
        public float Feedback { get; set; } = 0.2f; // Realimentación
        public float Mix { get; set; } = 0.0f;    // Mezcla Dry/Wet

        public WaveFormat WaveFormat => _source.WaveFormat;

        public ChorusEffect(ISampleProvider source)
        {
            _source = source;
            // Buffer de 100ms es suficiente para Chorus y Flanger
            int bufferLen = (int)(0.1 * source.WaveFormat.SampleRate * source.WaveFormat.Channels);
            _delayBuffer = new float[bufferLen];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            if (Mix <= 0.001f) return samplesRead;

            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;
            int bufferLen = _delayBuffer.Length;
            int totalFrames = bufferLen / channels;
            
            // Avance del LFO por frame
            float lfoStep = (float)(2.0 * Math.PI * Rate / sampleRate);

            for (int i = 0; i < samplesRead; i += channels)
            {
                // Actualizar LFO
                _lfoPhase += lfoStep;
                if (_lfoPhase > 2.0 * Math.PI) _lfoPhase -= (float)(2.0 * Math.PI);
                
                // Calcular retardo modulado
                // El LFO oscila entre -1 y 1. Modulamos el retardo base.
                // Un swing de 5ms es típico para Chorus.
                float lfoVal = (float)Math.Sin(_lfoPhase);
                float modMs = lfoVal * (Depth * 5.0f); 
                float totalDelayMs = Math.Max(0.1f, DelayMs + modMs);
                
                float delaySamples = (totalDelayMs / 1000.0f) * sampleRate;

                for (int ch = 0; ch < channels; ch++)
                {
                    float input = buffer[offset + i + ch];
                    
                    // Calcular posición de lectura en frames (índices de muestra / canales)
                    int currentFrameIndex = _writePos / channels;
                    float readFrameIndex = currentFrameIndex - delaySamples;
                    
                    // Ajuste circular
                    while (readFrameIndex < 0) readFrameIndex += totalFrames;
                    while (readFrameIndex >= totalFrames) readFrameIndex -= totalFrames;
                    
                    // Interpolación Lineal
                    int frameA = (int)readFrameIndex;
                    int frameB = (frameA + 1) % totalFrames;
                    float frac = readFrameIndex - frameA;
                    
                    float delayed = _delayBuffer[frameA * channels + ch] * (1.0f - frac) + 
                                    _delayBuffer[frameB * channels + ch] * frac;
                    
                    _delayBuffer[_writePos + ch] = input + (delayed * Feedback);
                    buffer[offset + i + ch] = (input * (1.0f - Mix)) + (delayed * Mix);
                }
                _writePos = (_writePos + channels) % bufferLen;
            }
            return samplesRead;
        }
    }
}