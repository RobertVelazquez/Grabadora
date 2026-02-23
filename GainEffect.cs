using NAudio.Wave;

namespace Grabadora
{
    public class GainEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public float Gain { get; set; } = 1.0f;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public GainEffect(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] *= Gain;
            }
            return read;
        }
    }
}