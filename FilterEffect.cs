using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace Grabadora
{
    public class FilterEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private BiQuadFilter?[] _filters;
        private float _cutoff;
        private float _q;
        private FilterType _type;

        public FilterType CurrentType => _type;
        public float CurrentCutoff => _cutoff;

        public enum FilterType { None, LowPass, HighPass }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public FilterEffect(ISampleProvider source)
        {
            _source = source;
            _filters = new BiQuadFilter[source.WaveFormat.Channels];
            _cutoff = 1000; // Valor por defecto dentro del rango de la UI
            _q = 0.707f; // Q estándar para respuesta plana (Butterworth)
            _type = FilterType.None;
            UpdateFilters();
        }

        public void Configure(FilterType type, float cutoff)
        {
            _type = type;
            _cutoff = cutoff;
            // Protecciones de rango para evitar crash del filtro
            if (_cutoff < 20) _cutoff = 20;
            if (_cutoff > WaveFormat.SampleRate / 2) _cutoff = WaveFormat.SampleRate / 2 - 100;
            
            UpdateFilters();
        }

        private void UpdateFilters()
        {
            for (int i = 0; i < _filters.Length; i++)
            {
                if (_type == FilterType.LowPass)
                    _filters[i] = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, _cutoff, _q);
                else if (_type == FilterType.HighPass)
                    _filters[i] = BiQuadFilter.HighPassFilter(WaveFormat.SampleRate, _cutoff, _q);
                else
                    _filters[i] = null;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            if (_type == FilterType.None) return samplesRead;

            for (int i = 0; i < samplesRead; i++)
            {
                int ch = i % WaveFormat.Channels;
                if (_filters[ch] != null)
                {
                    buffer[offset + i] = _filters[ch]!.Transform(buffer[offset + i]);
                }
            }
            return samplesRead;
        }
    }
}