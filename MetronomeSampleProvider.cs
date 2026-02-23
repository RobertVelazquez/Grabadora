using System;
using NAudio.Wave;

namespace Grabadora
{
    public class MetronomeSampleProvider : ISampleProvider
    {
        private readonly WaveFormat _waveFormat;
        private float[] _highClick;
        private float[] _lowClick;
        private long _samplePosition;

        public double Bpm { get; set; } = 120;
        public bool Enabled { get; set; }
        public WaveFormat WaveFormat => _waveFormat;

        public MetronomeSampleProvider(WaveFormat format)
        {
            _waveFormat = format;
            GenerateClicks();
        }

        private void GenerateClicks()
        {
            // Generar sonidos de clic (Onda sinusoidal con decaimiento)
            _highClick = GenerateClick(1000, 0.05f); // Tono alto (1er golpe)
            _lowClick = GenerateClick(800, 0.05f);   // Tono bajo (otros golpes)
        }

        private float[] GenerateClick(float freq, float duration)
        {
            int samples = (int)(_waveFormat.SampleRate * duration);
            var buffer = new float[samples * _waveFormat.Channels];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / _waveFormat.SampleRate;
                // Seno con decaimiento exponencial
                float val = (float)(Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-100 * t)); 
                for (int ch = 0; ch < _waveFormat.Channels; ch++) 
                    buffer[i * _waveFormat.Channels + ch] = val * 0.5f; // Volumen 0.5
            }
            return buffer;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (!Enabled)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int channels = _waveFormat.Channels;
            int samplesPerBeat = (int)(_waveFormat.SampleRate * 60.0 / Bpm);
            int samplesRead = 0;

            while (samplesRead < count)
            {
                long beatIndex = _samplePosition / samplesPerBeat;
                long sampleInBeat = _samplePosition % samplesPerBeat;
                
                float[] click = (beatIndex % 4 == 0) ? _highClick : _lowClick;
                int clickFrames = click.Length / channels;

                int remainingInBatch = count - samplesRead; // Total floats remaining in buffer
                int remainingInBatchFrames = remainingInBatch / channels;

                if (sampleInBeat < clickFrames)
                {
                    // Estamos dentro del sonido del clic
                    int clickOffsetFrames = (int)sampleInBeat;
                    int framesToCopy = Math.Min(remainingInBatchFrames, clickFrames - clickOffsetFrames);
                    
                    if (framesToCopy == 0 && remainingInBatch > 0) 
                    {
                         Array.Clear(buffer, offset + samplesRead, remainingInBatch);
                         samplesRead += remainingInBatch;
                         break;
                    }

                    int floatsToCopy = framesToCopy * channels;
                    Array.Copy(click, clickOffsetFrames * channels, buffer, offset + samplesRead, floatsToCopy);
                    
                    samplesRead += floatsToCopy;
                    _samplePosition += framesToCopy;
                }
                else
                {
                    // Silencio hasta el siguiente beat
                    long framesUntilNextBeat = samplesPerBeat - sampleInBeat;
                    int framesToZero = (int)Math.Min(remainingInBatchFrames, framesUntilNextBeat);
                    
                    if (framesToZero == 0 && remainingInBatch > 0)
                    {
                         Array.Clear(buffer, offset + samplesRead, remainingInBatch);
                         samplesRead += remainingInBatch;
                         break;
                    }

                    int floatsToZero = framesToZero * channels;
                    Array.Clear(buffer, offset + samplesRead, floatsToZero);
                    
                    samplesRead += floatsToZero;
                    _samplePosition += framesToZero;
                }
            }
            return count;
        }

        public void SetPosition(long samplePosition)
        {
            _samplePosition = samplePosition;
        }
    }
}