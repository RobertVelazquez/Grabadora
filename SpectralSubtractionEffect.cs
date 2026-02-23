using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace Grabadora
{
    public class SpectralSubtractionEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _noiseProfile;
        private readonly float _strength;
        private readonly float _spectralFloor;
        
        private readonly int _fftSize;
        private readonly int _hopSize; 
        private readonly int _m; 
        private readonly int _channels;
        
        private readonly float[] _inputBuffer;
        private readonly float[] _overlapBuffer;
        private readonly Complex[] _fftBuffer;
        private readonly float[] _window;
        
        private int _inputBufferCount; // En muestras totales (floats)
        private bool _endOfStream;
        private int _overlapPending;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public SpectralSubtractionEffect(ISampleProvider source, float[] noiseProfile, float strength = 1.0f)
        {
            _source = source;
            _noiseProfile = noiseProfile;
            // Limitar fuerza a 0-2 (rango de la UI) y suavizar comportamiento
            _strength = Math.Clamp(strength, 0f, 2f);
            // Mantener un resto muy pequeño del perfil de ruido para evitar artefactos,
            // pero permitir que el ruido baje de manera claramente audible.
            _spectralFloor = 0.01f; // ~ -40 dB respecto al nivel de ruido
            _channels = source.WaveFormat.Channels;
            
            // El perfil de ruido tiene tamaño (FFT / 2) + 1. Reconstruimos el tamaño FFT original.
            _fftSize = (noiseProfile.Length - 1) * 2; 
            if (_fftSize <= 0) _fftSize = 1024; // Valor por defecto si falla el cálculo
            // Asegurar tamaño de FFT como potencia de 2
            int pow2 = 1;
            while (pow2 < _fftSize) pow2 <<= 1;
            if (pow2 != _fftSize) _fftSize = pow2;
            
            _hopSize = _fftSize / 2;
            _m = (int)Math.Log(_fftSize, 2.0);
            
            // Los buffers deben alojar datos para todos los canales
            _inputBuffer = new float[_fftSize * _channels];
            _overlapBuffer = new float[_fftSize * _channels];
            _fftBuffer = new Complex[_fftSize]; // Reutilizable por canal
            _window = new float[_fftSize];
            for (int i = 0; i < _fftSize; i++)
            {
                _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (_fftSize - 1)))); // Hann
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesWritten = 0;
            int inputBufferSize = _fftSize * _channels;
            int hopSizeBytes = _hopSize * _channels;
            
            while (samplesWritten < count)
            {
                // Si estamos drenando el final, devolver el resto del overlap
                if (_endOfStream && _overlapPending > 0)
                {
                    int toCopy = Math.Min(count - samplesWritten, _overlapPending);
                    Array.Copy(_overlapBuffer, 0, buffer, offset + samplesWritten, toCopy);
                    samplesWritten += toCopy;
                    Array.Copy(_overlapBuffer, toCopy, _overlapBuffer, 0, _overlapPending - toCopy);
                    Array.Clear(_overlapBuffer, _overlapPending - toCopy, toCopy);
                    _overlapPending -= toCopy;
                    if (_overlapPending == 0) break;
                    continue;
                }

                // Necesitamos llenar el buffer de entrada
                int needed = inputBufferSize - _inputBufferCount;
                
                if (needed > 0)
                {
                    int read = _source.Read(_inputBuffer, _inputBufferCount, needed);
                    if (read > 0)
                    {
                        _inputBufferCount += read;
                    }
                    
                    // Si leímos menos de lo necesario (o 0), rellenamos con ceros para procesar el final
                    if (read < needed)
                    {
                        // Fin del stream
                        if (_inputBufferCount == 0)
                        {
                            _endOfStream = true;
                            break;
                        }
                        
                        // Rellenar con ceros si tenemos datos parciales al final
                        Array.Clear(_inputBuffer, _inputBufferCount, inputBufferSize - _inputBufferCount);
                        _inputBufferCount = inputBufferSize;
                        _endOfStream = true;
                    }
                }

                if (_inputBufferCount == inputBufferSize)
                {
                    ProcessBlock();

                    // Copiar los primeros HopSize samples del buffer de superposición a la salida
                    int toCopy = Math.Min(count - samplesWritten, hopSizeBytes);
                    Array.Copy(_overlapBuffer, 0, buffer, offset + samplesWritten, toCopy);
                    samplesWritten += toCopy;

                    // Desplazar el buffer de superposición (Overlap Buffer)
                    Array.Copy(_overlapBuffer, hopSizeBytes, _overlapBuffer, 0, hopSizeBytes);
                    Array.Clear(_overlapBuffer, hopSizeBytes, hopSizeBytes);
                    _overlapPending = hopSizeBytes;

                    // Desplazar el buffer de entrada por HopSize
                    Array.Copy(_inputBuffer, hopSizeBytes, _inputBuffer, 0, hopSizeBytes);
                    _inputBufferCount -= hopSizeBytes;
                }
                else
                {
                    break;
                }
            }
            return samplesWritten;
        }

        private void ProcessBlock()
        {
            float scale = 1.0f / _fftSize;

            // Procesar cada canal independientemente
            for (int ch = 0; ch < _channels; ch++)
            {
                // 1. Ventana y FFT
                for (int i = 0; i < _fftSize; i++)
                {
                    // Acceso intercalado: [i * channels + ch]
                    _fftBuffer[i].X = _inputBuffer[i * _channels + ch] * _window[i];
                    _fftBuffer[i].Y = 0;
                }

                FastFourierTransform.FFT(true, _m, _fftBuffer);

                // 2. Sustracción Espectral (selectiva por relación señal/ruido)
                for (int i = 0; i <= _fftSize / 2; i++)
                {
                    double real = _fftBuffer[i].X;
                    double imag = _fftBuffer[i].Y;
                    double mag = Math.Sqrt(real * real + imag * imag);
                    double phase = Math.Atan2(imag, real);

                    double noise = i < _noiseProfile.Length ? _noiseProfile[i] : 0.0;
                    double newMag = mag;

                    if (noise > 0.0)
                    {
                        // Relación señal/ruido aproximada en esta banda
                        double snr = mag / (noise + 1e-12);

                        // Zonas muy próximas al ruido (snr <= 2): recorte fuerte
                        if (snr <= 2.0)
                        {
                            double reduction = noise * (_strength * 2.0); // over-subtraction en bandas ruidosas
                            newMag = mag - reduction;
                        }
                        // Zonas algo por encima del ruido (2 < snr <= 5): recorte moderado
                        else if (snr <= 5.0)
                        {
                            double reduction = noise * _strength;
                            newMag = mag - reduction;
                        }
                        // Zonas claramente de señal (snr > 5): apenas tocar (pequeña corrección opcional)
                        else
                        {
                            double reduction = noise * (_strength * 0.2);
                            newMag = mag - reduction;
                        }

                        // No bajar de un suelo relativo al ruido para evitar artefactos
                        double noiseFloor = noise * _spectralFloor;
                        if (newMag < noiseFloor) newMag = noiseFloor;
                    }

                    _fftBuffer[i].X = (float)(newMag * Math.Cos(phase));
                    _fftBuffer[i].Y = (float)(newMag * Math.Sin(phase));
                    
                    if (i > 0 && i < _fftSize / 2)
                    {
                        _fftBuffer[_fftSize - i].X = _fftBuffer[i].X;
                        _fftBuffer[_fftSize - i].Y = -_fftBuffer[i].Y;
                    }
                }

                // 3. IFFT
                FastFourierTransform.FFT(false, _m, _fftBuffer);

                // 4. Overlap-Add
                for (int i = 0; i < _fftSize; i++)
                {
                    // Acumulación intercalada
                    _overlapBuffer[i * _channels + ch] += _fftBuffer[i].X * scale; 
                }
            }
        }
    }
}