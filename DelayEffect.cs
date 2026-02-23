using System;
using NAudio.Wave;

namespace Grabadora
{
    public class DelayEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _delayBuffer;
        private int _bufferPosition;
        private int _delaySamples;

        public int CurrentDelay { get; private set; }
        public float Feedback { get; set; } // 0.0 to <1.0
        public float WetMix { get; set; }   // 0.0 to 1.0

        public WaveFormat WaveFormat => _source.WaveFormat;

        public DelayEffect(ISampleProvider source, int maxDelayMilliseconds)
        {
            _source = source;
            // Max buffer size for stereo
            int maxBufferSize = (int)((maxDelayMilliseconds / 1000.0) * WaveFormat.SampleRate * WaveFormat.Channels);
            _delayBuffer = new float[maxBufferSize];
            // Establecer valores por defecto para evitar errores en la UI
            SetDelay(250);
            Feedback = 0.5f;
            WetMix = 0.0f;
        }

        public void SetDelay(int delayMilliseconds)
        {
            CurrentDelay = delayMilliseconds;
            _delaySamples = (int)((delayMilliseconds / 1000.0) * WaveFormat.SampleRate * WaveFormat.Channels);
            if (_delaySamples >= _delayBuffer.Length)
            {
                _delaySamples = _delayBuffer.Length - 1;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            for (int i = 0; i < samplesRead; i++)
            {
                int readPosition = (_bufferPosition - _delaySamples + _delayBuffer.Length) % _delayBuffer.Length;
                float delayedSample = _delayBuffer[readPosition];

                float inputSample = buffer[offset + i];
                
                // Mix dry (original) and wet (con efecto) signals
                float outputSample = (inputSample * (1 - WetMix)) + (delayedSample * WetMix);

                // Escribir en el buffer de delay con feedback para las repeticiones
                _delayBuffer[_bufferPosition] = inputSample + (delayedSample * Feedback);

                buffer[offset + i] = outputSample;

                _bufferPosition = (_bufferPosition + 1) % _delayBuffer.Length;
            }

            return samplesRead;
        }
    }
}