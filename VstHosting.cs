// Código de soporte para hospedar plugins VST2 usando VST.NET.
// Está protegido por una directiva de compilación para evitar errores
// cuando las dependencias de Jacobi.Vst no están presentes.
#if VST_NET_HOSTING
using System;
using NAudio.Wave;
using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;
using Jacobi.Vst.Host.Interop;

namespace Grabadora
{
    /// <summary>
    /// Host VST muy básico: implementa los comandos que los plugins suelen pedir
    /// con valores por defecto, suficientes para muchos efectos sencillos.
    /// </summary>
    internal sealed class MinimalHostCommandStub : IVstHostCommandStub
    {
        private readonly IVstHostCommands20 _commands;

        public MinimalHostCommandStub(float sampleRate, int blockSize)
        {
            _commands = new MinimalHostCommands(sampleRate, blockSize);
        }

        public IVstPluginContext PluginContext { get; set; } = null!;

        public IVstHostCommands20 Commands => _commands;

        /// <summary>
        /// Implementación mínima de los comandos del host.
        /// </summary>
        private sealed class MinimalHostCommands : IVstHostCommands20
        {
            private readonly float _sampleRate;
            private readonly int _blockSize;

            public MinimalHostCommands(float sampleRate, int blockSize)
            {
                _sampleRate = sampleRate;
                _blockSize = blockSize;
            }

            // IVstHostCommands10
            public void SetParameterAutomated(int index, float value)
            {
                // No grabamos automatización hacia el host.
            }

            public int GetVersion() => 1000;

            public int GetCurrentPluginID() => 0;

            public void ProcessIdle()
            {
                // Nada especial que hacer en este host sencillo.
            }

            // IVstHostCommands20
            public VstTimeInfo GetTimeInfo(VstTimeInfoFlags filterFlags)
            {
                // Información de tiempo muy básica: solo sample rate.
                return new VstTimeInfo
                {
                    SampleRate = _sampleRate
                };
            }

            public bool ProcessEvents(VstEvent[] events) => false;

            public bool IoChanged() => false;

            public bool SizeWindow(int width, int height) => false;

            public float GetSampleRate() => _sampleRate;

            public int GetBlockSize() => _blockSize;

            public int GetInputLatency() => 0;

            public int GetOutputLatency() => 0;

            public VstProcessLevels GetProcessLevel() => VstProcessLevels.Unknown;

            public VstAutomationStates GetAutomationState() => VstAutomationStates.Off;

            public string GetVendorString() => "Grabadora";

            public string GetProductString() => "Grabadora VST Host";

            public int GetVendorVersion() => 1;

            public VstCanDoResult CanDo(string cando) => VstCanDoResult.No;

            public VstHostLanguage GetLanguage() => VstHostLanguage.NotSupported;

            public string GetDirectory() => AppDomain.CurrentDomain.BaseDirectory;

            public bool UpdateDisplay() => false;

            public bool BeginEdit(int index) => false;

            public bool EndEdit(int index) => false;

            public bool OpenFileSelector(VstFileSelect fileSelect) => false;

            public bool CloseFileSelector(VstFileSelect fileSelect) => true;
        }
    }

    /// <summary>
    /// Slot de efecto VST2 que envuelve un ISampleProvider y procesa audio mediante VST.NET.
    /// Se asume audio en coma flotante intercalado (NAudio).
    /// </summary>
    internal sealed class VstPluginSlot : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider _source;
        private readonly WaveFormat _waveFormat;
        private readonly VstPluginContext _pluginContext;
        private readonly Jacobi.Vst.Core.IVstPluginCommands24 _pluginCommands;
        private readonly VstAudioBufferManager _inputMgr;
        private readonly VstAudioBufferManager _outputMgr;
        private readonly int _blockSize;
        private readonly int _inputCount;
        private readonly int _outputCount;
        private bool _disposed;

        public VstPluginSlot(string pluginPath, ISampleProvider source, int sampleRate, int blockSize = 1024)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _waveFormat = source.WaveFormat;
            _blockSize = Math.Max(32, blockSize);

            if (_waveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new NotSupportedException("El slot VST solo admite audio en coma flotante.");

            var hostStub = new MinimalHostCommandStub(sampleRate, _blockSize);

            _pluginContext = VstPluginContext.Create(pluginPath, hostStub);

            // Abrir el plugin
            _pluginContext.PluginCommandStub.Commands.Open();

            _pluginCommands = _pluginContext.PluginCommandStub.Commands;

            var info = _pluginContext.PluginInfo;
            _inputCount = Math.Max(1, info.AudioInputCount);
            _outputCount = Math.Max(1, info.AudioOutputCount);

            _inputMgr = new VstAudioBufferManager(_inputCount, _blockSize);
            _outputMgr = new VstAudioBufferManager(_outputCount, _blockSize);
        }

        public WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_disposed) return 0;

            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return read;

            // Si el plugin no tiene entradas/salidas, dejamos pasar el audio original.
            if (_inputCount <= 0 || _outputCount <= 0)
                return read;

            int channels = _waveFormat.Channels;
            if (channels <= 0)
                return read;

            int totalFrames = read / channels;
            int processedFrames = 0;

            while (processedFrames < totalFrames)
            {
                int framesThis = Math.Min(_blockSize, totalFrames - processedFrames);

                // Limpiar buffers de entrada/salida
                foreach (VstAudioBuffer buf in _inputMgr.Buffers)
                    buf.Clear();
                foreach (VstAudioBuffer buf in _outputMgr.Buffers)
                    buf.Clear();

                // Copiar desde el buffer intercalado de NAudio a los buffers de entrada del plugin
                for (int ch = 0; ch < _inputCount && ch < channels; ch++)
                {
                    var inBuf = _inputMgr.Buffers[ch];
                    var span = inBuf.AsSpan(0, framesThis);

                    for (int i = 0; i < framesThis; i++)
                    {
                        int srcIndex = offset + ((processedFrames + i) * channels) + ch;
                        if (srcIndex < offset + read)
                            span[i] = buffer[srcIndex];
                    }
                }

                // Procesar con el plugin
                _pluginCommands.ProcessReplacing(_inputMgr.Buffers, _outputMgr.Buffers);

                // Copiar salidas de vuelta al buffer intercalado
                for (int ch = 0; ch < channels; ch++)
                {
                    int srcCh = Math.Min(ch, _outputCount - 1);
                    var outBuf = _outputMgr.Buffers[srcCh];
                    var span = outBuf.AsSpan(0, framesThis);

                    for (int i = 0; i < framesThis; i++)
                    {
                        int dstIndex = offset + ((processedFrames + i) * channels) + ch;
                        if (dstIndex < offset + read)
                            buffer[dstIndex] = span[i];
                    }
                }

                processedFrames += framesThis;
            }

            return read;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            try
            {
                _pluginContext.PluginCommandStub.Commands.Close();
            }
            catch
            {
                // Ignorar errores al cerrar.
            }

            _pluginContext.Dispose();
            _inputMgr.Dispose();
            _outputMgr.Dispose();
        }
    }
}
#endif
