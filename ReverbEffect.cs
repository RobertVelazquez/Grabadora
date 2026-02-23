using System;
using NAudio.Wave;

namespace Grabadora
{
    public class ReverbEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        
        // Parámetros
        public float Mix { get; set; } = 0f; // 0.0 = Dry, 1.0 = Wet
        public float RoomSize { get; set; } = 0.5f; // Feedback gain

        // Estructuras para el algoritmo Schroeder (Mono aplicado a estéreo)
        private CombFilter[] _combFilters;
        private AllPassFilter[] _allPassFilters;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public ReverbEffect(ISampleProvider source)
        {
            _source = source;
            int sr = source.WaveFormat.SampleRate;

            // Tiempos de retardo típicos para Schroeder (en ms convertidos a muestras)
            // Comb filters: crean los ecos densos
            _combFilters = new CombFilter[]
            {
                new CombFilter((int)(0.0297 * sr)),
                new CombFilter((int)(0.0371 * sr)),
                new CombFilter((int)(0.0411 * sr)),
                new CombFilter((int)(0.0437 * sr))
            };

            // All-pass filters: difunden el sonido
            _allPassFilters = new AllPassFilter[]
            {
                new AllPassFilter((int)(0.005 * sr)),
                new AllPassFilter((int)(0.0017 * sr))
            };
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;

            for (int i = 0; i < samplesRead; i += channels)
            {
                // Procesamos como mono para el reverb (promedio L+R)
                float input = buffer[offset + i];
                if (channels == 2) input = (input + buffer[offset + i + 1]) * 0.5f;

                // 1. Paralelo: Comb Filters
                float reverbSignal = 0;
                foreach (var comb in _combFilters)
                {
                    reverbSignal += comb.Process(input, RoomSize);
                }

                // 2. Serie: All-Pass Filters
                foreach (var allPass in _allPassFilters)
                {
                    reverbSignal = allPass.Process(reverbSignal);
                }

                // 3. Mezcla (Wet/Dry)
                // Atenuamos un poco la señal reverb porque la suma de combs gana mucho volumen
                reverbSignal *= 0.2f; 

                for (int ch = 0; ch < channels; ch++)
                {
                    // Mezcla lineal
                    buffer[offset + i + ch] = (buffer[offset + i + ch] * (1 - Mix)) + (reverbSignal * Mix);
                }
            }
            return samplesRead;
        }

        // Clases internas auxiliares para el algoritmo
        private class CombFilter
        {
            private float[] _buffer;
            private int _index;
            public CombFilter(int size) { _buffer = new float[size]; }
            public float Process(float input, float feedback)
            {
                float output = _buffer[_index];
                _buffer[_index] = input + (output * feedback);
                _index = (_index + 1) % _buffer.Length;
                return output;
            }
        }

        private class AllPassFilter
        {
            private float[] _buffer;
            private int _index;
            public AllPassFilter(int size) { _buffer = new float[size]; }
            public float Process(float input)
            {
                float buffered = _buffer[_index];
                // Fórmula All-Pass estándar
                float output = -input + buffered;
                _buffer[_index] = input + (buffered * 0.5f); // 0.5 es un coeficiente de difusión fijo
                _index = (_index + 1) % _buffer.Length;
                return output;
            }
        }
    }
}