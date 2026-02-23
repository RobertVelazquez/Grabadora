using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Lame;
using NAudio.Dsp;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Timers;

#nullable disable

namespace Grabadora
{
    // Comparador para la búsqueda binaria de puntos de automatización
    public class AudioTrack : IDisposable
    {
        public AudioFileReader Reader { get; private set; } // Puede ser null durante grabación
        public float[] Samples { get; private set; }
        public BufferedWaveProvider MonitorProvider { get; } = null!; // Para monitorización en vivo
        public CircularBufferSampleProvider RecordingBuffer { get; } // OPTIMIZACIÓN: Buffer circular de floats para ASIO
        public string Name { get; set; }
        public Grabadora.BasicSynthesizer Synthesizer { get; private set; } // Referencia al sintetizador (si es pista MIDI)
        public List<MidiNote> MidiNotes { get; } = new List<MidiNote>();
        public List<TimeSpan> KickMarkers { get; set; } = new List<TimeSpan>();

    #if VST_NET_HOSTING
        // Ruta a un plugin VST2 opcional para esta pista
        public string VstPluginPath { get; set; }

        // Slot VST2 opcional insertado en la cadena de efectos
        public VstPluginSlot VstSlot { get; private set; }
    #endif

        // Objeto de bloqueo para sincronizar acceso a listas de automatización entre UI y Audio
        public readonly object AutomationLock = new object();

        private readonly AudioEngine _engine;
        // Effects chain for this track
        // (Propiedades de efectos de pista...)
        public double FadeInSeconds { get; set; } = 0.0;
        public double FadeOutSeconds { get; set; } = 0.0;
        
        public SmartFadeProvider FadeEffect { get; private set; } = null!;
        public Equalizer Equalizer { get; private set; } = null!;
        public TimeStretchEffect TimeStretchEffect { get; private set; } = null!;
        public DistortionEffect DistortionEffect { get; private set; } = null!;
        public ChorusEffect ChorusEffect { get; private set; } = null!;
        public FilterEffect FilterEffect { get; private set; } = null!;
        public CompressorEffect CompressorEffect { get; private set; } = null!;
        public DelayEffect DelayEffect { get; private set; } = null!;
        public ReverbEffect ReverbEffect { get; private set; } = null!;
        public CustomPanningProvider PanningProvider { get; private set; } = null!;
        public VolumeSampleProvider VolumeProvider { get; private set; } = null!;
        public MeteringSampleProvider MeteringProvider { get; private set; } = null!;
        public float LastPeakLeft { get; private set; }
        public float LastPeakRight { get; private set; }

        // Estado de mezcla
        public bool IsMuted { get; set; }
        public bool IsSolo { get; set; }
        public float UserVolume { get; set; } = 1.0f;

        // Automatización
        public List<AutomationPoint> VolumeAutomation { get; } = new List<AutomationPoint>();
        public List<AutomationPoint> PanAutomation { get; } = new List<AutomationPoint>();
        public Dictionary<string, List<AutomationPoint>> FxAutomation { get; } = new Dictionary<string, List<AutomationPoint>>();

        // The final provider in the chain, ready to be mixed
        public ISampleProvider FinalProvider { get; private set; } = null!;
        public ISampleProvider MixerInput { get; set; } // El proveedor que realmente se añade al mixer (puede incluir control de transporte)

        public AudioTrack(string filePath, WaveFormat targetFormat, AudioEngine engine)
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(filePath);
            // Modo Archivo: Leemos del disco
            Reader = new AudioFileReader(filePath);
            ISampleProvider sourceProvider = Reader;

            // If a target format is specified, conform to it.
            if (targetFormat != null)

            {
                // Asegurarnos de que el número de canales coincida con el del proyecto
                if (Reader.WaveFormat.Channels != targetFormat.Channels)
                {
                    if (Reader.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
                    {
                        // Pista mono en proyecto estéreo: duplicamos a L/R
                        sourceProvider = new MonoToStereoSampleProvider(sourceProvider);
                    }
                    else if (Reader.WaveFormat.Channels == 2 && targetFormat.Channels == 1)
                    {
                        // Pista estéreo en proyecto mono: promediamos canales
                        var stereoToMono = new StereoToMonoSampleProvider(sourceProvider)
                        {
                            LeftVolume = 0.5f,
                            RightVolume = 0.5f
                        };
                        sourceProvider = stereoToMono;
                    }
                    else
                    {
                        // Otros casos (5.1, etc.) no se soportan de momento
                        throw new NotSupportedException("Solo se soportan conversiones entre mono y estéreo.");
                    }
                }
            }

            _engine = engine;
            InitializeEffects(new InfiniteSampleProvider(sourceProvider), engine);

            // Read all samples into memory for visualization and editing
            // Usamos un lector separado para la visualización para no interferir con el estado del 'Reader' de reproducción.
            using (var readerForVisualization = new AudioFileReader(filePath))
            {
                long totalSamples = readerForVisualization.Length / (readerForVisualization.WaveFormat.BitsPerSample / 8);
                
                // Protección: Limitar a int.MaxValue y capturar OOM
                if (totalSamples > int.MaxValue) totalSamples = int.MaxValue;

                try
                {
                    Samples = new float[totalSamples];
                    // La lectura de NAudio solo acepta un entero para la longitud. Leemos en trozos para evitar desbordamiento en archivos grandes.
                    int samplesRead = 0;
                    int chunkSize = 1024 * 1024; // Leer en trozos de 4MB (1M de floats)
                    while (samplesRead < totalSamples)
                    {
                        int toRead = (int)Math.Min(chunkSize, totalSamples - samplesRead);
                        int read = readerForVisualization.Read(Samples, samplesRead, toRead);
                        if (read == 0) break; // Fin del stream
                        samplesRead += read;
                    }
                }
                catch (OutOfMemoryException)
                {
                    Samples = new float[0]; // Fallback seguro: sin visualización pero reproduce
                }
            }
        }

        public AudioTrack(WaveFormat format, WaveFormat targetFormat, AudioEngine engine)
        {
            _engine = engine;
            Name = "Grabando...";
            // Modo Grabación: Monitorizamos el buffer en vivo
            MonitorProvider = new BufferedWaveProvider(format);
            MonitorProvider.DiscardOnBufferOverflow = true;
            
            ISampleProvider source;
            // Convertir explícitamente PCM 16-bit a Float para evitar errores de "must be already floating point"
            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                source = new Pcm16BitToSampleProvider(MonitorProvider);
                // OPTIMIZACIÓN ASIO: Usar buffer circular de floats directo
                // 5 segundos de buffer es suficiente para absorber picos del sistema
                // RecordingBuffer = new CircularBufferSampleProvider(format.Channels, format.SampleRate, 5); // CORRECCIÓN: No necesario para PCM16
            }
            else if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                source = new WaveToSampleProvider(MonitorProvider);
                // CORRECCIÓN: Inicializar RecordingBuffer también para Float (ASIO)
                RecordingBuffer = new CircularBufferSampleProvider(format.Channels, format.SampleRate, 5);
                source = RecordingBuffer;
            }
            else
            {
                source = new WaveToSampleProvider(MonitorProvider);
            }

            if (targetFormat != null && source.WaveFormat.Channels != targetFormat.Channels)
            {
                if (source.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
                {
                    source = new MonoToStereoSampleProvider(source);
                }
                else if (source.WaveFormat.Channels == 2 && targetFormat.Channels == 1)
                {
                    var stereoToMono = new StereoToMonoSampleProvider(source)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    };
                    source = stereoToMono;
                }
                else
                {
                    throw new NotSupportedException("Solo se soportan conversiones entre mono y estéreo.");
                }
            }

            InitializeEffects(new InfiniteSampleProvider(source), engine);
        }

        // Nuevo constructor para Pistas de Instrumento
        public AudioTrack(Grabadora.BasicSynthesizer synth, WaveFormat targetFormat, AudioEngine engine)
        {
            _engine = engine;
            Name = "Instrumento Virtual";
            Synthesizer = synth;
            // El sintetizador es la fuente. No necesita InfiniteSampleProvider porque BasicSynthesizer ya es infinito.
            InitializeEffects(synth, engine);
        }

        private void InitializeEffects(ISampleProvider sourceProvider, AudioEngine engine)
        {
            // Cadena de efectos en tiempo real.
            // Orden aproximado: Filter -> EQ -> Distortion -> Chorus -> Delay -> Reverb -> Compressor -> Pan -> AutoPan -> AutoVol -> Fader -> Metering.
            // (El TimeStretchEffect se mantiene instanciado para poder controlar el tempo,
            //  pero de momento no se inserta en la ruta en vivo para evitar problemas de silencio
            //  hasta depurarlo bien.)

            // Fades siguen usando un proveedor dedicado (utilizado sobre todo para operaciones offline).
            FadeEffect = new SmartFadeProvider(sourceProvider, this);

            // Instanciamos el TimeStretchEffect pero no lo encadenamos todavía.
            TimeStretchEffect = new TimeStretchEffect(sourceProvider);

            ISampleProvider fxSource = sourceProvider;

            // Filtro
            FilterEffect = new FilterEffect(fxSource);
            fxSource = FilterEffect;

            // Ecualizador
            Equalizer = new Equalizer(fxSource, new float[] { 60, 250, 1000, 4000, 16000 });
            fxSource = Equalizer;

            // Distorsión
            DistortionEffect = new DistortionEffect(fxSource);
            fxSource = DistortionEffect;

            // Chorus / Modulación
            ChorusEffect = new ChorusEffect(fxSource);
            fxSource = ChorusEffect;

            // Delay
            DelayEffect = new DelayEffect(fxSource, 2000);
            fxSource = DelayEffect;

            // Reverb
            ReverbEffect = new ReverbEffect(fxSource);
            fxSource = ReverbEffect;

            // Compresor
            CompressorEffect = new CompressorEffect(fxSource);
            fxSource = CompressorEffect;

            // Automatización de efectos (lee las curvas y ajusta los parámetros de los efectos anteriores)
            var fxAutomationProvider = new FxAutomationProvider(fxSource, this, engine);
            fxSource = fxAutomationProvider;

#if VST_NET_HOSTING
            // Inserto VST2 opcional (si se ha configurado una ruta de plugin)
            if (!string.IsNullOrWhiteSpace(VstPluginPath))
            {
                try
                {
                    VstSlot = new VstPluginSlot(VstPluginPath, fxSource, engine.SampleRate);
                    fxSource = VstSlot;
                }
                catch
                {
                    // Si falla la carga del plugin, continuamos sin él.
                    VstSlot = null;
                }
            }
#endif

            // Ruta de reproducción con paneo y automatización
            PanningProvider = new CustomPanningProvider(fxSource);
            // Automatización de paneo y volumen por pista
            var panAutomationProvider = new AutomationPanProvider(PanningProvider, this, engine);
            var volumeAutomationProvider = new AutomationVolumeProvider(panAutomationProvider, this, engine);

            VolumeProvider = new VolumeSampleProvider(volumeAutomationProvider) { Volume = 1.0f };
            
            MeteringProvider = new MeteringSampleProvider(VolumeProvider);
            MeteringProvider.StreamVolume += (s, e) => {
                LastPeakLeft = e.MaxSampleValues.Length > 0 ? e.MaxSampleValues[0] : 0;
                LastPeakRight = e.MaxSampleValues.Length > 1 ? e.MaxSampleValues[1] : LastPeakLeft;
            };
            FinalProvider = MeteringProvider;
        }

        public void Dispose()
        {
            Reader?.Dispose();
#if VST_NET_HOSTING
            VstSlot?.Dispose();
#endif
        }
    }

    public partial class AudioEngine : IDisposable
    {
        private IWavePlayer _outputDevice;
        private MixingSampleProvider _mixer;
        private List<AudioTrack> _tracks = new List<AudioTrack>();

        // Grabación
        private WaveInEvent _recorder;
        private WaveFileWriter _recordingWriter;
        public bool IsRecording { get; private set; }
        private AudioTrack _currentRecordingTrack;
        private bool _isExporting = false;
        public bool IsTransportPlaying { get; private set; } // Nuevo: Control lógico de transporte
        public bool IsPlaying => _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
        private TimeSpan _recordStartTime = TimeSpan.Zero; // Momento del proyecto donde comienza la grabación
        
        private MixingSampleProvider _outputMixer; // Mezcla final (Proyecto + Metrónomo)
        public bool IsLooping { get; set; }
        public TimeSpan LoopStart { get; set; }
        public TimeSpan LoopEnd { get; set; }
        
        public int InputDeviceNumber { get; set; } = 0;
        public int OutputDeviceNumber { get; set; } = -1; // -1 = Mapper por defecto

        // Propiedad para saber si estamos usando ASIO
        public bool IsAsio => _outputDevice is AsioOut;

        // Propiedad para saber si el motor está inicializado (hay mixer/proyecto activo)
        public bool IsEngineInitialized => _mixer != null;

        private float _masterVolume = 1.0f;
        private MeteringSampleProvider _meteringProvider;
        private LoopingSampleProvider _loopingProvider;
        private MetronomeSampleProvider _metronome;
        private FftSampleProvider _fftProvider;
        public Equalizer MasterEqualizer { get; private set; }
        public CompressorEffect MasterLimiter { get; private set; }

        public bool MetronomeEnabled 
        { 
            get => _metronome?.Enabled ?? false; 
            set { if (_metronome != null) _metronome.Enabled = value; } 
        }

        private double _bpm = 120;
        public double Bpm 
        { 
            get => _bpm; 
            set { _bpm = value; if (_metronome != null) _metronome.Bpm = value; } 
        }

        // Información general del proyecto
        public string ProjectName { get; set; } = "Proyecto sin nombre";

        private TimeSpan _projectDuration = TimeSpan.Zero;
        public TimeSpan ProjectDuration
        {
            get => _projectDuration;
            set => _projectDuration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        // Frecuencia de muestreo actual del proyecto (o valor por defecto)
        public int SampleRate => _mixer?.WaveFormat.SampleRate ?? 44100;
        
        // Historial para Undo/Redo
        private string _currentFilePath;
        private Stack<string> _undoStack = new Stack<string>();
        private Stack<string> _redoStack = new Stack<string>();
        private bool _isRestoring = false;
        private float[] _capturedNoiseProfile; // Perfil de ruido capturado
        private float[] _clipboardSamples;
        private WaveFormat _clipboardFormat;
        private bool _isMonitoringInput; // Indica si la entrada está siendo monitorizada
        // Estados temporales para monitorización sin reproducir todo el proyecto
        private Dictionary<AudioTrack, bool> _monitoringMuteSnapshot;
        
        // Buffers reusables para ASIO (evita GC en cada callback)
        private float[] _asioInputBuffer;

        // Reproducción MIDI (pistas de instrumento / Piano Roll)
        private System.Timers.Timer _midiTimer;
        private readonly object _midiLock = new object();
        private readonly Dictionary<AudioTrack, HashSet<int>> _activeMidiNotes = new Dictionary<AudioTrack, HashSet<int>>();
        private bool _midiWallClockInitialized;
        private DateTime _midiWallClockStart;

        public event Action PlaybackStopped;
        public event Action RecordingStarted;
        public event Action<string> RecordingFinished;
        public event Action<float[]> FftCalculated; // Evento para el analizador de espectro
        public event Action<float, float> MeteringUpdate; // Evento para enviar niveles L/R

        public TimeSpan CurrentTime
        {
            get
            {
                // Si estamos grabando, el tiempo lo dicta la cantidad de audio capturado
                if (IsRecording && _recordingWriter != null)
                {
                    double bytes = _recordingWriter.Length;
                    double rate = _recordingWriter.WaveFormat.AverageBytesPerSecond;
                    return _recordStartTime + TimeSpan.FromSeconds(bytes / rate);
                }

                // Para la reproducción, el tiempo lo marca la primera pista.
                // SetPosition y Stop se aseguran de que todas las pistas estén sincronizadas.
                if (_tracks.Count > 0 && _tracks[0].Reader != null)
                {
                    return _tracks[0].Reader.CurrentTime;
                }

                return TimeSpan.Zero;
            }
        }
        public TimeSpan TotalTime
        {
            get
            {
                var tracksTotal = _tracks.Count > 0 ? _tracks.Max(t => t.Reader?.TotalTime ?? TimeSpan.Zero) : TimeSpan.Zero;
                return tracksTotal > ProjectDuration ? tracksTotal : ProjectDuration;
            }
        }
        public int TrackCount => _tracks.Count;
        public bool IsMonitoringInput => _isMonitoringInput;

        // Modo de interpolación para automatización (lineal por defecto, opcionalmente suavizado tipo Bezier)
        public bool UseBezierAutomation { get; set; }

        /// <summary>
        /// Evalúa una curva de automatización (lista de puntos tiempo/valor).
        /// Por defecto usa interpolación lineal. Si UseBezierAutomation está activo
        /// se aplica una interpolación suavizada tipo Hermite/Bezier entre puntos.
        /// </summary>
        internal float EvaluateAutomation(List<AutomationPoint> points, double timeSeconds, float defaultValue)
        {
            if (!UseBezierAutomation)
                return EvaluateAutomationLinear(points, timeSeconds, defaultValue);

            return EvaluateAutomationBezier(points, timeSeconds, defaultValue);
        }

        // Versión puramente lineal (comportamiento anterior)
        internal static float EvaluateAutomationLinear(List<AutomationPoint> points, double timeSeconds, float defaultValue)
        {
            if (points == null || points.Count == 0)
                return defaultValue;

            var searchKey = new AutomationPoint { TimeSeconds = timeSeconds };
            int index = points.BinarySearch(searchKey, AutomationPointComparer.Instance);

            if (index >= 0)
                return points[index].Value;

            int nextIndex = ~index;

            if (nextIndex == 0)
                return points[0].Value;
            if (nextIndex == points.Count)
                return points[points.Count - 1].Value;

            var a = points[nextIndex - 1];
            var b = points[nextIndex];

            double t = (timeSeconds - a.TimeSeconds) / (b.TimeSeconds - a.TimeSeconds);
            return (float)(a.Value + (b.Value - a.Value) * t);
        }

        // Versión suavizada: cubic Hermite usando puntos vecinos como tangentes aproximadas
        internal static float EvaluateAutomationBezier(List<AutomationPoint> points, double timeSeconds, float defaultValue)
        {
            if (points == null || points.Count == 0)
                return defaultValue;

            var searchKey = new AutomationPoint { TimeSeconds = timeSeconds };
            int index = points.BinarySearch(searchKey, AutomationPointComparer.Instance);

            if (index >= 0)
                return points[index].Value;

            int nextIndex = ~index;

            if (nextIndex == 0)
                return points[0].Value;
            if (nextIndex == points.Count)
                return points[points.Count - 1].Value;

            int i1 = nextIndex - 1;
            int i2 = nextIndex;

            var a = points[i1];
            var b = points[i2];

            double dt = b.TimeSeconds - a.TimeSeconds;
            if (dt <= 0)
                return a.Value;

            double t = (timeSeconds - a.TimeSeconds) / dt;

            // Calcular pendientes en los extremos usando vecinos si existen
            double m0;
            if (i1 > 0)
            {
                var prev = points[i1 - 1];
                double dtPrev = a.TimeSeconds - prev.TimeSeconds;
                m0 = dtPrev > 0 ? (a.Value - prev.Value) / dtPrev : (b.Value - a.Value) / dt;
            }
            else
            {
                m0 = (b.Value - a.Value) / dt;
            }

            double m1;
            if (i2 < points.Count - 1)
            {
                var next = points[i2 + 1];
                double dtNext = next.TimeSeconds - b.TimeSeconds;
                m1 = dtNext > 0 ? (next.Value - b.Value) / dtNext : (b.Value - a.Value) / dt;
            }
            else
            {
                m1 = (b.Value - a.Value) / dt;
            }

            // Polinomio de Hermite cúbico
            double tt = t * t;
            double ttt = tt * t;

            double h00 = 2 * ttt - 3 * tt + 1;
            double h10 = ttt - 2 * tt + t;
            double h01 = -2 * ttt + 3 * tt;
            double h11 = ttt - tt;

            double value = h00 * a.Value + h10 * m0 * dt + h01 * b.Value + h11 * m1 * dt;

            // Limitar al rango típico de automatización [0..1] / [-1..1]
            if (double.IsNaN(value) || double.IsInfinity(value))
                return defaultValue;

            if (points == null || points.Count == 0)
                return defaultValue;

            // Como no sabemos el rango concreto, limitamos a [-2, 2] y luego a [min, max] de los dos puntos
            value = Math.Max(-2.0, Math.Min(2.0, value));
            double minAB = Math.Min(a.Value, b.Value) - 0.5;
            double maxAB = Math.Max(a.Value, b.Value) + 0.5;
            value = Math.Max(minAB, Math.Min(maxAB, value));

            return (float)value;
        }

        /// <summary>
        /// Agrega una pista al mezclador asegurando que coincida el WaveFormat del proyecto
        /// (frecuencia de muestreo y número de canales). Si es necesario, realiza
        /// conversión de frecuencia (resampling) y de mono/estéreo.
        /// </summary>
        private void AddTrackToMixer(AudioTrack track, bool isLiveInput = false)
        {
            if (_mixer == null || track == null) return;

            ISampleProvider input = track.FinalProvider;
            WaveFormat targetFormat = _mixer.WaveFormat;
            WaveFormat currentFormat = input.WaveFormat;

            // Ajustar frecuencia de muestreo si es necesario
            if (currentFormat.SampleRate != targetFormat.SampleRate)
            {
                input = new WdlResamplingSampleProvider(input, targetFormat.SampleRate);
                currentFormat = input.WaveFormat;
            }

            // Ajustar número de canales si es necesario (solo mono <-> estéreo)
            if (currentFormat.Channels != targetFormat.Channels)
            {
                if (currentFormat.Channels == 1 && targetFormat.Channels == 2)
                {
                    input = new MonoToStereoSampleProvider(input);
                }
                else if (currentFormat.Channels == 2 && targetFormat.Channels == 1)
                {
                    var stereoToMono = new StereoToMonoSampleProvider(input)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    };
                    input = stereoToMono;
                }
                else
                {
                    throw new NotSupportedException("Solo se soportan conversiones entre mono y estéreo.");
                }
            }

            // Verificación final de seguridad
            if (input.WaveFormat.SampleRate != targetFormat.SampleRate ||
                input.WaveFormat.Channels != targetFormat.Channels)
            {
                throw new InvalidOperationException("No se pudo adaptar el formato de la pista al formato del proyecto.");
            }

            // Conectar directamente la pista al mixer.
            // (Si quieres volver a usar el control de transporte fino,
            //  se puede reactivar TransportAwareSampleProvider aquí.)
            track.MixerInput = input;
            _mixer.AddMixerInput(input);
        }

        public void LoadFile(string path)
        {
            _currentFilePath = path; // Actualizar ruta actual
            StopAndDisposeAll(true);

            var mainTrack = new AudioTrack(path, null, this); // La primera pista define el formato del proyecto
            _tracks.Add(mainTrack);

            // Inicializar Mezclador
            _mixer = new MixingSampleProvider(mainTrack.FinalProvider.WaveFormat); // La primera pista define el formato del proyecto
            _mixer.ReadFully = true; // Mantener el mixer activo incluso si las pistas terminan (evita que se eliminen)
            AddTrackToMixer(mainTrack); // Agregar la pista principal procesada asegurando formato

            InitializeOutputChain(_mixer.WaveFormat);
        }

        private void InitializeOutputChain(WaveFormat format)
        {
            _loopingProvider = new LoopingSampleProvider(_mixer, this);
            _meteringProvider = new MeteringSampleProvider(_loopingProvider);
            _meteringProvider.StreamVolume += (s, e) => {
                try
                {
                    float left = e.MaxSampleValues.Length > 0 ? e.MaxSampleValues[0] : 0;
                    float right = e.MaxSampleValues.Length > 1 ? e.MaxSampleValues[1] : left;
                    MeteringUpdate?.Invoke(left * _masterVolume, right * _masterVolume);
                }
                catch
                {
                    // Nunca dejar que errores de UI detengan el hilo de audio
                }
            };
            _meteringProvider.SamplesPerNotification = format.SampleRate / 25;

            _metronome = new MetronomeSampleProvider(format);
            _metronome.Bpm = Bpm;
            _metronome.Enabled = MetronomeEnabled;

            _outputMixer = new MixingSampleProvider(format);
            _outputMixer.AddMixerInput(_meteringProvider);
            _outputMixer.AddMixerInput(_metronome);

            MasterEqualizer = new Equalizer(_outputMixer, new float[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 });
            MasterLimiter = new CompressorEffect(MasterEqualizer);
            MasterLimiter.UpdateParameters(-0.2f, 20.0f, 1.0f, 50.0f);

            _fftProvider = new FftSampleProvider(MasterLimiter);
            _fftProvider.FftCalculated += (data) =>
            {
                try
                {
                    FftCalculated?.Invoke(data);
                }
                catch
                {
                    // Proteger el hilo de audio frente a errores del analizador
                }
            };

            if (!IsAsio && _outputDevice == null)
            {
                _outputDevice = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };
            }

            if (_outputDevice != null)
            {
                try { _outputDevice.Volume = _masterVolume; } catch { }
                InitOutputDevice();
                _outputDevice.PlaybackStopped += (s, e) =>
                {
                    try
                    {
                        PlaybackStopped?.Invoke();
                    }
                    catch
                    {
                        // No propagar excepciones a NAudio
                    }
                };
            }
        }

        private void StopAndDisposeAll(bool keepRecorder = false)
        {
            if (IsRecording)
            {
                IsRecording = false;
                _recordingWriter?.Dispose();
                _recordingWriter = null;
            }
            Stop();
            _outputDevice?.Dispose();
            _outputDevice = null;
            if (!keepRecorder) _recorder?.Dispose(); // Asegurar que el grabador se libere
            foreach (var track in _tracks)
            {
                track.Dispose();
            }
            _tracks.Clear();
            _mixer = null;
        }

        public void AddTrack(string path)
        {
            RegisterUndoSnapshot();
            if (_mixer == null) throw new InvalidOperationException("Debe cargar un archivo principal primero.");
            var newTrack = new AudioTrack(path, _mixer.WaveFormat, this);
            _tracks.Add(newTrack);
            AddTrackToMixer(newTrack);
            UpdateMixerVolumes();
        }

        public void AddInstrumentTrack()
        {
            RegisterUndoSnapshot();
            // Si aún no hay proyecto inicializado, creamos uno vacío estéreo
            if (_mixer == null)
            {
                InitializeEmptyEngine(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            }

            // Crear un sintetizador básico ajustado al formato del proyecto
            var format = _mixer!.WaveFormat;
            var synth = new Grabadora.BasicSynthesizer(format.SampleRate, format.Channels);

            // Crear pista de instrumento virtual y agregarla al mezclador
            var newTrack = new AudioTrack(synth, format, this);
            _tracks.Add(newTrack);
            AddTrackToMixer(newTrack);
            UpdateMixerVolumes();
        }

        public void MoveTrack(int trackIndex, int direction)
        {
            RegisterUndoSnapshot();
            int newIndex = trackIndex + direction;
            if (newIndex < 0 || newIndex >= _tracks.Count) return;

            var track = _tracks[trackIndex];
            _tracks.RemoveAt(trackIndex);
            _tracks.Insert(newIndex, track);
        }

        public void DuplicateTrack(int trackIndex)
        {
            RegisterUndoSnapshot();
            var source = GetTrack(trackIndex);
            if (source == null || source.Reader == null) return;

            // Crear nueva pista y copiar todo
            var dest = new AudioTrack(source.Reader.FileName, _mixer.WaveFormat, this);
            dest.Name = source.Name + " (Copia)";
            CopyTrackSettings(source, dest);
            
            _tracks.Add(dest);
            if (_mixer != null) AddTrackToMixer(dest);
            
            UpdateMixerVolumes();
        }

        public void RemoveTrack(int trackIndex)
        {
            RegisterUndoSnapshot();
            if (trackIndex < 0 || trackIndex >= _tracks.Count) return;

            var track = _tracks[trackIndex];
            
            // Remover del mezclador si está activo
            if (_mixer != null)
            {
                _mixer.RemoveMixerInput(track.MixerInput);
            }

            track.Dispose();
            _tracks.RemoveAt(trackIndex);

            // Si la pista eliminada era la pista de entrada/monitorización,
            // limpiar la referencia interna para permitir crear otra pista de grabación.
            if (ReferenceEquals(track, _currentRecordingTrack))
            {
                _currentRecordingTrack = null;
                _isMonitoringInput = false;
            }

            UpdateMixerVolumes();
        }

        

        private void StartMidiPlayback()
        {
            if (_midiTimer == null)
            {
                _midiTimer = new System.Timers.Timer(10);
                _midiTimer.AutoReset = true;
                _midiTimer.Elapsed += MidiTimerElapsed;
            }

            _midiWallClockInitialized = false;
            _midiTimer.Start();
        }

        private void StopMidiPlayback()
        {
            if (_midiTimer == null) return;
            _midiTimer.Stop();

            // Apagar todas las notas que sigan sonando
            lock (_midiLock)
            {
                foreach (var kvp in _activeMidiNotes)
                {
                    var track = kvp.Key;
                    if (track.Synthesizer == null) continue;

                    foreach (var note in kvp.Value)
                    {
                        track.Synthesizer.NoteOff(note);
                    }
                }

                _activeMidiNotes.Clear();
            }
        }

        private void MidiTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_outputDevice == null || _outputDevice.PlaybackState != PlaybackState.Playing)
            {
                StopMidiPlayback();
                return;
            }

            if (Bpm <= 0) return;

            double secondsPerBeat = 60.0 / Bpm;

            double nowSeconds;

            // Si hay al menos una pista basada en archivo, usamos CurrentTime del proyecto
            if (_tracks.Any(t => t.Reader != null))
            {
                nowSeconds = CurrentTime.TotalSeconds;
                _midiWallClockInitialized = false;
            }
            else
            {
                // Proyecto solo-MIDI: usar tiempo de reloj desde que comenzó la reproducción
                if (!_midiWallClockInitialized)
                {
                    _midiWallClockStart = DateTime.UtcNow;
                    _midiWallClockInitialized = true;
                }
                nowSeconds = (DateTime.UtcNow - _midiWallClockStart).TotalSeconds;
            }

            lock (_midiLock)
            {
                foreach (var track in _tracks)
                {
                    if (track.Synthesizer == null || track.MidiNotes == null || track.MidiNotes.Count == 0)
                        continue;

                    if (!_activeMidiNotes.TryGetValue(track, out var activeSet))
                    {
                        activeSet = new HashSet<int>();
                        _activeMidiNotes[track] = activeSet;
                    }

                    var shouldBeActive = new HashSet<int>();

                    foreach (var note in track.MidiNotes)
                    {
                        double noteStartSec = note.StartBeat * secondsPerBeat;
                        double noteEndSec = noteStartSec + note.DurationBeats * secondsPerBeat;

                        if (nowSeconds >= noteStartSec && nowSeconds < noteEndSec)
                        {
                            shouldBeActive.Add(note.NoteNumber);

                            if (!activeSet.Contains(note.NoteNumber))
                            {
                                track.Synthesizer.NoteOn(note.NoteNumber, note.Velocity / 127.0f);
                            }
                        }
                    }

                    // Notas que ya no deberían sonar
                    foreach (var noteNumber in activeSet.ToArray())
                    {
                        if (!shouldBeActive.Contains(noteNumber))
                        {
                            track.Synthesizer.NoteOff(noteNumber);
                            activeSet.Remove(noteNumber);
                        }
                    }

                    // Registrar nuevas notas activas
                    foreach (var noteNumber in shouldBeActive)
                    {
                        activeSet.Add(noteNumber);
                    }
                }
            }
        }

        // Configura el WaveIn (_recorder) y engancha los eventos comunes de entrada de micrófono.
        // No inicia la grabación; solo deja preparado el dispositivo.
        private void EnsureWaveInInitialized()
        {
            if (IsAsio) return; // En ASIO la entrada se gestiona por AsioOut

            if (_recorder != null) return;

            int sampleRate = _mixer != null ? _mixer.WaveFormat.SampleRate : 44100;

            // Si aún no hay motor inicializado, crear uno vacío para poder monitorizar/grabar
            if (_mixer == null)
            {
                InitializeEmptyEngine(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
            }

            _recorder = new WaveInEvent();
            _recorder.DeviceNumber = InputDeviceNumber;
            _recorder.WaveFormat = new WaveFormat(sampleRate, 16, 1);

            _recorder.DataAvailable += (s, e) =>
            {
                _currentRecordingTrack?.MonitorProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                if (IsRecording && _recordingWriter != null)
                {
                    _recordingWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _recorder.RecordingStopped += (s, e) =>
            {
                _recorder?.Dispose();
                _recorder = null;
            };
        }

        public void CreateInputTrack()
        {
            if (_currentRecordingTrack != null) return; // Ya existe una pista de entrada

            if (IsAsio)
            {
                // Modo ASIO: Usamos formato Float y no iniciamos WaveInEvent
                // Usar la frecuencia del proyecto si está disponible (NAudio no expone DriverSampleRate en todas las versiones)
                int asioSampleRate = _mixer?.WaveFormat.SampleRate ?? 44100;
                // ASIO suele trabajar con Floats, creamos un formato compatible
                var format = WaveFormat.CreateIeeeFloatWaveFormat(asioSampleRate, 1); // Mono
                
                // Si aún no hay motor inicializado (no hay pistas cargadas), crear uno vacío
                if (_mixer == null)
                {
                    InitializeEmptyEngine(WaveFormat.CreateIeeeFloatWaveFormat(asioSampleRate, 2));
                }

                _currentRecordingTrack = new AudioTrack(format, _mixer.WaveFormat, this);
                _tracks.Add(_currentRecordingTrack);
                AddTrackToMixer(_currentRecordingTrack, true);
                
                // En ASIO, la captura comienza cuando el driver reproduce (Play)
                return;
            }

            // Asegurar WaveIn configurado (modo Windows)
            EnsureWaveInInitialized();

            _currentRecordingTrack = new AudioTrack(_recorder.WaveFormat, _mixer.WaveFormat, this);
            _tracks.Add(_currentRecordingTrack);
            AddTrackToMixer(_currentRecordingTrack, true);
        }

        public void StartRecording()
        {
            if (_isExporting) return;
            if (IsRecording) return;

            // Si venimos de una monitorización en la que se silenciaron
            // temporalmente otras pistas, restaurar su estado antes de grabar
            if (_monitoringMuteSnapshot != null)
            {
                foreach (var kvp in _monitoringMuteSnapshot)
                {
                    if (_tracks.Contains(kvp.Key))
                    {
                        kvp.Key.IsMuted = kvp.Value;
                    }
                }
                _monitoringMuteSnapshot = null;
                UpdateMixerVolumes();
            }

            // Si no hay pista de entrada, crearla (auto-armado)
            if (_currentRecordingTrack == null) CreateInputTrack();

            // Caso especial: venimos de modo ASIO y ya existe pista de entrada,
            // pero el WaveIn (_recorder) es null porque antes usábamos ASIOOut.
            // En modo Windows debemos asegurarnos de tener un WaveIn configurado.
            if (!IsAsio)
            {
                EnsureWaveInInitialized();
            }

            // Si hay otras pistas, reproducir para acompañar (Overdub)
            // _tracks.Count > 1 porque una es la pista de entrada
            // En ASIO, SIEMPRE debemos reproducir para que el driver procese audio (input/output van juntos)
            if (IsAsio || _tracks.Count > 1) 
            {
                if (_outputDevice.PlaybackState != PlaybackState.Playing) Play();
            }
            
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rec_{Guid.NewGuid()}.wav");

            // Momento del proyecto donde comienza la grabación (para alinear con la línea roja)
            _recordStartTime = CurrentTime;

            // Usar el formato correcto según el modo
            var format = IsAsio ? _currentRecordingTrack.MonitorProvider.WaveFormat : _recorder.WaveFormat;
            _recordingWriter = new WaveFileWriter(tempFile, format);

            // Si la grabación comienza más tarde que 0, rellenar con silencio inicial
            if (_recordStartTime > TimeSpan.Zero)
            {
                long bytesToPad = (long)(format.AverageBytesPerSecond * _recordStartTime.TotalSeconds);
                // Alinear al tamaño de bloque de audio
                bytesToPad -= bytesToPad % format.BlockAlign;

                if (bytesToPad > 0)
                {
                    byte[] silence = new byte[format.BlockAlign * 1024];
                    long remaining = bytesToPad;
                    while (remaining > 0)
                    {
                        int toWrite = (int)Math.Min(silence.Length, remaining);
                        _recordingWriter.Write(silence, 0, toWrite);
                        remaining -= toWrite;
                    }
                }
            }
            
            IsRecording = true;
            
            // Asegurar que la entrada está activa. Si ya está monitorizando, no reiniciamos el WaveIn.
            if (!IsAsio && !_isMonitoringInput)
            {
                _recorder.StartRecording();
            }
            _isMonitoringInput = true; // Durante la grabación también se monitoriza la entrada

            // Asegurar que la salida está reproduciendo para oír micro y pistas
            if (_outputDevice != null && _outputDevice.PlaybackState != PlaybackState.Playing)
            {
                _outputDevice.Play();
                Play();
            }
            else
            {
                IsTransportPlaying = true;
            }
            
            RecordingStarted?.Invoke();
        }

        public void StopRecording()
        {
            if (!IsRecording) 
            {
                // Si solo estamos monitorizando (sin grabar), detenemos el monitor y quitamos la pista
                if (_currentRecordingTrack != null)
                {
                    if (!IsAsio) _recorder?.StopRecording();
                    int idx = _tracks.IndexOf(_currentRecordingTrack);
                    if (idx != -1) RemoveTrack(idx);
                    _currentRecordingTrack = null;
                }
                _isMonitoringInput = false;
                return;
            }
            
            Stop(); // Detener reproducción también
            IsRecording = false;
            
            string file = _recordingWriter?.Filename;
            _recordingWriter?.Dispose();
            _recordingWriter = null;

            if (!IsAsio) _recorder?.StopRecording();

            if (file != null) RecordingFinished?.Invoke(file);
            _isMonitoringInput = false;
        }

        public void FinalizeRecording(string filePath)
        {
            RegisterUndoSnapshot();
            if (_currentRecordingTrack == null) return;

            int index = _tracks.IndexOf(_currentRecordingTrack);
            if (index == -1) return;

            // 1. Remover la pista de monitorización del mezclador
            //    Importante: se añadió al mixer usando MixerInput (TransportAwareSampleProvider),
            //    no FinalProvider. Debemos eliminar exactamente el mismo proveedor.
            if (_mixer != null && _currentRecordingTrack.MixerInput != null)
            {
                _mixer.RemoveMixerInput(_currentRecordingTrack.MixerInput);
            }
            
            // 2. Crear la nueva pista basada en el archivo grabado
            var newTrack = new AudioTrack(filePath, _mixer?.WaveFormat ?? _currentRecordingTrack.MonitorProvider.WaveFormat, this);
            
            // 3. Copiar configuraciones de efectos de la pista de grabación a la nueva
            CopyTrackSettings(_currentRecordingTrack, newTrack, true);

            // 4. Reemplazar en la lista
            _currentRecordingTrack.Dispose();
            _tracks[index] = newTrack;
            _currentRecordingTrack = null;

            // 5. Añadir la nueva pista al mezclador
            if (_mixer != null) 
            {
                AddTrackToMixer(newTrack);
            }
            else
            {
                // Si es la primera pista grabada (no había archivo previo), inicializar el motor de audio
                _mixer = new MixingSampleProvider(newTrack.FinalProvider.WaveFormat);
                _mixer.ReadFully = true; // Mantener activo
                AddTrackToMixer(newTrack);
                InitializeOutputChain(_mixer.WaveFormat);
            }
            
            UpdateMixerVolumes();
        }

        private void CopyTrackSettings(AudioTrack source, AudioTrack dest, bool isRecordingFinalization = false)
        {
            dest.Name = isRecordingFinalization ? "Grabación" : source.Name;
            dest.UserVolume = source.UserVolume;
            dest.IsMuted = source.IsMuted;
            dest.IsSolo = source.IsSolo;
            dest.PanningProvider.Pan = source.PanningProvider.Pan;

            // Copiar Efectos
            // EQ
            float[] eqGains = source.Equalizer.GetGains();
            for (int i = 0; i < eqGains.Length; i++) dest.Equalizer.UpdateBand(i, eqGains[i]);

            // Tempo
            dest.TimeStretchEffect.Tempo = source.TimeStretchEffect.Tempo;

            // Delay
            dest.DelayEffect.SetDelay(source.DelayEffect.CurrentDelay);
            dest.DelayEffect.Feedback = source.DelayEffect.Feedback;
            dest.DelayEffect.WetMix = source.DelayEffect.WetMix;

            // Compressor
            dest.CompressorEffect.UpdateParameters(
                source.CompressorEffect.GetThreshold(),
                source.CompressorEffect.GetRatio(),
                source.CompressorEffect.GetAttack(),
                source.CompressorEffect.GetRelease()
            );

            // Reverb
            dest.ReverbEffect.Mix = source.ReverbEffect.Mix;
            dest.ReverbEffect.RoomSize = source.ReverbEffect.RoomSize;

            // Filter
            dest.FilterEffect.Configure(source.FilterEffect.CurrentType, source.FilterEffect.CurrentCutoff);

            // Distortion
            dest.DistortionEffect.Drive = source.DistortionEffect.Drive;
            dest.DistortionEffect.Mix = source.DistortionEffect.Mix;

            // Chorus
            dest.ChorusEffect.Mix = source.ChorusEffect.Mix;
            dest.ChorusEffect.Depth = source.ChorusEffect.Depth;
            dest.ChorusEffect.Rate = source.ChorusEffect.Rate;
        }

        public AudioTrack GetTrack(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _tracks.Count) return null;
            return _tracks[trackIndex];
        }

        public bool IsTrackRecording(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            return track != null && (track.MonitorProvider != null || track.RecordingBuffer != null);
        }

        // Métodos para previsualizar notas (usados por el Piano Roll)
        public void PreviewNoteOn(int trackIndex, int noteNumber, int velocity)
        {
            var track = GetTrack(trackIndex);
            if (track != null && track.Synthesizer != null)
            {
                track.Synthesizer.NoteOn(noteNumber, velocity / 127.0f);
            }
        }

        public void PreviewNoteOff(int trackIndex, int noteNumber)
        {
            var track = GetTrack(trackIndex);
            if (track != null && track.Synthesizer != null)
            {
                track.Synthesizer.NoteOff(noteNumber);
            }
        }

        /// <summary>
        /// Activa la monitorización de la entrada sin grabar a disco.
        /// </summary>
        public void StartMonitoring()
        {
            if (_isExporting) return;
            if (IsRecording) return; // Durante la grabación ya se monitoriza
            if (_isMonitoringInput) return;

            // Asegurar que existe una pista de entrada
            if (_currentRecordingTrack == null)
            {
                CreateInputTrack();
            }

            if (!IsAsio)
            {
                // Asegurar que el WaveIn está configurado y enlazado a la pista actual
                EnsureWaveInInitialized();
                _recorder.StartRecording();
            }

            _isMonitoringInput = true;
            
            // En modo ASIO, la entrada y salida van ligadas al driver, por lo que
            // necesitamos que el dispositivo de salida esté reproduciendo. En modo
            // Windows Audio dejamos que el usuario decida cuándo pulsar Play para
            // no disparar la reproducción de las pistas grabadas al activar el mic.
            if (IsAsio && _outputDevice != null && _outputDevice.PlaybackState != PlaybackState.Playing)
            {
                _outputDevice.Play();
            }
        }

        /// <summary>
        /// Desactiva la monitorización de la entrada sin afectar al modo de grabación.
        /// </summary>
        public void StopMonitoring()
        {
            if (_isExporting) return;
            if (IsRecording) return; // No detener entrada si se está grabando
            if (!_isMonitoringInput) return;

            if (!IsAsio)
            {
                _recorder?.StopRecording();
            }
            // En ASIO seguimos dejando la pista armada; simplemente no habrá salida si está muteada.

            _isMonitoringInput = false;

            // Restaurar estado de muteo de las pistas tras la monitorización
            if (_monitoringMuteSnapshot != null)
            {
                foreach (var kvp in _monitoringMuteSnapshot)
                {
                    if (_tracks.Contains(kvp.Key))
                    {
                        kvp.Key.IsMuted = kvp.Value;
                    }
                }
                _monitoringMuteSnapshot = null;
                UpdateMixerVolumes();
            }
        }

        public void SetTrackName(int trackIndex, string name)
        {
            var track = GetTrack(trackIndex);
            if (track != null) track.Name = name;
        }

        public string GetTrackName(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            return track?.Name ?? $"Pista {trackIndex + 1}";
        }

        public void GetTrackLevels(int trackIndex, out float left, out float right)
        {
            left = 0; right = 0;
            var track = GetTrack(trackIndex);
            if (track != null)
            {
                left = track.LastPeakLeft;
                right = track.LastPeakRight;
            }
        }

        /// <summary>
        /// Devuelve el índice de la primera pista de instrumento (MIDI) encontrada.
        /// Si no hay ninguna, devuelve -1.
        /// </summary>
        public int FindFirstInstrumentTrackIndex()
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (_tracks[i].Synthesizer != null)
                    return i;
            }
            return -1;
        }

        public void SetEqualizerBand(int trackIndex, int band, float gain)
        {
            GetTrack(trackIndex)?.Equalizer.UpdateBand(band, gain);
        }

        public void SetMasterEqualizerBand(int band, float gain)
        {
            MasterEqualizer?.UpdateBand(band, gain);
        }

        public float[] GetMasterEqGains()
        {
            return MasterEqualizer?.GetGains();
        }

        public void SetTrackPan(int trackIndex, float pan)
        {
            var track = GetTrack(trackIndex);
            if (track != null) track.PanningProvider.Pan = pan;
        }

        public float GetTrackPan(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            return track != null ? track.PanningProvider.Pan : 0.0f;
        }

        public void SetDelayParameters(int trackIndex, int delayMilliseconds, float feedback, float wetMix)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            track.DelayEffect.SetDelay(delayMilliseconds);
            track.DelayEffect.Feedback = feedback;
            track.DelayEffect.WetMix = wetMix;
        }

        public void SetCompressorParameters(int trackIndex, float threshold, float ratio, float attack, float release)
        {
            GetTrack(trackIndex)?.CompressorEffect.UpdateParameters(threshold, ratio, attack, release);
        }

        public void SetTempo(int trackIndex, float tempo)
        {
            var track = GetTrack(trackIndex);
            if (track != null) track.TimeStretchEffect.Tempo = tempo;
        }

        public void SetFilterParameters(int trackIndex, int filterTypeIndex, float cutoff)
        {
            GetTrack(trackIndex)?.FilterEffect.Configure((FilterEffect.FilterType)filterTypeIndex, cutoff);
        }

        public void SetDistortionParameters(int trackIndex, float drive, float mix)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            track.DistortionEffect.Drive = drive;
            track.DistortionEffect.Mix = mix;
        }

        public void SetChorusParameters(int trackIndex, float mix, float depth, float rate)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            track.ChorusEffect.Mix = mix;
            track.ChorusEffect.Depth = depth;
            track.ChorusEffect.Rate = rate;
        }

        public async Task ApplyDelayToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var delay = track.DelayEffect.CurrentDelay;
            var feedback = track.DelayEffect.Feedback;
            var mix = track.DelayEffect.WetMix;
            if (mix == 0) return;

            await ApplyEffectToSelectionAsync<DelayEffect>(trackIndex, start, end, source => {
                var effect = new DelayEffect(source, 2000);
                effect.SetDelay(delay);
                effect.Feedback = feedback;
                effect.WetMix = mix;
                return effect;
            });
            
            // Resetear efecto para evitar doble procesamiento
            SetDelayParameters(trackIndex, delay, feedback, 0.0f);
        }

        public async Task ApplyReverbToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var mix = track.ReverbEffect.Mix;
            var size = track.ReverbEffect.RoomSize;
            if (mix == 0) return;

            await ApplyEffectToSelectionAsync<ReverbEffect>(trackIndex, start, end, source => {
                var effect = new ReverbEffect(source);
                effect.Mix = mix;
                effect.RoomSize = size;
                return effect;
            });
            
            SetReverbParameters(trackIndex, 0.0f, size);
        }

        public async Task ApplyCompressorToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var threshold = track.CompressorEffect.GetThreshold();
            var ratio = track.CompressorEffect.GetRatio();
            var attack = track.CompressorEffect.GetAttack();
            var release = track.CompressorEffect.GetRelease();

            await ApplyEffectToSelectionAsync<CompressorEffect>(trackIndex, start, end, source => {
                var effect = new CompressorEffect(source);
                effect.UpdateParameters(threshold, ratio, attack, release);
                return effect;
            });
            
            // Resetear compresor (Ratio 1:1 lo hace transparente)
            SetCompressorParameters(trackIndex, 0.0f, 1.0f, attack, release);
        }

        public async Task ApplyFilterToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var type = track.FilterEffect.CurrentType;
            var cutoff = track.FilterEffect.CurrentCutoff;
            if (type == FilterEffect.FilterType.None) return;

            await ApplyEffectToSelectionAsync<FilterEffect>(trackIndex, start, end, source => {
                var effect = new FilterEffect(source);
                effect.Configure(type, cutoff);
                return effect;
            });
            
            SetFilterParameters(trackIndex, (int)FilterEffect.FilterType.None, cutoff);
        }

        public async Task ApplyDistortionToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var drive = track.DistortionEffect.Drive;
            var mix = track.DistortionEffect.Mix;
            if (drive == 0 && mix == 0) return;

            await ApplyEffectToSelectionAsync<DistortionEffect>(trackIndex, start, end, source => {
                var effect = new DistortionEffect(source);
                effect.Drive = drive;
                effect.Mix = mix;
                return effect;
            });
            
            SetDistortionParameters(trackIndex, 0.0f, 0.0f);
        }

        public async Task ApplyChorusToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            var mix = track.ChorusEffect.Mix;
            var depth = track.ChorusEffect.Depth;
            var rate = track.ChorusEffect.Rate;
            if (mix == 0) return;

            await ApplyEffectToSelectionAsync<ChorusEffect>(trackIndex, start, end, source => {
                var effect = new ChorusEffect(source);
                effect.Mix = mix;
                effect.Depth = depth;
                effect.Rate = rate;
                return effect;
            });
            
            SetChorusParameters(trackIndex, 0.0f, depth, rate);
        }

        public async Task ApplyGainToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end, float gainDb)
        {
            float gainLinear = (float)Math.Pow(10, gainDb / 20.0);
            await ApplyEffectToSelectionAsync<GainEffect>(trackIndex, start, end, source => {
                var effect = new GainEffect(source);
                effect.Gain = gainLinear;
                return effect;
            });
        }

        public async Task ApplyNoiseReductionToSelectionAsync(int trackIndex, TimeSpan start, TimeSpan end, float thresholdDb)
        {
            float thresholdLinear = (float)Math.Pow(10, thresholdDb / 20.0);
            await ApplyEffectToSelectionAsync<NoiseGateEffect>(trackIndex, start, end, source => {
                var effect = new NoiseGateEffect(source);
                // Presets para una puerta de ruido estándar
                effect.Threshold = thresholdLinear;
                effect.Attack = 0.005f; // 5 ms: abre rápido para no comer transitorios
                effect.Hold = 0.02f;    // 20 ms: mantiene la puerta abierta para evitar "chattering"
                effect.Release = 0.15f; // 150 ms: cierre suave pero no demasiado largo
                // El parámetro Ratio ya no es necesario en la nueva implementación mejorada.
                return effect;
            });
        }

        /// <summary>
        /// Analiza una selección de audio (que debe ser solo ruido) y guarda su perfil de frecuencias.
        /// </summary>
        public async Task CaptureNoiseProfileAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null) return;

            int sampleRate = track.Reader.WaveFormat.SampleRate;
            int channels = track.Reader.WaveFormat.Channels;
            GetSampleRange(start, end, track.Reader.WaveFormat, track.Samples.Length, out long startIndex, out long endIndex);

            long length = endIndex - startIndex;
            // Necesitamos al menos 1024 FRAMES (no solo muestras) para un análisis FFT estable
            if (length < 1024L * channels) throw new Exception("La selección es demasiado corta para analizar.");

            await Task.Run(() =>
            {
                int fftSize = 1024;
                int m = (int)Math.Log(fftSize, 2.0);
                var fftBuffer = new Complex[fftSize];
                float[] magnitudes = new float[fftSize / 2 + 1];
                int blocksProcessed = 0;

                // Trabajamos en FRAMES (muestras por canal), no en muestras totales intercaladas
                long totalFrames = length / channels;
                long hopFrames = fftSize / 2; // 50% de solapamiento

                // Analizar bloques con 50% overlap
                for (long frameOffset = 0; frameOffset <= totalFrames - fftSize; frameOffset += hopFrames)
                {
                    long baseIndex = startIndex + frameOffset * channels;

                    // Cargar y aplicar ventana
                    for (int j = 0; j < fftSize; j++)
                    {
                        long frameIndex = baseIndex + (long)j * channels;

                        // Promediar canales si es estéreo para obtener perfil general
                        float sample = track.Samples[frameIndex];
                        if (channels == 2)
                        {
                            sample = (sample + track.Samples[frameIndex + 1]) * 0.5f;
                        }

                        double window = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * j / (fftSize - 1)));
                        fftBuffer[j].X = (float)(sample * window);
                        fftBuffer[j].Y = 0;
                    }

                    FastFourierTransform.FFT(true, m, fftBuffer);

                    // Acumular magnitudes
                    for (int j = 0; j < magnitudes.Length; j++)
                    {
                        magnitudes[j] += (float)Math.Sqrt(fftBuffer[j].X * fftBuffer[j].X + fftBuffer[j].Y * fftBuffer[j].Y);
                    }
                    blocksProcessed++;
                }

                // Promediar
                if (blocksProcessed > 0)
                {
                    for (int j = 0; j < magnitudes.Length; j++) magnitudes[j] /= blocksProcessed;
                    _capturedNoiseProfile = magnitudes;
                }
            });
        }

        public async Task ApplySpectralNoiseReductionAsync(int trackIndex, TimeSpan start, TimeSpan end, float strength)
        {
            if (_capturedNoiseProfile == null) throw new InvalidOperationException("Primero debe capturar un perfil de ruido.");

            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            var waveFormat = track.Reader.WaveFormat;
            int channels = waveFormat.Channels;

            GetSampleRange(start, end, waveFormat, track.Samples.Length, out long startIndex, out long endIndex);
            long length = endIndex - startIndex;
            if (length <= 0) return;

            string tempFile = await Task.Run(() =>
            {
                float[] selectionBuffer = new float[length];
                Array.Copy(track.Samples, startIndex, selectionBuffer, 0, length);
                float[] originalSelection = (float[])selectionBuffer.Clone();

                // Optimización: Evitar copia de array usando IgnoreDisposeStream
                var memoryStream = new MemoryStream();
                using (var writer = new WaveFileWriter(new IgnoreDisposeStream(memoryStream), waveFormat))
                {
                    writer.WriteSamples(selectionBuffer, 0, selectionBuffer.Length);
                }

                memoryStream.Position = 0;
                using (var reader = new WaveFileReader(memoryStream))
                {
                    ISampleProvider sourceProvider = reader.ToSampleProvider();
                    var effectProvider = new SpectralSubtractionEffect(sourceProvider, _capturedNoiseProfile, strength);

                    float[] processedBuffer = new float[length];
                    int totalRead = 0;
                    while (totalRead < length)
                    {
                        int toRead = (int)Math.Min(length - totalRead, 4096);
                        int readSamples = effectProvider.Read(processedBuffer, totalRead, toRead);
                        if (readSamples <= 0) break;
                        totalRead += readSamples;
                    }

                    if (totalRead > 0)
                    {
                        // Mezcla húmedo/seco: la fuerza controla cuánto procesado se aplica.
                        // Ahora usamos directamente strength en el rango [0..1] como cantidad de efecto.
                        float wet = Math.Clamp(strength, 0.0f, 1.0f);
                        float dry = 1.0f - wet;

                        for (int i = 0; i < totalRead; i++)
                        {
                            track.Samples[startIndex + i] = originalSelection[i] * dry + processedBuffer[i] * wet;
                        }
                    }
                }

                string tmp = Path.Combine(Path.GetTempPath(), $"fx_apply_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(track.Samples, 0, track.Samples.Length);
                }
                return tmp;
            });

            ReplaceTrack(trackIndex, tempFile);
        }

        /// <summary>
        /// Crea una automatización lineal de volumen entre dos tiempos, en el rango [0..1].
        /// Sustituye los puntos existentes dentro de ese rango.
        /// </summary>
        public void SetVolumeAutomationLinear(int trackIndex, TimeSpan start, TimeSpan end, float startValue, float endValue)
        {
            RegisterUndoSnapshot();
            var track = GetTrack(trackIndex);
            if (track == null) return;

            if (start > end) (start, end) = (end, start);
            if (start == end) return;

            double s = start.TotalSeconds;
            double e = end.TotalSeconds;

            // Limpiar puntos existentes en el rango para esta curva
            track.VolumeAutomation.RemoveAll(p => p.TimeSeconds >= s && p.TimeSeconds <= e);

            track.VolumeAutomation.Add(new AutomationPoint { TimeSeconds = s, Value = startValue });
            track.VolumeAutomation.Add(new AutomationPoint { TimeSeconds = e, Value = endValue });
            track.VolumeAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }

        /// <summary>
        /// Crea una automatización lineal de paneo entre dos tiempos, en el rango [-1..1].
        /// Sustituye los puntos existentes dentro de ese rango.
        /// </summary>
        public void SetPanAutomationLinear(int trackIndex, TimeSpan start, TimeSpan end, float startPan, float endPan)
        {
            RegisterUndoSnapshot();
            var track = GetTrack(trackIndex);
            if (track == null) return;

            if (start > end) (start, end) = (end, start);
            if (start == end) return;

            double s = start.TotalSeconds;
            double e = end.TotalSeconds;

            track.PanAutomation.RemoveAll(p => p.TimeSeconds >= s && p.TimeSeconds <= e);

            track.PanAutomation.Add(new AutomationPoint { TimeSeconds = s, Value = startPan });
            track.PanAutomation.Add(new AutomationPoint { TimeSeconds = e, Value = endPan });
            track.PanAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }

        /// <summary>
        /// Define o actualiza un punto de automatización de volumen en un instante concreto.
        /// Útil para edición puntual con el ratón.
        /// </summary>
        public void SetVolumeAutomationPoint(int trackIndex, TimeSpan time, float value)
        {
            RegisterUndoSnapshot();
            var track = GetTrack(trackIndex);
            if (track == null) return;

            double t = time.TotalSeconds;
            // Eliminar puntos muy cercanos para evitar duplicados
            track.VolumeAutomation.RemoveAll(p => Math.Abs(p.TimeSeconds - t) < 0.01);
            track.VolumeAutomation.Add(new AutomationPoint { TimeSeconds = t, Value = value });
            track.VolumeAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }

        /// <summary>
        /// Define o actualiza un punto de automatización de paneo en un instante concreto.
        /// Valor en rango [-1..1].
        /// </summary>
        public void SetPanAutomationPoint(int trackIndex, TimeSpan time, float pan)
        {
            RegisterUndoSnapshot();
            var track = GetTrack(trackIndex);
            if (track == null) return;

            double t = time.TotalSeconds;
            track.PanAutomation.RemoveAll(p => Math.Abs(p.TimeSeconds - t) < 0.01);
            track.PanAutomation.Add(new AutomationPoint { TimeSeconds = t, Value = pan });
            track.PanAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }

        public void SetReverbParameters(int trackIndex, float mix, float roomSize)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return;
            track.ReverbEffect.Mix = mix;
            track.ReverbEffect.RoomSize = roomSize;
        }

        public object GetEffectParameters(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return null;
            return new {
                EqGains = track.Equalizer.GetGains(),
                Delay = new { Delay = track.DelayEffect.CurrentDelay, Feedback = track.DelayEffect.Feedback, Mix = track.DelayEffect.WetMix },
                Compressor = new { Threshold = track.CompressorEffect.GetThreshold(), Ratio = track.CompressorEffect.GetRatio(), Attack = track.CompressorEffect.GetAttack(), Release = track.CompressorEffect.GetRelease() },
                Filter = new { Type = track.FilterEffect.CurrentType, Cutoff = track.FilterEffect.CurrentCutoff },
                Distortion = new { Drive = track.DistortionEffect.Drive, Mix = track.DistortionEffect.Mix },
                Chorus = new { Mix = track.ChorusEffect.Mix, Depth = track.ChorusEffect.Depth, Rate = track.ChorusEffect.Rate },
                Reverb = new { Mix = track.ReverbEffect.Mix, RoomSize = track.ReverbEffect.RoomSize },
                Tempo = track.TimeStretchEffect.Tempo
            };
        }

        public void SetVolume(float volume)
        {
            _masterVolume = volume;
            if (_outputDevice != null && !IsAsio)
            {
                _outputDevice.Volume = _masterVolume;
            }
        }

        public void SetOutputDevice(int deviceNumber)
        {
            bool isCurrentlyAsio = IsAsio;

            // Si ya estamos en modo Windows y el dispositivo es el mismo, no hacer nada.
            // (Cuando venimos de ASIO siempre forzamos la reconfiguración.)
            if (!isCurrentlyAsio && OutputDeviceNumber == deviceNumber && _outputDevice != null) return;

            // Si estamos cambiando DESDE ASIO a Windows Audio, limpiar pista de entrada
            // y estado de monitorización/grabación asociados al driver ASIO.
            if (isCurrentlyAsio)
            {
                // Esto, si no se está grabando, elimina la pista de entrada actual
                // y desactiva la monitorización. Si se estaba grabando, también detiene
                // la grabación de forma segura.
                StopRecording();
            }

            OutputDeviceNumber = deviceNumber;

            // Detener y liberar el dispositivo actual (sea ASIO o WaveOut)
            bool wasPlaying = _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
            Stop(); // Llama a _outputDevice.Stop()
            _outputDevice?.Dispose();
            _outputDevice = null;

            // Crear y configurar el nuevo dispositivo WaveOut
            var waveOut = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };
            _outputDevice = waveOut;
            try { _outputDevice.Volume = _masterVolume; } catch { }

            // Reinicializar el dispositivo con la cadena de proveedores de audio actual
            ISampleProvider providerToInit = _fftProvider ?? _outputMixer ?? (ISampleProvider)_meteringProvider;
            bool initialized = false;
            if (providerToInit != null)
            {
                _outputDevice.Init(providerToInit);
                initialized = true;
            }

            // Reanudar la reproducción solo si el nuevo dispositivo fue inicializado correctamente
            if (wasPlaying && initialized) _outputDevice.Play();
        }

        public void SetTrackVolume(int trackIndex, float volume)
        {
            var track = GetTrack(trackIndex);
            if (track != null) 
            {
                track.UserVolume = volume;
                UpdateMixerVolumes();
            }
        }

        public float GetTrackVolume(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            return track != null ? track.UserVolume : 1.0f;
        }

        public bool ToggleTrackMute(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return false;
            track.IsMuted = !track.IsMuted;
            UpdateMixerVolumes();
            return track.IsMuted;
        }

        public bool ToggleTrackSolo(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            if (track == null) return false;
            track.IsSolo = !track.IsSolo;
            UpdateMixerVolumes();
            return track.IsSolo;
        }

        public void SetTrackMute(int trackIndex, bool mute)
        {
            var track = GetTrack(trackIndex);
            if (track != null)
            {
                track.IsMuted = mute;
                UpdateMixerVolumes();
            }
        }

        public void SetTrackSolo(int trackIndex, bool solo)
        {
            var track = GetTrack(trackIndex);
            if (track != null)
            {
                track.IsSolo = solo;
                UpdateMixerVolumes();
            }
        }

        private void UpdateMixerVolumes()
        {
            bool anySolo = _tracks.Any(t => t.IsSolo);

            foreach (var track in _tracks)
            {
                float effectiveVolume = track.UserVolume;

                if (anySolo)
                {
                    // Si hay algún Solo activo, solo suenan los que tienen Solo (el Mute se ignora o se suma, aquí Solo manda)
                    if (!track.IsSolo) effectiveVolume = 0;
                }
                else if (track.IsMuted)
                {
                    effectiveVolume = 0;
                }

                track.VolumeProvider.Volume = effectiveVolume;
            }
        }

        

        /// <summary>
        /// Analiza el audio para detectar golpes graves (Kicks).
        /// </summary>
        public async Task<List<TimeSpan>> DetectKicksAsync(int trackIndex)
        {
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return new List<TimeSpan>();
            
            var audioSamples = track.Samples;

            var kicks = new List<TimeSpan>();
            int sampleRate = track.Reader.WaveFormat.SampleRate;
            int channels = track.Reader.WaveFormat.Channels;

            // Configuración del filtro Low Pass (aprox 150 Hz)
            // Fórmula RC discreta: alpha = dt / (RC + dt)
            float dt = 1.0f / sampleRate;
            float rc = 1.0f / (2.0f * (float)Math.PI * 150.0f);
            float alpha = dt / (rc + dt);

            return await Task.Run(() => 
            {
                // Paso 1: Encontrar el pico máximo en frecuencias bajas para normalizar el umbral
                float maxLowPass = 0f;
                float lowPassSample = 0f;
                int step = channels; // Analizar cada frame

                // Hacemos un escaneo rápido primero para determinar el volumen de los graves
                for (int i = 0; i < audioSamples.Length; i += step * 4) // Saltamos muestras para velocidad
                {
                    float input = Math.Abs(audioSamples[i]);
                    lowPassSample = lowPassSample + alpha * (input - lowPassSample);
                    if (lowPassSample > maxLowPass) maxLowPass = lowPassSample;
                }

                // Paso 2: Detectar kicks reales
                // Umbral: 70% del volumen máximo de graves detectado
                float threshold = maxLowPass * 0.7f;
                // Distancia mínima entre kicks: 200ms (para evitar rebotes o doble detección en el mismo golpe)
                int minDistanceSamples = (int)(0.200 * sampleRate * channels); 
                int lastKickIndex = -minDistanceSamples;
                
                lowPassSample = 0f;

                for (int i = 0; i < audioSamples.Length; i += step)
                {
                    // Promedio simple de canales si es estéreo
                    float input = Math.Abs(audioSamples[i]);
                    if (channels == 2 && i + 1 < audioSamples.Length)
                        input = (input + Math.Abs(audioSamples[i + 1])) * 0.5f;

                    // Aplicar filtro
                    lowPassSample = lowPassSample + alpha * (input - lowPassSample);

                    // Comprobar si es un golpe
                    if (lowPassSample > threshold && (i - lastKickIndex) > minDistanceSamples)
                    {
                        double time = (double)i / channels / sampleRate;
                        kicks.Add(TimeSpan.FromSeconds(time));
                        lastKickIndex = i;
                    }
                }
                track.KickMarkers = kicks;
                return kicks;
            });
        }

        /// <summary>
        /// Calcula el BPM estimado basado en los intervalos entre kicks.
        /// </summary>
        public double CalculateBpm(List<TimeSpan> kicks)
        {
            if (kicks == null || kicks.Count < 2) return 0;

            var intervals = new List<double>();
            for (int i = 0; i < kicks.Count - 1; i++)
            {
                double diff = (kicks[i+1] - kicks[i]).TotalSeconds;
                // Filtramos intervalos para un rango razonable de BPM (40 a 220 BPM)
                // 40 BPM = 1.5s, 220 BPM = ~0.27s
                if (diff > 0.27 && diff < 1.5)
                {
                    intervals.Add(diff);
                }
            }

            if (intervals.Count == 0) return 0;
            intervals.Sort();
            // Usamos la mediana para evitar que errores de detección afecten el cálculo
            double median = intervals[intervals.Count / 2];
            return median > 0 ? Math.Round(60.0 / median) : 0;
        }

        private string GetProjectState()
        {
            var data = CreateProjectData();
            return JsonSerializer.Serialize(data);
        }

        private void RestoreProjectState(string json, bool fromLoadProject = false)
        {
            // Cuando se carga un proyecto, no es una operación de "deshacer", por lo que no activamos el flag.
            if (!fromLoadProject) _isRestoring = true;
            try // Usar un único bloque try/finally para el flag
            {
                var data = JsonSerializer.Deserialize<ProjectData>(json);
                if (data == null) return;

                StopAndDisposeAll(true);

                if (data.Tracks.Count == 0)
                {
                    // Restaurando a un estado vacío, no hay nada que cargar.
                    return;
                }

                // Inicializar el motor con el formato de la primera pista basada en archivo que encontremos.
                var firstFileTrack = data.Tracks.FirstOrDefault(t => !t.IsRecordingTrack && !string.IsNullOrEmpty(t.FilePath));
                if (firstFileTrack != null)
                {
                    try
                    {
                        LoadFile(firstFileTrack.FilePath);
                        // LoadFile agrega la pista, así que la quitamos para reconstruir la lista en el orden correcto.
                        _mixer.RemoveAllMixerInputs();
                        _tracks.Clear();
                    }
                    catch (FileNotFoundException)
                    {
                        throw new FileNotFoundException($"No se encontró el archivo de audio principal: {firstFileTrack.FilePath}");
                    }
                }
                else
                {
                    // No hay pistas de archivo, solo de grabación. Inicializar motor con formato por defecto.
                    InitializeEmptyEngine(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
                }

                // Restaurar propiedades generales del proyecto
                ProjectName = string.IsNullOrWhiteSpace(data.ProjectName) ? "Proyecto sin nombre" : data.ProjectName;
                ProjectDuration = data.ProjectDurationSeconds > 0 ? TimeSpan.FromSeconds(data.ProjectDurationSeconds) : TimeSpan.Zero;

                // Reconstruir todas las pistas en el orden guardado
                foreach (var td in data.Tracks)
                {
                    if (td.IsRecordingTrack)
                    {
                        CreateInputTrack(); // Crea y agrega la pista de grabación
                        ApplyTrackSettings(_tracks.Count - 1, td);
                    }
                    else if (td.IsInstrumentTrack)
                    {
                        AddInstrumentTrack();
                        ApplyTrackSettings(_tracks.Count - 1, td);
                    }
                    else if (!string.IsNullOrEmpty(td.FilePath))
                    {
                        AddTrack(td.FilePath); // Agrega pista de archivo
                        ApplyTrackSettings(_tracks.Count - 1, td);
                    }
                }
                
                SetVolume(data.MasterVolume);
                Bpm = data.Bpm;
                IsLooping = data.IsLooping;
                LoopStart = TimeSpan.FromSeconds(data.LoopStartSeconds);
                LoopEnd = TimeSpan.FromSeconds(data.LoopEndSeconds);
                
                if (data.MasterEqGains != null && MasterEqualizer != null)
                {
                    for (int i = 0; i < data.MasterEqGains.Length; i++)
                        MasterEqualizer.UpdateBand(i, data.MasterEqGains[i]);
                }
                
                if (data.MasterLimiter != null && MasterLimiter != null)
                {
                    MasterLimiter.UpdateParameters(data.MasterLimiter.Threshold, data.MasterLimiter.Ratio, data.MasterLimiter.Attack, data.MasterLimiter.Release);
                }
                
                UpdateMixerVolumes();
            }
            finally
            {
                if (!fromLoadProject) _isRestoring = false;
            }
        }

        /// <summary>
        /// Inicializa los componentes básicos del motor (mixer, output) con un formato específico, sin cargar archivos.
        /// </summary>
        private void InitializeEmptyEngine(WaveFormat format)
        {
            _mixer = new MixingSampleProvider(format);
            _mixer.ReadFully = true;
            InitializeOutputChain(format);
            // Si estamos en modo ASIO y aún no hay dispositivo seleccionado, el usuario tendrá que
            // re-seleccionar el driver en la configuración, ya que no podemos adivinar cuál usar aquí.
        }

        private void ApplyTrackSettings(int index, TrackData td)
        {
            SetTrackName(index, td.Name);
            SetTrackVolume(index, td.Volume);
            SetTrackPan(index, td.Pan);
            
            // Establecer estado Mute/Solo directamente
            var track = GetTrack(index);
            if (td.IsRecordingTrack) track.Name = td.Name; // El nombre de la pista de grabación también se restaura
            if (track != null)
            {
                track.IsMuted = td.IsMuted;
                track.IsSolo = td.IsSolo;
                track.FadeInSeconds = td.FadeInSeconds;
                track.FadeOutSeconds = td.FadeOutSeconds;

                track.VolumeAutomation.Clear();
                if (td.VolumeAutomation != null)
                {
                    track.VolumeAutomation.AddRange(td.VolumeAutomation);
                    track.VolumeAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                }

                track.PanAutomation.Clear();
                if (td.PanAutomation != null)
                {
                    track.PanAutomation.AddRange(td.PanAutomation);
                    track.PanAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                }

                track.MidiNotes.Clear();
                if (td.MidiNotes != null && td.MidiNotes.Length > 0)
                {
                    foreach (var mn in td.MidiNotes)
                    {
                        track.MidiNotes.Add(new MidiNote
                        {
                            NoteNumber = mn.NoteNumber,
                            StartBeat = mn.StartBeat,
                            DurationBeats = mn.DurationBeats,
                            Velocity = mn.Velocity
                        });
                    }
                }

                track.FxAutomation.Clear();
                if (td.FxAutomation != null)
                {
                    foreach (var kvp in td.FxAutomation)
                    {
                        track.FxAutomation[kvp.Key] = new List<AutomationPoint>(kvp.Value);
                    }
                }

                if (td.KickMarkers != null)
                {
                    track.KickMarkers = td.KickMarkers.Select(k => TimeSpan.FromSeconds(k)).ToList();
                }
            }

            if (td.EqGains != null)
                for (int i = 0; i < td.EqGains.Length; i++) SetEqualizerBand(index, i, td.EqGains[i]);

            SetTempo(index, td.Tempo);

            if (td.Delay != null) SetDelayParameters(index, td.Delay.TimeMs, td.Delay.Feedback, td.Delay.Mix);
            if (td.Compressor != null) SetCompressorParameters(index, td.Compressor.Threshold, td.Compressor.Ratio, td.Compressor.Attack, td.Compressor.Release);
            if (td.Reverb != null) SetReverbParameters(index, td.Reverb.Mix, td.Reverb.Size);
            if (td.Filter != null) SetFilterParameters(index, td.Filter.Type, td.Filter.Cutoff);
            if (td.Distortion != null) SetDistortionParameters(index, td.Distortion.Drive, td.Distortion.Mix);
            if (td.Chorus != null) SetChorusParameters(index, td.Chorus.Mix, td.Chorus.Depth, td.Chorus.Rate);
        }

        /// <summary>
        /// Normaliza el volumen de la pista para que el pico máximo alcance 0dB (1.0).
        /// </summary>
        public async Task NormalizeTrackAsync(int trackIndex)
        {
            RegisterUndoSnapshot();
            Stop(); // Detener reproducción antes de editar
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            // 1. Encontrar el pico máximo
            float maxPeak = 0f;
            for (int i = 0; i < track.Samples.Length; i++)
            {
                float abs = Math.Abs(track.Samples[i]);
                if (abs > maxPeak) maxPeak = abs;
            }

            // Si ya está normalizado o es silencio, no hacer nada
            if (maxPeak < 0.001f || maxPeak >= 1.0f) return;

            string tempFile = await Task.Run(() => 
            {
                // 2. Calcular ganancia
                float gain = 1.0f / maxPeak;

                // 3. Aplicar ganancia a los samples en memoria
                for (int i = 0; i < track.Samples.Length; i++)
                {
                    track.Samples[i] *= gain;
                }

                // 4. Guardar a archivo temporal
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"norm_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, track.Reader.WaveFormat))
                {
                    writer.WriteSamples(track.Samples, 0, track.Samples.Length);
                }
                return tmp;
            });

            // 5. Reemplazar la pista
            ReplaceTrack(trackIndex, tempFile);
        }

        /// <summary>
        /// Desplaza toda la pista en el tiempo.
        /// - Desplazamiento positivo: añade silencio al principio (la pista suena más tarde)
        ///   sin recortar el contenido de audio.
        /// - Desplazamiento negativo: mueve el audio hacia la izquierda; cualquier parte
        ///   que quedaría antes del tiempo 0 del proyecto se descarta (no puede sonar
        ///   antes del inicio del proyecto).
        /// </summary>
        public async Task MoveTrackInTimeAsync(int trackIndex, TimeSpan offset)
        {
            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            double offsetSeconds = offset.TotalSeconds;
            if (Math.Abs(offsetSeconds) < 1e-6) return;

            var waveFormat = track.Reader.WaveFormat;
            int channels = waveFormat.Channels;
            int sampleRate = waveFormat.SampleRate;

            long totalSamples = track.Samples.Length;

            string tempFile = await Task.Run(() =>
            {
                float[] newSamples;

                if (offsetSeconds > 0)
                {
                    // Mover hacia la derecha: añadimos silencio al principio y
                    // mantenemos todo el audio original (no recortamos el final).
                    long shiftSamples = (long)Math.Round(offsetSeconds * sampleRate) * channels;
                    if (shiftSamples <= 0)
                    {
                        // Desplazamiento demasiado pequeño para afectar a muestras
                        return null;
                    }

                    long newTotalSamples = totalSamples + shiftSamples;
                    newSamples = new float[newTotalSamples]; // inicializado a 0 (silencio)
                    Array.Copy(track.Samples, 0, newSamples, shiftSamples, totalSamples);
                }
                else
                {
                    // Desplazamiento negativo: mover hacia la izquierda. No podemos sonar antes
                    // de t = 0, así que la parte que "saldría" por la izquierda se descarta.
                    double absSeconds = -offsetSeconds;
                    long shiftSamples = (long)Math.Round(absSeconds * sampleRate) * channels;

                    if (shiftSamples <= 0)
                    {
                        return null;
                    }

                    if (shiftSamples >= totalSamples)
                    {
                        // Desplazamiento mayor que la duración de la pista: sería todo silencio.
                        // En vez de crear una pista muda, la dejamos como está.
                        return null;
                    }

                    long copyLen = totalSamples - shiftSamples;
                    newSamples = new float[totalSamples];
                    Array.Copy(track.Samples, shiftSamples, newSamples, 0, copyLen);
                    // El resto del final queda en silencio implícitamente.
                }

                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"move_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(newSamples, 0, newSamples.Length);
                }
                return tmp;
            });

            if (!string.IsNullOrEmpty(tempFile))
            {
                ReplaceTrack(trackIndex, tempFile);
            }
        }

        private void ReplaceTrack(int index, string newPath)
        {
            var oldTrack = _tracks[index];
            
            if (_mixer != null && oldTrack.MixerInput != null) _mixer.RemoveMixerInput(oldTrack.MixerInput);

            var newTrack = new AudioTrack(newPath, _mixer?.WaveFormat ?? oldTrack.Reader.WaveFormat, this);
            
            // Copiar todos los ajustes (Nombre, Volumen, Pan, Efectos)
            CopyTrackSettings(oldTrack, newTrack);
            
            oldTrack.Dispose();
            _tracks[index] = newTrack;
            
            if (_mixer != null) AddTrackToMixer(newTrack);
            UpdateMixerVolumes();
        }

        public async Task ApplyFadeInAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            await ApplyFadeAsync(trackIndex, start, end, true);
        }

        public async Task ApplyFadeOutAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            await ApplyFadeAsync(trackIndex, start, end, false);
        }

        /// <summary>
        /// Convierte un rango de tiempo en índices de muestra sobre un buffer intercalado,
        /// alineando al tamaño de bloque (número de canales) y acotando al tamaño total.
        /// No valida que el resultado tenga longitud positiva.
        /// </summary>
        private static void GetSampleRange(TimeSpan start, TimeSpan end, WaveFormat waveFormat, int totalSamples, out long startIndex, out long endIndex)
        {
            if (start > end)
            {
                (start, end) = (end, start);
            }

            int channels = waveFormat.Channels;
            int sampleRate = waveFormat.SampleRate;

            startIndex = (long)(start.TotalSeconds * sampleRate) * channels;
            endIndex = (long)(end.TotalSeconds * sampleRate) * channels;

            // Alinear a límites de bloque de canales
            if (channels > 0)
            {
                startIndex -= startIndex % channels;
                endIndex -= endIndex % channels;
            }

            // Acotar al rango válido del buffer
            if (startIndex < 0) startIndex = 0;
            if (endIndex > totalSamples) endIndex = totalSamples;
        }

        public async Task ReverseAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            // Asegurar orden correcto y obtener rango de muestras
            var waveFormat = track.Reader.WaveFormat;
            int channels = waveFormat.Channels;
            GetSampleRange(start, end, waveFormat, track.Samples.Length, out long startIndex, out long endIndex);

            long length = endIndex - startIndex;
            if (length <= 0) return;

            // Invertir muestras manteniendo la alineación de canales
            long frames = length / channels;
            
            string tempFile = await Task.Run(() => 
            {
                // Usamos un buffer temporal para la sección a invertir
                float[] buffer = new float[length];
                Array.Copy(track.Samples, startIndex, buffer, 0, length);

                for (long i = 0; i < frames; i++)
                {
                    long destFrame = frames - 1 - i;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        track.Samples[startIndex + (i * channels) + ch] = buffer[(destFrame * channels) + ch];
                    }
                }

                // Guardar en archivo temporal y reemplazar
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reverse_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(track.Samples, 0, track.Samples.Length);
                }
                return tmp;
            });

            ReplaceTrack(trackIndex, tempFile);
        }

        private async Task ApplyFadeAsync(int trackIndex, TimeSpan start, TimeSpan end, bool fadeIn)
        {
            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            // Asegurar orden correcto
            var waveFormat = track.Reader.WaveFormat;
            int channels = waveFormat.Channels;
            GetSampleRange(start, end, waveFormat, track.Samples.Length, out long startIndex, out long endIndex);

            long length = endIndex - startIndex;
            if (length <= 0) return;

            long frames = length / channels;

            string tempFile = await Task.Run(() => 
            {
                for (long i = 0; i < frames; i++)
                {
                    double progress = (double)i / frames;
                    float gain = fadeIn ? (float)progress : (float)(1.0 - progress);

                    for (int ch = 0; ch < channels; ch++)
                    {
                        long idx = startIndex + (i * channels) + ch;
                        if (idx < track.Samples.Length)
                            track.Samples[idx] *= gain;
                    }
                }

                // Guardar en archivo temporal y reemplazar
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fade_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(track.Samples, 0, track.Samples.Length);
                }
                return tmp;
            });

            ReplaceTrack(trackIndex, tempFile);
        }

        /// <summary>
        /// Corta un segmento de audio de una pista y recarga el proyecto.
        /// </summary>
        public async Task CopyAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            var waveFormat = track.Reader.WaveFormat;
            GetSampleRange(start, end, waveFormat, track.Samples.Length, out long startIndex, out long endIndex);
            long length = endIndex - startIndex;
            if (length <= 0) return;

            await Task.Run(() =>
            {
                float[] selectionBuffer = new float[length];
                Array.Copy(track.Samples, startIndex, selectionBuffer, 0, length);
                _clipboardSamples = selectionBuffer;
                _clipboardFormat = waveFormat;
            });
        }

        /// <summary>
        /// Corta un segmento de audio de una pista y recarga el proyecto.
        /// </summary>
        public async Task CutAsync(int trackIndex, TimeSpan start, TimeSpan end)
        {
            RegisterUndoSnapshot();
            Stop();
            if (trackIndex < 0 || trackIndex >= _tracks.Count) return;

            var trackToCut = _tracks[trackIndex];
            var audioSamples = trackToCut.Samples;
            var waveFormat = trackToCut.Reader.WaveFormat;
            GetSampleRange(start, end, waveFormat, audioSamples.Length, out long startSampleIndex, out long endSampleIndex);
            if (startSampleIndex >= endSampleIndex) return;
            
            string tempFile = await Task.Run(() => 
            {
                // Crear nuevo array excluyendo la selección
                long removeCount = endSampleIndex - startSampleIndex;
                long newLength = audioSamples.Length - removeCount;
                float[] newSamples = new float[newLength];

                Array.Copy(audioSamples, 0, newSamples, 0, startSampleIndex);
                Array.Copy(audioSamples, endSampleIndex, newSamples, startSampleIndex, audioSamples.Length - endSampleIndex);

                // Guardar en archivo temporal WAV
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"temp_edit_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(newSamples, 0, newSamples.Length);
                }
                return tmp;
            });

            // Reemplazar la pista actual con la versión cortada, preservando efectos
            ReplaceTrack(trackIndex, tempFile);
        }

        public async Task PasteAsync(int trackIndex, TimeSpan insertTime)
        {
            if (_clipboardSamples == null || _clipboardSamples.Length == 0 || _clipboardFormat == null) return;

            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            var waveFormat = track.Reader.WaveFormat;
            if (waveFormat.SampleRate != _clipboardFormat.SampleRate || waveFormat.Channels != _clipboardFormat.Channels) return;

            int channels = waveFormat.Channels;
            int sampleRate = waveFormat.SampleRate;

            long insertIndex = (long)(insertTime.TotalSeconds * sampleRate) * channels;
            insertIndex -= insertIndex % channels;
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > track.Samples.Length) insertIndex = track.Samples.Length;

            string tempFile = await Task.Run(() =>
            {
                long newLength = track.Samples.Length + _clipboardSamples.Length;
                float[] newSamples = new float[newLength];

                Array.Copy(track.Samples, 0, newSamples, 0, insertIndex);
                Array.Copy(_clipboardSamples, 0, newSamples, insertIndex, _clipboardSamples.Length);
                Array.Copy(track.Samples, insertIndex, newSamples, insertIndex + _clipboardSamples.Length, track.Samples.Length - insertIndex);

                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"paste_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(newSamples, 0, newSamples.Length);
                }
                return tmp;
            });

            ReplaceTrack(trackIndex, tempFile);
        }

        /// <summary>
        /// Aplica un efecto de forma destructiva a una selección de una pista.
        /// </summary>
        private async Task ApplyEffectToSelectionAsync<T>(int trackIndex, TimeSpan start, TimeSpan end, Func<ISampleProvider, T> effectFactory) where T : ISampleProvider
        {
            RegisterUndoSnapshot();
            Stop();
            var track = GetTrack(trackIndex);
            if (track == null || track.Samples == null || track.Reader == null) return;

            if (start == end) return;

            var waveFormat = track.Reader.WaveFormat;
            int channels = waveFormat.Channels;
            GetSampleRange(start, end, waveFormat, track.Samples.Length, out long startIndex, out long endIndex);
            long length = endIndex - startIndex;
            if (length <= 0) return;

            string tempFile = await Task.Run(() =>
            {
                float[] selectionBuffer = new float[length];
                Array.Copy(track.Samples, startIndex, selectionBuffer, 0, length);

                // Optimización: Evitar copia de array usando IgnoreDisposeStream
                var memoryStream = new MemoryStream();
                using (var writer = new WaveFileWriter(new IgnoreDisposeStream(memoryStream), waveFormat))
                {
                    writer.WriteSamples(selectionBuffer, 0, selectionBuffer.Length);
                }
                
                memoryStream.Position = 0;
                using (var reader = new WaveFileReader(memoryStream))
                {
                    ISampleProvider sourceProvider = reader.ToSampleProvider();
                    T effectProvider = effectFactory(sourceProvider);

                    float[] processedBuffer = new float[length];
                    int totalRead = 0;

                    // Leer en bucle hasta llenar o hasta que el efecto no devuelva más muestras.
                    while (totalRead < length)
                    {
                        int toRead = (int)Math.Min(length - totalRead, 4096);
                        int readSamples = effectProvider.Read(processedBuffer, totalRead, toRead);
                        if (readSamples <= 0) break;
                        totalRead += readSamples;
                    }

                    // Solo copiamos la parte realmente procesada. El resto de la selección queda intacto.
                    if (totalRead > 0)
                    {
                        Array.Copy(processedBuffer, 0, track.Samples, startIndex, totalRead);
                    }
                }

                string tmp = Path.Combine(Path.GetTempPath(), $"fx_apply_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(tmp, waveFormat))
                {
                    writer.WriteSamples(track.Samples, 0, track.Samples.Length);
                }
                return tmp;
            });

            ReplaceTrack(trackIndex, tempFile);
        }

        /// <summary>
        /// Exporta el audio actual a un archivo.
        /// </summary>
        public async Task ExportAsync(string path, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (_mixer == null) return;
            if (_isExporting) return;
            
            // Detener y rebobinar antes de exportar para asegurar que se grabe desde el principio
            _isExporting = true;
            Stop();

            await Task.Run(() =>
            {
                // Estimar duración total para la barra de progreso
                double totalSeconds = TotalTime.TotalSeconds;
                if (totalSeconds <= 0) totalSeconds = 1; // Evitar división por cero

                // Construir una cadena de exportación que replique el master (EQ + limitador)
                // pero sin incluir el metrónomo ni el analizador FFT.
                ISampleProvider exportSource = _mixer;

                // Aplicar la ecualización master si existe
                if (MasterEqualizer != null)
                {
                    // Mismas frecuencias que el ecualizador master
                    var eqFrequencies = new float[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
                    var exportEq = new Equalizer(exportSource, eqFrequencies);
                    var masterGains = MasterEqualizer.GetGains();

                    for (int i = 0; i < masterGains.Length && i < eqFrequencies.Length; i++)
                    {
                        exportEq.UpdateBand(i, masterGains[i]);
                    }

                    exportSource = exportEq;
                }

                // Aplicar el limitador master si existe
                if (MasterLimiter != null)
                {
                    var exportLimiter = new CompressorEffect(exportSource);
                    exportLimiter.UpdateParameters(
                        MasterLimiter.GetThreshold(),
                        MasterLimiter.GetRatio(),
                        MasterLimiter.GetAttack(),
                        MasterLimiter.GetRelease());
                    exportSource = exportLimiter;
                }

                // Usamos SampleToWaveProvider16 para estandarizar a PCM 16-bit (compatible con WAV y LAME)
                var waveProvider = new SampleToWaveProvider16(exportSource);
                long totalBytesEstimated = (long)(totalSeconds * waveProvider.WaveFormat.AverageBytesPerSecond);
                long bytesWritten = 0;

                // Buffer de 1 segundo
                byte[] buffer = new byte[waveProvider.WaveFormat.AverageBytesPerSecond];
                int read;

                // Seleccionar escritor según formato
                Stream writer = null;

                try
                {
                    if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        // Usar constructor que acepta ruta (string) y bitrate entero (128)
                        writer = new LameMP3FileWriter(path, waveProvider.WaveFormat, 128);
                    }
                    else
                    {
                        // Usar constructor que acepta ruta (string)
                        writer = new WaveFileWriter(path, waveProvider.WaveFormat);
                    }

                    using (writer)
                    {
                        while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;

                            writer.Write(buffer, 0, read);

                            bytesWritten += read;

                            // Progreso basado en byte estimado, independientemente de que el mixer nunca devuelva 0
                            double percentage = (double)bytesWritten / totalBytesEstimated;
                            if (percentage > 1.0) percentage = 1.0;
                            progress?.Report(percentage);

                            // Como usamos InfiniteSampleProvider, el mixer nunca indica fin de stream.
                            // Cortamos manualmente cuando alcanzamos o superamos la duración estimada.
                            if (bytesWritten >= totalBytesEstimated)
                                break;
                        }
                    }
                }
                finally
                {
                    _isExporting = false;
                }
            });
            
            // Rebobinar después de exportar para dejar el proyecto listo para reproducir
            Stop();
        }

        /// <summary>
        /// Obtiene los datos de la forma de onda para un rango de tiempo específico (Zoom).
        /// </summary>
        public float[] GetWaveformData(int trackIndex, int pointsToRender, double startSeconds, double endSeconds)
        {
            var track = GetTrack(trackIndex);
            if (track == null || pointsToRender <= 0) return Array.Empty<float>();
            if (track.Samples == null) return new float[pointsToRender]; // Si está grabando, devolver silencio visual

            var audioSamples = track.Samples;
            var waveFormat = track.Reader?.WaveFormat ?? track.MonitorProvider.WaveFormat;

            int sampleRate = waveFormat.SampleRate;
            int channels = waveFormat.Channels;

            // CORRECCIÓN 2: Ajustar por el Tempo (Time Stretch)
            // Si el tempo es 0.5 (lento), necesitamos leer menos muestras originales para llenar el mismo tiempo visual.
            // Si el tempo es 2.0 (rápido), necesitamos leer más muestras originales.
            float tempo = track.TimeStretchEffect?.Tempo ?? 1.0f;
            
            // Mapear el tiempo del proyecto al tiempo del archivo de audio original
            double sourceStartSeconds = startSeconds * tempo;
            double sourceEndSeconds = endSeconds * tempo;

            // Esto asegura que la escala visual (segundos por pixel) sea constante para todas las pistas.
            long reqStartSample = (long)(sourceStartSeconds * sampleRate * channels);
            long reqEndSample = (long)(sourceEndSeconds * sampleRate * channels);
            long reqTotalSamples = reqEndSample - reqStartSample;

            if (reqTotalSamples <= 0) return new float[pointsToRender];

            float[] waveform = new float[pointsToRender];
            double samplesPerPoint = (double)reqTotalSamples / pointsToRender;
            if (samplesPerPoint < 1) samplesPerPoint = 1;

            for (int i = 0; i < pointsToRender; i++)
            {
                long currentStart = reqStartSample + (long)(i * samplesPerPoint);
                long currentEnd = reqStartSample + (long)((i + 1) * samplesPerPoint);
                
                // Si el pixel corresponde a un tiempo fuera de la duración de esta pista, es silencio
                if (currentStart >= audioSamples.Length || currentEnd < 0)
                {
                    waveform[i] = 0;
                    continue;
                }

                // Intersección entre lo que pide el pixel y lo que tiene la pista
                long readStart = Math.Max(0, currentStart);
                long readEnd = Math.Min(audioSamples.Length, currentEnd);

                if (readEnd <= readStart)
                {
                    waveform[i] = 0;
                    continue;
                }

                float max = 0;
                // Optimización: saltar muestras si el rango es muy denso, pero en zoom solemos querer detalle
                int step = (int)samplesPerPoint > 100 ? (int)(samplesPerPoint / 50) : 1;

                for (long j = readStart; j < readEnd; j += step)
                {
                    float val = Math.Abs(audioSamples[j]);
                    if (val > max) max = val;
                }
                waveform[i] = max;
            }
            return waveform;
        }

        public List<string> GetInputDevices()
        {
            var list = new List<string>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                list.Add(caps.ProductName);
            }
            return list;
        }

        public List<string> GetOutputDevices()
        {
            var list = new List<string>();
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                list.Add(caps.ProductName);
            }
            return list;
        }

        // --- SOPORTE ASIO ---

        public List<string> GetAsioDrivers()
        {
            if (!AsioOut.isSupported()) return new List<string>();
            return AsioOut.GetDriverNames().ToList();
        }

        public void SetAsioDriver(string driverName)
        {
            // Limpiar cualquier estado de grabación/monitorización previo (WaveIn u otro dispositivo)
            StopRecording();
            Stop();
            _outputDevice?.Dispose();
            _recorder?.Dispose(); // ASIO reemplaza al recorder también
            _recorder = null;

            try
            {
                var asio = new AsioOut(driverName);
                _outputDevice = asio;

                // Si ya existe una cadena de audio, inicializar ahora el dispositivo ASIO
                if (_fftProvider != null)
                {
                    InitOutputDevice();
                }
                // Si no hay mixer aún, se inicializará en LoadFile/CreateInputTrack/InitializeEmptyEngine
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al inicializar ASIO: {ex.Message}");
            }
        }

        /// <summary>
        /// Inicializa el dispositivo de salida actual (WaveOut o ASIO) con la cadena de audio
        /// del proyecto (_fftProvider). En modo ASIO se configura también la captura de entrada
        /// mediante InitRecordAndPlayback y el evento AudioAvailable.
        /// </summary>
        private void InitOutputDevice()
        {
            if (_outputDevice == null || _fftProvider == null) return;

            // Caso ASIO: usar InitRecordAndPlayback para tener input + output
            if (_outputDevice is AsioOut asio)
            {
                int sampleRate = _fftProvider.WaveFormat.SampleRate;
                int outChannels = _fftProvider.WaveFormat.Channels;

                // Usar 2 canales de entrada si están disponibles (estéreo), ya que algunos drivers son inestables con configuraciones impares
                int inChannels = asio.DriverInputChannelCount >= 2 ? 2 : (asio.DriverInputChannelCount >= 1 ? 1 : 0);

                var waveProvider = new SampleToWaveProvider(_fftProvider);

                // Evitar suscripciones duplicadas
                asio.AudioAvailable -= OnAsioAudioAvailable;
                if (inChannels > 0)
                {
                    asio.AudioAvailable += OnAsioAudioAvailable;
                    asio.InitRecordAndPlayback(waveProvider, inChannels, sampleRate);
                }
                else
                {
                    // Solo reproducción, sin entrada
                    asio.Init(waveProvider);
                }
            }
            else
            {
                // WaveOutEvent u otro IWavePlayer estándar
                _outputDevice.Init(_fftProvider);
            }
        }

        private void OnAsioAudioAvailable(object sender, AsioAudioAvailableEventArgs e)
        {
            // Este evento se dispara en el hilo de audio de ASIO
            if (_currentRecordingTrack != null && _currentRecordingTrack.RecordingBuffer != null)
            {
                int samplesPerBuffer = e.SamplesPerBuffer;
                // Canales que espera la pista (ej. 1 para Mono)
                int trackChannels = _currentRecordingTrack.RecordingBuffer.WaveFormat.Channels;
                int totalSamplesToWrite = samplesPerBuffer * trackChannels;

                // 1. Asegurar que el buffer intermedio existe y tiene el tamaño correcto
                if (_asioInputBuffer == null || _asioInputBuffer.Length < totalSamplesToWrite)
                {
                    _asioInputBuffer = new float[totalSamplesToWrite];
                }

                // 2. Leer datos de ASIO y adaptar canales
                // Usamos el overload con buffer reutilizable para evitar la API obsoleta
                int asioChannels = e.InputBuffers.Length;
                int totalAsioSamples = samplesPerBuffer * asioChannels;
                if (_asioInputBuffer == null || _asioInputBuffer.Length < totalAsioSamples)
                {
                    _asioInputBuffer = new float[totalAsioSamples];
                }

                e.GetAsInterleavedSamples(_asioInputBuffer);

                try
                {
                    if (trackChannels == 1 && asioChannels > 1)
                    {
                        // Caso común: Grabar Mono (Mic) desde entrada Estéreo (Interfaz)
                        // Tomamos solo el canal 0 (Izquierdo/Input 1)
                        for (int i = 0; i < samplesPerBuffer; i++)
                        {
                            _asioInputBuffer[i] = _asioInputBuffer[i * asioChannels];
                        }
                    }
                    else
                    {
                        // Si coinciden canales o la pista es estéreo, los datos ya están en _asioInputBuffer;
                        // solo nos aseguramos de no leer más de lo debido al escribir en RecordingBuffer.
                    }
                }
                catch
                {
                    // Si falla la lectura, rellena con silencio
                    Array.Clear(_asioInputBuffer, 0, totalSamplesToWrite);
                }

                // 3. Escribir directamente al buffer circular de la pista (Floats)
                _currentRecordingTrack.RecordingBuffer.Write(_asioInputBuffer, 0, totalSamplesToWrite);

                // 4. Escribir a disco si estamos grabando
                if (IsRecording && _recordingWriter != null)
                {
                    _recordingWriter.WriteSamples(_asioInputBuffer, 0, totalSamplesToWrite);
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopAndDisposeAll();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Proveedor de Paneo personalizado que soporta entrada Mono (Pan) y Estéreo (Balance).
    /// Reemplaza a PanningSampleProvider de NAudio que falla con entradas estéreo.
    /// </summary>
    public class CustomPanningProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _pan;
        private readonly bool _isMonoInput;
        private float[] _monoBuffer;

        public WaveFormat WaveFormat { get; }

        public float Pan
        {
            get => _pan;
            set => _pan = Math.Max(-1.0f, Math.Min(1.0f, value));
        }

        public CustomPanningProvider(ISampleProvider source)
        {
            _source = source;
            _isMonoInput = source.WaveFormat.Channels == 1;
            
            if (_isMonoInput)
            {
                // Si es mono, convertimos a estéreo para poder panear
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
            }
            else
            {
                // Si ya es estéreo (o más), mantenemos el formato
                WaveFormat = source.WaveFormat;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Si es Mono, leemos y expandimos a Stereo aplicando Pan
            if (_isMonoInput)
            {
                int sourceSamplesRequired = count / 2;
                if (_monoBuffer == null || _monoBuffer.Length < sourceSamplesRequired)
                {
                    _monoBuffer = new float[sourceSamplesRequired];
                }

                int samplesRead = _source.Read(_monoBuffer, 0, sourceSamplesRequired);
                
                // Aplicar Ley de Potencia Constante (Constant Power Pan)
                // Mapeamos Pan (-1 a 1) a un ángulo (0 a PI/2)
                double angle = (_pan + 1.0f) * Math.PI / 4.0;
                float leftFactor = (float)Math.Cos(angle);
                float rightFactor = (float)Math.Sin(angle);

                int outIndex = offset;
                for (int i = 0; i < samplesRead; i++)
                {
                    float sample = _monoBuffer[i];
                    // Multiplicador sqrt(2) opcional para compensar la caída de -3dB si se desea mantener 0dB en los extremos
                    // pero la ley estándar de sen/cos es suficiente para mantener la potencia relativa.
                    buffer[outIndex++] = sample * leftFactor; 
                    buffer[outIndex++] = sample * rightFactor;
                }
                return samplesRead * 2;
            }
            else
            {
                // Si es Estéreo, aplicamos Balance
                int samplesRead = _source.Read(buffer, offset, count);
                if (_pan == 0.0f) return samplesRead;

                // Balance estéreo con potencia constante
                double angle = (_pan + 1.0f) * Math.PI / 4.0;
                float leftFactor = (float)Math.Cos(angle);
                float rightFactor = (float)Math.Sin(angle);

                for (int i = 0; i < samplesRead; i += 2)
                {
                    buffer[offset + i] *= leftFactor;
                    buffer[offset + i + 1] *= rightFactor;
                }
                return samplesRead;
            }
        }
    }

    /// <summary>
    /// Aplica automatización de paneo sobre una señal estéreo, usando la curva
    /// PanAutomation de la pista en función del tiempo de reproducción.
    /// Se coloca después de CustomPanningProvider en la cadena.
    /// </summary>
    public class AutomationPanProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly AudioEngine _engine;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public AutomationPanProvider(ISampleProvider source, AudioTrack track, AudioEngine engine)
        {
            _engine = engine;
            _source = source;
            _track = track;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            if (read <= 0 || WaveFormat.Channels < 2)
                return read;

            // PROTECCIÓN: Lock y Try-Catch para evitar caídas por concurrencia
            try
            {
                lock (_track.AutomationLock)
                {
                    if (_track.PanAutomation == null || _track.PanAutomation.Count == 0)
                        return read;

                    int channels = WaveFormat.Channels;
                    int sampleRate = WaveFormat.SampleRate;

                    double current = _track.Reader?.CurrentTime.TotalSeconds ?? _engine.CurrentTime.TotalSeconds;
                    int frames = read / channels;
                    double startTime = current - (double)frames / sampleRate;

                    for (int frame = 0; frame < frames; frame++)
                    {
                        double t = startTime + (double)frame / sampleRate;
                        float pan = _engine.EvaluateAutomation(_track.PanAutomation, t, _track.PanningProvider.Pan);

                        double angle = (pan + 1.0f) * Math.PI / 4.0;
                        float leftFactor = (float)Math.Cos(angle);
                        float rightFactor = (float)Math.Sin(angle);

                        int idx = offset + frame * 2;
                        buffer[idx] *= leftFactor;
                        buffer[idx + 1] *= rightFactor;
                    }
                }
            }
            catch
            {
                // En caso de error de concurrencia, devolvemos audio sin procesar en lugar de crashear
            }

            return read;
        }
    }

    /// <summary>
    /// Aplica automatización de volumen por pista usando la curva VolumeAutomation
    /// y el tiempo de reproducción de la pista, antes del VolumeSampleProvider.
    /// </summary>
    public class AutomationVolumeProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly AudioEngine _engine;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public AutomationVolumeProvider(ISampleProvider source, AudioTrack track, AudioEngine engine)
        {
            _engine = engine;
            _source = source;
            _track = track;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            if (read <= 0)
                return read;

            try
            {
                lock (_track.AutomationLock)
                {
                    if (_track.VolumeAutomation == null || _track.VolumeAutomation.Count == 0)
                        return read;

                    int channels = WaveFormat.Channels;
                    int sampleRate = WaveFormat.SampleRate;

                    double current = _track.Reader.CurrentTime.TotalSeconds;
                    int frames = read / channels;
                    double startTime = current - (double)frames / sampleRate;

                    for (int frame = 0; frame < frames; frame++)
                    {
                        double t = startTime + (double)frame / sampleRate;
                        float autoGain = _engine.EvaluateAutomation(_track.VolumeAutomation, t, 1.0f);

                        int idx = offset + frame * channels;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            buffer[idx + ch] *= autoGain;
                        }
                    }
                }
            }
            catch { }

            return read;
        }
    }

    /// <summary>
    /// Aplica automatización de efectos en tiempo real, leyendo las curvas de la pista
    /// y ajustando los parámetros de los efectos correspondientes en la cadena de audio.
    /// </summary>
    public class FxAutomationProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly AudioEngine _engine;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public FxAutomationProvider(ISampleProvider source, AudioTrack track, AudioEngine engine)
        {
            _source = source;
            _track = track;
            _engine = engine;
        }

        private void ApplyFxValue(string effectName, float value)
        {
            value = Math.Clamp(value, 0f, 1f);

            switch (effectName)
            {
                case "Delay":
                case "Echo":
                    _track.DelayEffect.WetMix = value;
                    break;
                case "Reverb":
                    _track.ReverbEffect.Mix = value;
                    break;
                case "Chorus":
                    _track.ChorusEffect.Mix = value;
                    break;
                case "Filter":
                    float minHz = 200f, maxHz = 8000f;
                    double minLog = Math.Log(minHz), maxLog = Math.Log(maxHz);
                    float cutoff = (float)Math.Exp(minLog + (maxLog - minLog) * value);
                    _track.FilterEffect.Configure(_track.FilterEffect.CurrentType, cutoff);
                    break;
                case "Pitch":
                    float minTempo = 0.5f, maxTempo = 2.0f;
                    _track.TimeStretchEffect.Tempo = minTempo + (maxTempo - minTempo) * value;
                    break;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            try
            {
                lock (_track.AutomationLock)
                {
                    if (_track.FxAutomation.Count > 0)
                    {
                        double timeSeconds = _track.Reader?.CurrentTime.TotalSeconds ?? _engine.CurrentTime.TotalSeconds;

                        foreach (var kvp in _track.FxAutomation)
                        {
                            var points = kvp.Value;
                            if (points != null && points.Count > 0)
                            {
                                // Nota: Asumimos que la UI mantiene la lista ordenada. Ordenar aquí es costoso.
                                var first = points[0];
                                var last = points[points.Count - 1];

                                float value = (timeSeconds < first.TimeSeconds || timeSeconds > last.TimeSeconds)
                                    ? 0.0f
                                    : _engine.EvaluateAutomation(points, timeSeconds, 0.0f);

                                ApplyFxValue(kvp.Key, value);
                            }
                        }
                    }
                }
            }
            catch { }
            return _source.Read(buffer, offset, count);
        }
    }
    public class LoopingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioEngine _engine;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public LoopingSampleProvider(ISampleProvider source, AudioEngine engine)
        {
            _source = source;
            _engine = engine;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            
            if (_engine.IsLooping && _engine.LoopEnd > _engine.LoopStart)
            {
                if (_engine.CurrentTime >= _engine.LoopEnd)
                {
                    _engine.SetPosition(_engine.LoopStart);
                }
            }
            
            return read;
        }
    }

    public class FftSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Complex[] _fftBuffer;
        private readonly float[] _fftArgs;
        private int _fftPos;
        private readonly int _fftLength;
        private readonly int _m;
        public event Action<float[]> FftCalculated;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public FftSampleProvider(ISampleProvider source, int fftLength = 1024)
        {
            _source = source;
            _fftLength = fftLength;
            _m = (int)Math.Log(fftLength, 2.0);
            _fftBuffer = new Complex[fftLength];
            _fftArgs = new float[fftLength];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead;
            try
            {
                samplesRead = _source.Read(buffer, offset, count);
            }
            catch
            {
                // Si algún proveedor aguas arriba lanza excepción, devolvemos silencio
                Array.Clear(buffer, offset, count);
                return count;
            }
            
            for (int i = 0; i < samplesRead; i++)
            {
                _fftArgs[_fftPos] = buffer[offset + i];
                _fftPos++;
                
                if (_fftPos >= _fftLength)
                {
                    // Aplicar ventana y preparar buffer complejo
                    for (int j = 0; j < _fftLength; j++)
                    {
                        _fftBuffer[j].X = (float)(_fftArgs[j] * FastFourierTransform.HammingWindow(j, _fftLength));
                        _fftBuffer[j].Y = 0;
                    }
                    
                    FastFourierTransform.FFT(true, _m, _fftBuffer);
                    
                    // Calcular magnitudes
                    float[] magnitudes = new float[_fftLength / 2];
                    for (int j = 0; j < _fftLength / 2; j++)
                        magnitudes[j] = (float)Math.Sqrt(_fftBuffer[j].X * _fftBuffer[j].X + _fftBuffer[j].Y * _fftBuffer[j].Y);
                    
                    try
                    {
                        FftCalculated?.Invoke(magnitudes);
                    }
                    catch
                    {
                        // Proteger el hilo de audio frente a errores del suscriptor
                    }
                    _fftPos = 0;
                }
            }
            return samplesRead;
        }
    }

    public class SmartFadeProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public SmartFadeProvider(ISampleProvider source, AudioTrack track)
        {
            _source = source;
            _track = track;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            
            if (_track.Reader == null) return samplesRead;

            double fadeIn = _track.FadeInSeconds;
            double fadeOut = _track.FadeOutSeconds;
            double total = _track.Reader.TotalTime.TotalSeconds;
            
            // Calcular tiempo actual aproximado basado en la posición del Reader
            double currentTime = _track.Reader.CurrentTime.TotalSeconds;
            double startTime = currentTime - ((double)samplesRead / WaveFormat.SampleRate / WaveFormat.Channels);

            if (fadeIn <= 0 && fadeOut <= 0) return samplesRead;

            for (int i = 0; i < samplesRead; i += WaveFormat.Channels)
            {
                double time = startTime + ((double)i / WaveFormat.Channels / WaveFormat.SampleRate);
                float gain = 1.0f;

                if (time < fadeIn) gain = (float)(time / fadeIn);
                else if (time > total - fadeOut) gain = (float)((total - time) / fadeOut);

                if (gain < 1.0f)
                {
                    gain = Math.Max(0f, gain);
                    for (int ch = 0; ch < WaveFormat.Channels; ch++) buffer[offset + i + ch] *= gain;
                }
            }
            return samplesRead;
        }
    }

    /// <summary>
    /// Envuelve un ISampleProvider para que nunca devuelva 0 muestras (fin de stream).
    /// Si la fuente termina, rellena el resto con silencio.
    /// Esto evita que MixingSampleProvider elimine la pista cuando termina.
    /// </summary>
    public class InfiniteSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public InfiniteSampleProvider(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read < count)
            {
                Array.Clear(buffer, offset + read, count - read);
                return count;
            }
            return read;
        }
    }

    /// <summary>
    /// Buffer circular de alto rendimiento para Floats.
    /// Evita conversiones a byte[] y bloqueos excesivos.
    /// </summary>
    public class CircularBufferSampleProvider : ISampleProvider
    {
        private readonly float[] _buffer;
        private int _writePosition;
        private int _readPosition;
        private int _sampleCount;
        private readonly object _lock = new object();

        public WaveFormat WaveFormat { get; }

        public CircularBufferSampleProvider(int channels, int sampleRate, int bufferDurationSeconds)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _buffer = new float[sampleRate * channels * bufferDurationSeconds];
        }

        public void Write(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                // Protección contra desbordamiento si el bloque entrante es mayor que el buffer total
                if (count > _buffer.Length)
                {
                    offset += (count - _buffer.Length);
                    count = _buffer.Length;
                }

                // Si el buffer se llena, sobrescribimos lo más viejo (comportamiento circular)
                // o descartamos. Para audio en tiempo real, mejor descartar si no se consume a tiempo
                // para evitar latencia acumulada, pero aquí implementamos sobrescritura segura.
                
                int bufferLength = _buffer.Length;
                int availableSpace = bufferLength - _sampleCount;
                
                // Si no hay espacio, avanzamos la lectura (drop old samples) para mantener latencia baja
                if (count > availableSpace)
                {
                    int drop = count - availableSpace;
                    _readPosition = (_readPosition + drop) % bufferLength;
                    _sampleCount -= drop;
                }

                int end = bufferLength;
                int writePos = _writePosition;
                int toEnd = end - writePos;

                if (count <= toEnd)
                {
                    Array.Copy(data, offset, _buffer, writePos, count);
                }
                else
                {
                    Array.Copy(data, offset, _buffer, writePos, toEnd);
                    Array.Copy(data, offset + toEnd, _buffer, 0, count - toEnd);
                }

                _writePosition = (_writePosition + count) % bufferLength;
                _sampleCount += count;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_sampleCount == 0) return 0;

                int samplesToRead = Math.Min(count, _sampleCount);
                int bufferLength = _buffer.Length;
                int readPos = _readPosition;
                int toEnd = bufferLength - readPos;

                if (samplesToRead <= toEnd)
                {
                    Array.Copy(_buffer, readPos, buffer, offset, samplesToRead);
                }
                else
                {
                    Array.Copy(_buffer, readPos, buffer, offset, toEnd);
                    Array.Copy(_buffer, 0, buffer, offset + toEnd, samplesToRead - toEnd);
                }

                _readPosition = (_readPosition + samplesToRead) % bufferLength;
                _sampleCount -= samplesToRead;

                return samplesToRead;
            }
        }
    }


    // Comparador para la búsqueda binaria de puntos de automatización
    internal class AutomationPointComparer : IComparer<AutomationPoint>
    {
        public static readonly AutomationPointComparer Instance = new AutomationPointComparer();
        public int Compare(AutomationPoint x, AutomationPoint y)
        {
            return x.TimeSeconds.CompareTo(y.TimeSeconds);
        }
    }

    /// <summary>
    /// Proveedor que bloquea el audio si el transporte del proyecto está detenido,
    /// excepto si es una entrada en vivo (monitorización).
    /// </summary>
    public class TransportAwareSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioEngine _engine;
        public bool IsLiveInput { get; set; }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public TransportAwareSampleProvider(ISampleProvider source, AudioEngine engine, bool isLiveInput)
        {
            _source = source;
            _engine = engine;
            IsLiveInput = isLiveInput;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (IsLiveInput || _engine.IsTransportPlaying)
            {
                return _source.Read(buffer, offset, count);
            }
            
            // Transporte detenido: silencio y no avanzar fuente
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    /// <summary>
    /// Stream wrapper que evita que el stream base sea cerrado al hacer Dispose.
    /// Útil para reutilizar MemoryStream con WaveFileWriter.
    /// </summary>
    public class IgnoreDisposeStream : Stream
    {
        private readonly Stream _baseStream;
        public IgnoreDisposeStream(Stream baseStream) { _baseStream = baseStream; }
        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get => _baseStream.Position; set => _baseStream.Position = value; }
        public override void Flush() => _baseStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _baseStream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* No cerrar baseStream */ }
    }
}