using System;
using NAudio.Wave;
using SoundTouch;

namespace Grabadora
{
    /// <summary>
    /// Efecto de Time Stretching (Cambio de Tempo sin cambio de Tono)
    /// Utiliza un buffer circular para permitir solapamiento y repetición de granos.
    /// </summary>
    public class TimeStretchEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly SoundTouchProcessor _processor;
        private readonly float[] _inputBuffer;
        private float[] _outputBuffer; // Buffer intermedio para la salida de SoundTouch
        private float _tempo = 1.0f;
        

        public WaveFormat WaveFormat => _source.WaveFormat;

        public float Tempo
        {
            get => _tempo;
            set 
            {
                _tempo = Math.Max(0.5f, Math.Min(2.0f, value));
                if (_processor != null) _processor.Tempo = _tempo;
            }
        }

        public TimeStretchEffect(ISampleProvider source)
        {
            _source = source;
            
            // Inicializar procesador SoundTouch
            _processor = new SoundTouchProcessor
            {
                SampleRate = source.WaveFormat.SampleRate,
                Channels = source.WaveFormat.Channels,
                Tempo = _tempo
            };

            // Buffer temporal para leer de la fuente (~200ms es suficiente)
            _inputBuffer = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels / 5]; 
            // Buffer de salida inicial (se redimensionará si es necesario)
            _outputBuffer = new float[_inputBuffer.Length];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Asegurar que el buffer intermedio sea suficiente
            if (_outputBuffer == null || _outputBuffer.Length < count)
            {
                _outputBuffer = new float[count];
            }

            int samplesRead = 0;

            // 1. Leer salida pendiente del procesador
            if (_processor.AvailableSamples > 0)
            {
                // SoundTouch escribe siempre en el índice 0, usamos buffer intermedio
                int received = _processor.ReceiveSamples(_outputBuffer, count);
                // Copiamos al buffer de destino en el offset correcto
                Array.Copy(_outputBuffer, 0, buffer, offset, received);
                samplesRead += received;
            }

            // 2. Alimentar procesador si hace falta más audio
            while (samplesRead < count)
            {
                int read = _source.Read(_inputBuffer, 0, _inputBuffer.Length);
                if (read == 0)
                {
                    _processor.Flush();
                    // Intentar leer el remanente tras flush
                    int received = _processor.ReceiveSamples(_outputBuffer, count - samplesRead);
                    Array.Copy(_outputBuffer, 0, buffer, offset + samplesRead, received);
                    samplesRead += received;
                    break;
                }

                _processor.PutSamples(_inputBuffer, read);
                
                // Intentar leer de nuevo del procesador
                if (samplesRead < count)
                {
                    int received = _processor.ReceiveSamples(_outputBuffer, count - samplesRead);
                    Array.Copy(_outputBuffer, 0, buffer, offset + samplesRead, received);
                    samplesRead += received;
                }
            }
            
            return samplesRead;
        }
    }
}