using System;
using NAudio.Wave;

namespace Grabadora
{
    public class DistortionEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _drive;
        
        public float Drive 
        { 
            get => _drive; 
            set => _drive = Math.Max(0.0f, Math.Min(1.0f, value)); 
        }
        
        public float Mix { get; set; } // 0.0 (Dry) to 1.0 (Wet)

        public WaveFormat WaveFormat => _source.WaveFormat;

        public DistortionEffect(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            
            if (Drive == 0 && Mix == 0) return samplesRead;

            // Mapear Drive 0..1 a Ganancia 1..50
            float gain = 1.0f + (Drive * 49.0f); 
            float mix = Math.Max(0f, Math.Min(1f, Mix));

            for (int i = 0; i < samplesRead; i++)
            {
                float input = buffer[offset + i];
                
                // Aplicar ganancia
                float x = input * gain;
                
                // Soft Clipping usando Tanh para un sonido cálido
                float wet = (float)Math.Tanh(x);

                // Mezclar señal original con distorsionada
                buffer[offset + i] = (input * (1 - mix)) + (wet * mix);
            }
            return samplesRead;
        }
    }
}