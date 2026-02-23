using System;

namespace Grabadora
{
    /// <summary>
    /// Implementación de un compresor de rango dinámico.
    /// Esta clase no es parte de NAudio, es una implementación personalizada necesaria para CompressorEffect.
    /// </summary>
    public class WdlDynamicRangeCompressor
    {
        public float SampleRate { get; set; } = 44100;
        public float Threshold { get; set; } = 0; // dB
        public float Ratio { get; set; } = 1;
        public float Attack { get; set; } = 10; // ms
        public float Release { get; set; } = 100; // ms
        public float MakeUpGain { get; set; } = 0; // dB

        private float _gain;

        public WdlDynamicRangeCompressor()
        {
            _gain = 1.0f;
        }

        /// <summary>
        /// Procesa un par de muestras estéreo (Left/Right)
        /// </summary>
        public void Process(ref float left, ref float right)
        {
            // 1. Detectar nivel de pico (Peak detection)
            float l = Math.Abs(left);
            float r = Math.Abs(right);
            float max = l > r ? l : r;

            // Evitar log de 0
            if (max < 1e-6f) max = 1e-6f;

            // 2. Convertir a dB
            float envDb = (float)(20 * Math.Log10(max));

            // 3. Calcular la reducción de ganancia objetivo (Gain Reduction)
            float targetGainDb = 0;
            if (envDb > Threshold)
            {
                // Fórmula de compresión: (Input - Threshold) * (1 - 1/Ratio)
                // Esto calcula cuánto debemos bajar el volumen en dB
                targetGainDb = (Threshold - envDb) * (1.0f - 1.0f / Ratio);
            }

            // Aplicar MakeUp Gain (si se usara)
            targetGainDb += MakeUpGain;

            // 4. Convertir ganancia objetivo a lineal
            float targetGain = (float)Math.Pow(10, targetGainDb / 20.0);

            // 5. Suavizado de la ganancia (Attack / Release)
            // Determinar si estamos atacando (reduciendo ganancia) o liberando (recuperando ganancia)
            float coeff;
            if (targetGain < _gain)
            {
                // Attack: El compresor actúa rápido para bajar el volumen
                coeff = (float)Math.Exp(-1.0 / (Attack * 0.001 * SampleRate));
            }
            else
            {
                // Release: El compresor deja de actuar lentamente
                coeff = (float)Math.Exp(-1.0 / (Release * 0.001 * SampleRate));
            }

            // Aplicar filtro de suavizado a la ganancia actual
            _gain = targetGain + coeff * (_gain - targetGain);

            // 6. Aplicar la ganancia calculada a las muestras originales
            left *= _gain;
            right *= _gain;
        }
    }
}