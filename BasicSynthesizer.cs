using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Grabadora
{
    /// <summary>
    /// Sintetizador muy simple pensado como base para pistas MIDI.
    /// Implementa ISampleProvider pero, de momento, solo genera silencio
    /// con la estructura necesaria para futuras extensiones.
    /// </summary>
    public class BasicSynthesizer : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }

        private readonly object _lock = new object();
        private class Voice
        {
            public double Phase;
            public double Frequency;
            public float Velocity;
        }

        private readonly Dictionary<int, Voice> _voices = new Dictionary<int, Voice>();

        public BasicSynthesizer(int sampleRate = 44100, int channels = 2)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        /// <summary>
        /// Activa una nota. Por ahora solo se registra el estado; la salida es silencio.
        /// </summary>
        public void NoteOn(int noteNumber, float velocity)
        {
            lock (_lock)
            {
                // Calcular frecuencia MIDI estándar (A4 = 440Hz, nota 69)
                double semitonesFromA4 = noteNumber - 69;
                double frequency = 440.0 * Math.Pow(2.0, semitonesFromA4 / 12.0);

                if (!_voices.TryGetValue(noteNumber, out var voice))
                {
                    voice = new Voice();
                    _voices[noteNumber] = voice;
                }

                voice.Frequency = frequency;
                voice.Velocity = Math.Max(0f, Math.Min(1f, velocity));
            }
        }

        /// <summary>
        /// Desactiva una nota previamente activada.
        /// </summary>
        public void NoteOff(int noteNumber)
        {
            lock (_lock)
            {
                _voices.Remove(noteNumber);
            }
        }

        /// <summary>
        /// Genera muestras de audio. Actualmente devuelve silencio;
        /// se puede extender para producir ondas según las notas activas.
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);

            lock (_lock)
            {
                if (_voices.Count == 0)
                {
                    // Sin notas activas: silencio
                    return count;
                }

                int channels = WaveFormat.Channels;
                int frames = count / channels;
                double sampleRate = WaveFormat.SampleRate;

                for (int n = 0; n < frames; n++)
                {
                    float sample = 0f;

                    foreach (var kvp in _voices)
                    {
                        var voice = kvp.Value;
                        sample += (float)(Math.Sin(voice.Phase) * voice.Velocity);
                        voice.Phase += 2.0 * Math.PI * voice.Frequency / sampleRate;
                        if (voice.Phase > 2.0 * Math.PI)
                            voice.Phase -= 2.0 * Math.PI;
                    }

                    // Evitar clipping extremo (limitación simple)
                    if (sample > 1f) sample = 1f;
                    else if (sample < -1f) sample = -1f;

                    int baseIndex = offset + n * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        buffer[baseIndex + ch] = sample;
                    }
                }
            }

            return count;
        }
    }
}
