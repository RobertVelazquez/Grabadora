using System;
using NAudio.Wave;

namespace Grabadora
{
    public partial class AudioEngine
    {
        public void Play()
        {
            if (_isExporting) return;

            // Si por alguna razón el dispositivo de salida fue liberado pero el motor
            // de mezcla sigue inicializado, re‑inicializamos la cadena de salida.
            if (_outputDevice == null && _mixer != null)
            {
                InitializeOutputChain(_mixer.WaveFormat);
            }

            if (_outputDevice == null) return; // No hay nada que reproducir (sin dispositivo ni proyecto)

            // Si ya está reproduciendo, no hacemos nada.
            if (_outputDevice.PlaybackState == PlaybackState.Playing)
            {
                IsTransportPlaying = true;
                return;
            }

            IsTransportPlaying = true;
            _outputDevice.Play();

            // Iniciar secuenciador MIDI para pistas de instrumento virtual
            StartMidiPlayback();
        }

        public void Stop()
        {
            IsTransportPlaying = false;

            // En modo ASIO, si estamos monitorizando, NO detenemos el dispositivo
            // para que el micrófono siga sonando (pero las pistas se silencian/rebobinan).
            if (IsAsio && IsMonitoringInput)
            {
                StopMidiPlayback();
                if (_mixer != null)
                {
                    foreach (var track in _tracks)
                    {
                        if (track.Reader != null) track.Reader.Position = 0;
                    }
                }
            }
            else
            {
                _outputDevice?.Stop();
                StopMidiPlayback();
                // Reiniciar todas las pistas al principio
                if (_mixer != null)
                {
                    foreach (var track in _tracks)
                    {
                        if (track.Reader != null) track.Reader.Position = 0;
                    }
                }
            }
        }

        public void Pause()
        {
            IsTransportPlaying = false;
            _outputDevice?.Pause();
            StopMidiPlayback();
        }

        public void SetPosition(TimeSpan time)
        {
            foreach (var track in _tracks)
            {
                if (track.Reader != null)
                {
                    // Alinear al bloque para evitar errores de lectura
                    long position = (long)(time.TotalSeconds * track.Reader.WaveFormat.AverageBytesPerSecond);
                    position -= (position % track.Reader.WaveFormat.BlockAlign);
                    track.Reader.Position = Math.Max(0, Math.Min(track.Reader.Length, position));
                }
            }
            
            if (_metronome != null)
            {
                 long samplePos = (long)(time.TotalSeconds * _metronome.WaveFormat.SampleRate);
                 _metronome.SetPosition(samplePos);
            }
        }
    }
}
