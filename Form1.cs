using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
// Resolvemos la ambigüedad especificando que 'Timer' se refiere al de Windows Forms
using Timer = System.Windows.Forms.Timer;

namespace Grabadora
{
    public partial class  Form1 : Form
    {
        private AudioEngine _engine;
        private string? _currentFilePath;
        
        // Variables para selección
        private TimeSpan? _selectionStart;
        private TimeSpan? _selectionEnd;
        private bool _isSelecting;
        private int _selectedTrackIndex = 0;
        
        // Controles
        private Panel _tracksContainer = null!; // Reemplaza al PictureBox único
        private MediaButton btnPlay = null!;
        private MediaButton btnStop = null!;
        private MediaButton btnRecord = null!;
        private MediaButton btnLoop = null!;
        private Button btnMasterEq = null!;
        private Button btnMasterLimiter = null!;
        private Panel _recordLed = null!;
        private Panel controlPanel = null!;
        private TimelineControl _timeline = null!;
        private Label timeLabel = null!;
        private Timer playbackTimer = null!;
        private Label lblBpm = null!;
        private CheckBox chkSnap = null!;
        private SpectrumAnalyzerControl _spectrumAnalyzer = null!;
        private double _currentBpm = 120.0;
        private ToolStripMenuItem _trackMenu = null!; // Referencia al menú Pista
        

        // Controles de Zoom
        private HScrollBar waveformScrollBar = null!;
        private float _zoomLevel = 1.0f;

        public Form1()
        {
            InitializeComponent();
            _engine = new AudioEngine();
            // Usar el mismo icono que el ejecutable
            try
            {
                var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null)
                    this.Icon = exeIcon;
            }
            catch
            {
                // Si falla, simplemente dejamos el icono por defecto
            }
            SetupCustomUI();
            _engine.PlaybackStopped += () =>
            {
                // Asegurarnos de que la actualización de la UI se haga en el hilo principal
                this.BeginInvoke((Action)(() => {
                    playbackTimer?.Stop();
                    UpdateTracksVisuals();
                    UpdateTimeLabel();
                }));
            };
            _engine.RecordingFinished += OnRecordingFinished;
            _engine.RecordingStarted += () => this.BeginInvoke((Action)(() => 
            {
                _recordLed.BackColor = Color.Red;
                playbackTimer?.Start();
            }));
            UpdateControlsState(); // Estado inicial (deshabilitado)
        }

        private void SetupCustomUI()
        {
            this.Text = "soundFire";
            this.Size = new Size(1000, 450); // Aumentar ancho para acomodar nuevos botones
            this.BackColor = Color.FromArgb(45, 45, 48); // Tema oscuro

            // 1. Contenedor de Pistas (Panel estándar para permitir Dock=Top y ancho completo)
            _tracksContainer = new Panel(); // Cambiado de FlowLayoutPanel a Panel
            _tracksContainer.Dock = DockStyle.Fill; // Ocupa el centro
            _tracksContainer.BackColor = Color.FromArgb(30, 30, 30);
            _tracksContainer.AutoScroll = true;
            this.Controls.Add(_tracksContainer);

            // 1.1 Barra de desplazamiento para la onda (Zoom)
            waveformScrollBar = new HScrollBar();
            waveformScrollBar.Dock = DockStyle.Top; // Se coloca justo debajo del PictureBox
            waveformScrollBar.Minimum = 0;
            waveformScrollBar.Maximum = 10000;
            waveformScrollBar.Scroll += (s, e) => UpdateTracksVisuals();
            this.Controls.Add(waveformScrollBar);

            // 1.2 Línea de Tiempo (Timeline)
            _timeline = new TimelineControl();
            _timeline.PositionChanged += seconds =>
            {
                // Mover la posición de reproducción cuando el usuario hace clic/arrastra en la regla
                _engine.SetPosition(TimeSpan.FromSeconds(seconds));
                UpdateTracksVisuals();
                UpdateTimeLabel();
            };
            this.Controls.Add(_timeline);

            // 2. Panel de Controles
            controlPanel = new Panel();
            controlPanel.Dock = DockStyle.Bottom;
            controlPanel.Height = 60;
            controlPanel.BackColor = Color.FromArgb(60, 60, 60);
            this.Controls.Add(controlPanel);

            // 3. Botones
            btnPlay = new MediaButton 
            { 
                ButtonType = MediaButtonType.Play, 
                Location = new Point(15, 5) // Centrado verticalmente (60-50)/2 = 5
            };
            btnPlay.Click += BtnPlay_Click;

            btnStop = new MediaButton 
            { 
                ButtonType = MediaButtonType.Stop, 
                Location = new Point(75, 5) 
            };
            btnStop.Click += BtnStop_Click;

            btnRecord = new MediaButton 
            { 
                ButtonType = MediaButtonType.Record, 
                Location = new Point(135, 5) 
            };
            btnRecord.Click += BtnRecord_Click;

            btnLoop = new MediaButton 
            { 
                ButtonType = MediaButtonType.Loop, 
                Location = new Point(225, 5) 
            };
            btnLoop.Click += BtnLoop_Click;

            // Botón Master EQ
            btnMasterEq = new Button
            {
                Text = "EQ Master",
                Location = new Point(280, 15),
                Size = new Size(50, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.Cyan,
                FlatStyle = FlatStyle.Flat
            };
            btnMasterEq.Click += (s, e) => { using (var f = new MasterEqForm(_engine)) f.ShowDialog(this); };

            // Botón Master Limiter
            btnMasterLimiter = new Button
            {
                Text = "Limitador",
                Location = new Point(335, 15),
                Size = new Size(65, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.Orange,
                FlatStyle = FlatStyle.Flat
            };
            btnMasterLimiter.Click += (s, e) => { using (var f = new MasterLimiterForm(_engine)) f.ShowDialog(this); };

            // Indicador de Grabación (LED)
            _recordLed = new Panel
            {
                Size = new Size(15, 15),
                Location = new Point(195, 22),
                BackColor = Color.FromArgb(60, 0, 0) // Rojo oscuro (Apagado)
            };
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, 15, 15);
            _recordLed.Region = new Region(path);


            timeLabel = new Label
            {
                Text = "00:00 / 00:00",
                ForeColor = Color.White,
                Location = new Point(410, 20), // Desplazado a la derecha
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };

            // Nuevo: Analizador de Espectro
            _spectrumAnalyzer = new SpectrumAnalyzerControl
            {
                Location = new Point(560, 10), // Desplazado a la derecha
                Size = new Size(100, 40)
            };
            _engine.FftCalculated += (data) => this.BeginInvoke((Action)(() => _spectrumAnalyzer.SetFftData(data)));

            // 6. Control de Volumen (Lado derecho)
            var lblVolume = new Label 
            { 
                Text = "Volumen", 
                ForeColor = Color.White, 
                Location = new Point(670, 22), // Desplazado a la derecha
                AutoSize = true 
            };

            // Reemplazamos el TrackBar por nuestro nuevo KnobControl
            var knobVolume = new KnobControl 
            { 
                Minimum = 0, 
                Maximum = 100, 
                Value = 100, 
                Location = new Point(730, 10), // Desplazado a la derecha
                Size = new Size(40, 40) 
            };

            var lblVolValue = new Label
            {
                Text = "100%",
                ForeColor = Color.White,
                Location = new Point(775, 22), // Desplazado a la derecha
                AutoSize = true
            };

            knobVolume.ValueChanged += (s, e) => 
            {
                _engine.SetVolume(knobVolume.Value / 100f);
                lblVolValue.Text = $"{(int)knobVolume.Value}%";
            };

            // 7. VU Meter (Nivel de sonido)
            var vuMeter = new VuMeterControl
            {
                Location = new Point(840, 10), // Desplazado a la derecha
                Size = new Size(40, 40)
            };

            // Conectar evento del motor al control visual
            _engine.MeteringUpdate += (left, right) =>
            {
                // Es necesario usar BeginInvoke porque el evento viene de otro hilo (audio)
                if (!this.IsDisposed && this.IsHandleCreated)
                    this.BeginInvoke((Action)(() => vuMeter.SetLevels(left, right)));
            };

            // 8. Etiqueta BPM (Bien pegado a la derecha abajo)
            lblBpm = new Label
            {
                Text = "BPM: --",
                ForeColor = Color.Lime, // Color neón para resaltar
                Location = new Point(885, 22), // Desplazado a la derecha
                AutoSize = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            // 9. CheckBox Snap
            chkSnap = new CheckBox
            {
                Text = "Snap",
                ForeColor = Color.White,
                Location = new Point(885, 42), // Desplazado a la derecha
                AutoSize = true,
                Font = new Font("Segoe UI", 8F)
            };
            chkSnap.CheckedChanged += (s, e) => UpdateTracksVisuals();

            controlPanel.Controls.AddRange(new Control[] { btnPlay, btnStop, btnRecord, _recordLed, btnLoop, btnMasterEq, btnMasterLimiter, timeLabel, _spectrumAnalyzer, lblVolume, knobVolume, lblVolValue, vuMeter, lblBpm, chkSnap });

            // Timer para actualizar el cursor de reproducción
            playbackTimer = new Timer();
            playbackTimer.Interval = 40; // ~25 FPS
            playbackTimer.Tick += PlaybackTimer_Tick;

            // 9. Menú Principal
            SetupMenu();
        }

        private void BtnPlay_Click(object? sender, EventArgs e)
        {
            // Alterna entre reproducir y pausar usando el motor de audio
            if (_engine.IsTransportPlaying)
            {
                _engine.Pause();
                playbackTimer?.Stop();
            }
            else
            {
                _engine.Play();
                playbackTimer?.Start();
            }
            UpdateTracksVisuals();
            UpdateTimeLabel();
        }

        private void BtnStop_Click(object? sender, EventArgs e)
        {
            _engine.Stop();
            playbackTimer?.Stop();
            _recordLed.BackColor = Color.FromArgb(60, 0, 0);
            UpdateTracksVisuals();
            UpdateTimeLabel();
        }

        private void BtnRecord_Click(object? sender, EventArgs e)
        {
            if (_engine.IsRecording)
            {
                _engine.StopRecording();
            }
            else
            {
                try
                {
                    _engine.StartRecording();
                    UpdateControlsState();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al iniciar grabación: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnLoop_Click(object? sender, EventArgs e)
        {
            _engine.IsLooping = !_engine.IsLooping;
            btnLoop.IsActive = _engine.IsLooping;
        }

        private void UpdateControlsState()
        {
            bool projectExists = _engine.IsEngineInitialized;
            bool hasTracks = _engine.TrackCount > 0;

            if (btnPlay != null) btnPlay.Enabled = hasTracks;
            if (btnStop != null) btnStop.Enabled = hasTracks;
            if (btnRecord != null) btnRecord.Enabled = projectExists;
            if (btnMasterEq != null) btnMasterEq.Enabled = hasTracks;
            if (btnMasterLimiter != null) btnMasterLimiter.Enabled = hasTracks;
            if (_trackMenu != null) _trackMenu.Enabled = projectExists;
        }

        private void ShowMixer()
        {
            try
            {
                using (var mixer = new MixerForm(_engine))
                {
                    mixer.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al abrir el mixer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // Detectar Shift + Rueda del Mouse para hacer Zoom
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                // e.Delta indica la dirección (positivo = arriba/acercar, negativo = abajo/alejar)
                float zoomChange = e.Delta > 0 ? 1.0f : -1.0f;
                _zoomLevel = Math.Max(1.0f, Math.Min(20.0f, _zoomLevel + zoomChange));

                UpdateScrollbar();
                UpdateTracksVisuals();
            }
        }

        private void UpdateScrollbar()
        {
            // Ajustar el tamaño del "pulgar" del scrollbar según el nivel de zoom
            int largeChange = (int)(10000 / _zoomLevel);
            if (largeChange > 10000) largeChange = 10000;
            waveformScrollBar.LargeChange = largeChange;
            waveformScrollBar.Enabled = _zoomLevel > 1;
        }

        private void SetupMenu()
        {
            MenuStrip menuStrip = new MenuStrip();
            menuStrip.BackColor = Color.FromArgb(60, 60, 60);
            menuStrip.ForeColor = Color.White;
            menuStrip.Dock = DockStyle.Top;

            // Archivo
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("Archivo");

            // Crear nuevo proyecto
            fileMenu.DropDownItems.Add("Crear proyecto...", null, (s, e) => NewProject());
            
            // Guardar/Cargar Proyecto
            fileMenu.DropDownItems.Add("Guardar Proyecto...", null, (s, e) => SaveProject());
            fileMenu.DropDownItems.Add("Cargar Proyecto...", null, (s, e) => LoadProject());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());

            // Submenú Importar
            ToolStripMenuItem importMenu = new ToolStripMenuItem("Importar Audio");
            importMenu.DropDownItems.Add("Audio MP3", null, (s, e) => ImportAudio("Archivos MP3|*.mp3"));
            importMenu.DropDownItems.Add("Audio WAV", null, (s, e) => ImportAudio("Archivos WAV|*.wav"));
            importMenu.DropDownItems.Add("Audio AAC", null, (s, e) => ImportAudio("Archivos AAC|*.aac"));
            importMenu.DropDownItems.Add("Todos los archivos", null, (s, e) => ImportAudio("Todos los archivos|*.*"));
            fileMenu.DropDownItems.Add(importMenu);

            // Submenú Exportar
            ToolStripMenuItem exportMenu = new ToolStripMenuItem("Exportar Audio");
            exportMenu.DropDownItems.Add("Audio MP3", null, (s, e) => ExportAudio("Archivos MP3|*.mp3"));
            exportMenu.DropDownItems.Add("Audio WAV", null, (s, e) => ExportAudio("Archivos WAV|*.wav"));
            fileMenu.DropDownItems.Add(exportMenu);

            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Salir", null, (s, e) => Close());

            // Editar
            ToolStripMenuItem editMenu = new ToolStripMenuItem("Editar");
            editMenu.DropDownItems.Add(new ToolStripMenuItem("Deshacer", null, (s, e) => 
            { 
                try
                {
                    _engine.Undo(); 
                    RebuildTrackControls(); // Reconstruir UI por si cambiaron las pistas
                    UpdateTimeLabel(); 
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al deshacer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }) { ShortcutKeys = Keys.Control | Keys.Z });
            
            editMenu.DropDownItems.Add(new ToolStripMenuItem("Rehacer", null, (s, e) => 
            { 
                try
                {
                    _engine.Redo(); 
                    RebuildTrackControls();
                    UpdateTimeLabel(); 
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al rehacer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }) { ShortcutKeys = Keys.Control | Keys.Y });
            
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(new ToolStripMenuItem("Cortar", null, async (s, e) => 
            {
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    await _engine.CutAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                    _selectionStart = null; _selectionEnd = null; // Limpiar selección
                    UpdateTracksVisuals();
                    RefreshAllTrackWaveforms();
                }
            }) { ShortcutKeys = Keys.Control | Keys.X });

            editMenu.DropDownItems.Add(new ToolStripMenuItem("Copiar", null, async (s, e) =>
            {
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    await _engine.CopyAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                }
            }) { ShortcutKeys = Keys.Control | Keys.C });

            editMenu.DropDownItems.Add(new ToolStripMenuItem("Pegar", null, async (s, e) =>
            {
                TimeSpan insertTime;
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    insertTime = _selectionStart.Value <= _selectionEnd.Value ? _selectionStart.Value : _selectionEnd.Value;
                }
                else
                {
                    insertTime = _engine.CurrentTime;
                }

                await _engine.PasteAsync(_selectedTrackIndex, insertTime);
                UpdateTracksVisuals();
                RefreshAllTrackWaveforms();
            }) { ShortcutKeys = Keys.Control | Keys.V });
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            
            // Menú Efectos (Nuevo)
            ToolStripMenuItem effectsMenu = new ToolStripMenuItem("Efectos");
            effectsMenu.DropDownItems.Add("Ganancia...", null, (s, e) => ShowGainDialog());
            effectsMenu.DropDownItems.Add(new ToolStripSeparator());
            effectsMenu.DropDownItems.Add("1. Capturar Perfil de Ruido", null, async (s, e) => await CaptureNoiseProfile());
            effectsMenu.DropDownItems.Add("2. Aplicar Reducción de Ruido (Spectral)...", null, (s, e) => ShowSpectralNoiseReductionDialog());
            effectsMenu.DropDownItems.Add(new ToolStripSeparator());
            effectsMenu.DropDownItems.Add("Puerta de Ruido (Noise Gate)...", null, (s, e) => ShowNoiseGateDialog());
            
            editMenu.DropDownItems.Add("Fade In", null, async (s, e) => 
            {
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    await _engine.ApplyFadeInAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                    UpdateTracksVisuals();
                }
            });
            editMenu.DropDownItems.Add("Fade Out", null, async (s, e) => 
            {
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    await _engine.ApplyFadeOutAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                    UpdateTracksVisuals();
                }
            });
            editMenu.DropDownItems.Add("Reverse", null, async (s, e) => 
            {
                if (_selectionStart.HasValue && _selectionEnd.HasValue)
                {
                    await _engine.ReverseAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                    UpdateTracksVisuals();
                }
            });

            // Pista
            _trackMenu = new ToolStripMenuItem("Pista");
            _trackMenu.DropDownItems.Add("Agregar Pista de Audio...", null, (s, e) => ImportBackgroundTrack());
            _trackMenu.DropDownItems.Add("Agregar Pista MIDI (Instrumento Virtual)", null, (s, e) =>
            {
                try
                {
                    _engine.AddInstrumentTrack();
                    AddTrackControl(_engine.TrackCount - 1);
                    UpdateTracksVisuals();
                    UpdateControlsState(); // Actualizar estado por si se creó proyecto vacío
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al agregar pista MIDI: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
            _trackMenu.DropDownItems.Add("Abrir Piano Roll (crear si no existe)", null, (s, e) =>
            {
                try
                {
                    // Buscar primera pista MIDI existente
                    int midiIndex = _engine.FindFirstInstrumentTrackIndex();

                    // Si no hay ninguna, crear una nueva pista MIDI
                    if (midiIndex == -1)
                    {
                        _engine.AddInstrumentTrack();
                        midiIndex = _engine.TrackCount - 1;
                        AddTrackControl(midiIndex);
                        UpdateTracksVisuals();
                        UpdateControlsState();
                    }

                    _selectedTrackIndex = midiIndex;

                    using (var form = new MidiEditorForm(_engine, midiIndex))
                    {
                        form.ShowDialog(this);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"No se pudo abrir el piano roll: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
            _trackMenu.DropDownItems.Add("Editar MIDI de esta pista...", null, (s, e) =>
            {
                // Solo tiene sentido si la pista actual es una pista de instrumento (sintetizador)
                var track = _engine.GetTrack(_selectedTrackIndex);
                if (track == null || track.Synthesizer == null)
                {
                    MessageBox.Show("La pista seleccionada no es una pista MIDI/instrumento.", "Información", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using (var form = new MidiEditorForm(_engine, _selectedTrackIndex))
                {
                    form.ShowDialog(this);
                }
            });
            _trackMenu.DropDownItems.Add("Grabación", null, (s, e) => 
            {
                if (_engine.IsRecording) return; // No crear nuevas pistas mientras se está grabando

                try
                {
                    int before = _engine.TrackCount;
                    _engine.CreateInputTrack(); // Solo crea la pista para monitorizar (una única pista de entrada)

                    // Solo añadir un nuevo control visual si realmente se ha creado una pista nueva
                    if (_engine.TrackCount > before)
                    {
                        AddTrackControl(_engine.TrackCount - 1);
                        UpdateTracksVisuals();
                        UpdateControlsState();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al iniciar grabación: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });

            // Mixer
            _trackMenu.DropDownItems.Add(new ToolStripSeparator());
            _trackMenu.DropDownItems.Add("Abrir Mixer...", null, (s, e) => ShowMixer());

            // Metrónomo
            ToolStripMenuItem metronomeMenu = new ToolStripMenuItem("Metrónomo");
            var itemActive = new ToolStripMenuItem("Activar");
            itemActive.CheckOnClick = true;
            itemActive.CheckedChanged += (s, e) => { _engine.MetronomeEnabled = itemActive.Checked; };
            metronomeMenu.DropDownItems.Add(itemActive);
            metronomeMenu.DropDownItems.Add("Configurar BPM...", null, (s, e) => 
            {
                using (var form = new Form())
                {
                    form.Text = "Configurar BPM";
                    form.Size = new Size(200, 120);
                    form.StartPosition = FormStartPosition.CenterParent;
                    form.FormBorderStyle = FormBorderStyle.FixedDialog;
                    form.MaximizeBox = false;
                    form.MinimizeBox = false;
                    
                    var lbl = new Label { Text = "BPM:", Location = new Point(10, 15), AutoSize = true };
                    var txt = new TextBox { Text = _currentBpm.ToString(), Location = new Point(50, 12), Width = 100 };
                    var btn = new Button { Text = "OK", Location = new Point(50, 45), DialogResult = DialogResult.OK };
                    
                    form.Controls.AddRange(new Control[] { lbl, txt, btn });
                    form.AcceptButton = btn;
                    
                    if (form.ShowDialog(this) == DialogResult.OK && double.TryParse(txt.Text, out double newBpm) && newBpm > 0)
                    {
                        _currentBpm = newBpm;
                        _engine.Bpm = _currentBpm; // Actualizar motor
                        lblBpm.Text = $"BPM: {_currentBpm}";
                        UpdateTracksVisuals();
                    }
                }
            });

            // Nuevo: Detectar BPM automático desde la pista seleccionada
            metronomeMenu.DropDownItems.Add("Detectar BPM desde pista seleccionada", null, async (s, e) =>
            {
                try
                {
                    // Usar la pista actualmente seleccionada en la vista
                    var kicks = await _engine.DetectKicksAsync(_selectedTrackIndex);
                    double detected = _engine.CalculateBpm(kicks);

                    if (detected <= 0)
                    {
                        MessageBox.Show("No se pudo detectar un BPM estable en esta pista.", "Detección de BPM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    _currentBpm = detected;
                    _engine.Bpm = _currentBpm;
                    lblBpm.Text = $"BPM: {_currentBpm}";
                    UpdateTracksVisuals();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al detectar BPM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });

            // Ayuda
            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Ayuda");
            helpMenu.DropDownItems.Add("Acerca de...", null, (s, e) => MessageBox.Show("Grabadora .NET 10\nPotenciado por NAudio", "Acerca de"));

            // Configuración
            ToolStripMenuItem configMenu = new ToolStripMenuItem("Configuración");
            configMenu.DropDownItems.Add("Dispositivos de Audio...", null, (s, e) => ShowSettings());

            menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, effectsMenu, _trackMenu, configMenu, metronomeMenu, helpMenu });
            this.Controls.Add(menuStrip);
            this.MainMenuStrip = menuStrip;
        }

        private void NewProject()
        {
            try
            {
                using (var form = new Form())
                {
                    form.Text = "Nuevo proyecto";
                    form.Size = new Size(340, 280);
                    form.StartPosition = FormStartPosition.CenterParent;
                    form.FormBorderStyle = FormBorderStyle.FixedDialog;
                    form.MaximizeBox = false;
                    form.MinimizeBox = false;

                    var lblName = new Label { Text = "Nombre del proyecto:", Location = new Point(10, 15), AutoSize = true };
                    var txtName = new TextBox { Location = new Point(150, 12), Width = 140, Text = _engine.ProjectName ?? "Proyecto sin nombre" };

                    var lblRate = new Label { Text = "Sample rate (Hz):", Location = new Point(10, 55), AutoSize = true };
                    var numRate = new NumericUpDown { Location = new Point(150, 52), Width = 100, Minimum = 8000, Maximum = 192000, Increment = 1000 };
                    numRate.Value = _engine.SampleRate;

                    var lblChannels = new Label { Text = "Canales:", Location = new Point(10, 95), AutoSize = true };
                    var cboChannels = new ComboBox { Location = new Point(150, 92), Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
                    cboChannels.Items.AddRange(new object[] { "Mono (1)", "Estéreo (2)" });
                    cboChannels.SelectedIndex = 1; // Estéreo por defecto

                    var lblDuration = new Label { Text = "Duración (minutos):", Location = new Point(10, 135), AutoSize = true };
                    var numDuration = new NumericUpDown { Location = new Point(150, 132), Width = 100, Minimum = 1, Maximum = 600, DecimalPlaces = 0 };

                    double minutes = _engine.ProjectDuration.TotalMinutes;
                    if (minutes >= 1 && minutes <= 600)
                        numDuration.Value = (decimal)minutes;
                    else
                        numDuration.Value = 5; // valor por defecto

                    var lblBpmInit = new Label { Text = "BPM inicial:", Location = new Point(10, 175), AutoSize = true };
                    var numBpmInit = new NumericUpDown { Location = new Point(150, 172), Width = 100, Minimum = 40, Maximum = 300, DecimalPlaces = 0, Value = (decimal)_currentBpm };

                    var btnOk = new Button { Text = "Crear", Location = new Point(80, 205), DialogResult = DialogResult.OK };
                    var btnCancel = new Button { Text = "Cancelar", Location = new Point(180, 205), DialogResult = DialogResult.Cancel };

                    form.Controls.AddRange(new Control[] { lblName, txtName, lblRate, numRate, lblChannels, cboChannels, lblDuration, numDuration, lblBpmInit, numBpmInit, btnOk, btnCancel });
                    form.AcceptButton = btnOk;
                    form.CancelButton = btnCancel;

                    if (form.ShowDialog(this) != DialogResult.OK)
                        return;

                    // Confirmar si ya hay un proyecto con pistas cargadas
                    if (_engine.TrackCount > 0 || !string.IsNullOrEmpty(_currentFilePath))
                    {
                        if (MessageBox.Show("Esto borrará el proyecto actual. ¿Desea continuar?", "Nuevo proyecto", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                            return;
                    }

                    // Detener reproducción/grabación actuales
                    if (_engine.IsRecording)
                    {
                        _engine.StopRecording();
                    }
                    _engine.Stop();
                    playbackTimer?.Stop();

                    string projectName = txtName.Text;
                    int sampleRate = (int)numRate.Value;
                    int channels = cboChannels.SelectedIndex == 0 ? 1 : 2;
                    TimeSpan duration = TimeSpan.FromMinutes((double)numDuration.Value);
                    double initialBpm = (double)numBpmInit.Value;

                    _engine.CreateEmptyProject(sampleRate, channels, duration, projectName, initialBpm);

                    // Limpiar selección, marcadores y UI
                    _selectionStart = null;
                    _selectionEnd = null;
                    _isSelecting = false;
                    _selectedTrackIndex = 0;
                    _currentFilePath = null;

                    _tracksContainer.Controls.Clear();

                    // Sincronizar BPM, loop, zoom y etiquetas
                    btnLoop.IsActive = _engine.IsLooping;
                    _currentBpm = _engine.Bpm;
                    lblBpm.Text = $"BPM: {_currentBpm}";

                    _zoomLevel = 1.0f;
                    UpdateScrollbar();
                    UpdateTracksVisuals();
                    UpdateTimeLabel();
                    UpdateControlsState(); // Habilitar controles

                    this.Text = $"{_engine.ProjectName} - SoundFire";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al crear un nuevo proyecto: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveProject()
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "Proyecto Grabadora|*.grb";
                if (!string.IsNullOrWhiteSpace(_engine.ProjectName))
                {
                    sfd.FileName = _engine.ProjectName + ".grb";
                }
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _engine.SaveProject(sfd.FileName);
                        MessageBox.Show("Proyecto guardado correctamente.", "Éxito");
                    }
                    catch (Exception ex) { MessageBox.Show($"Error al guardar: {ex.Message}", "Error"); }
                }
            }
        }

        private void LoadProject()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Proyecto Grabadora|*.grb";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _engine.LoadProject(ofd.FileName);
                        // Al cargar un proyecto nuevo, limpiamos el historial de Undo/Redo
                        _engine.ClearHistory();
                        
                        // Reconstruir UI
                        _tracksContainer.Controls.Clear();
                        for (int i = 0; i < _engine.TrackCount; i++) AddTrackControl(i);
                        
                        UpdateTracksVisuals();
                        _currentBpm = _engine.Bpm;
                        lblBpm.Text = $"BPM: {_currentBpm}";
                        _zoomLevel = 1.0f;
                        UpdateScrollbar();
                        UpdateTimeLabel();
                        this.Text = $"{_engine.ProjectName} - Grabadora y Editor .NET 10 (WinForms)";
                        UpdateControlsState(); // Habilitar controles
                        MessageBox.Show("Proyecto cargado correctamente.", "Éxito");
                    }
                    catch (Exception ex) { MessageBox.Show($"Error al cargar: {ex.Message}", "Error"); }
                }
            }
        }

        private void ImportAudio(string filter)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = filter;
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    _currentFilePath = openFileDialog.FileName;
                    try
                    {
                        _engine.ClearHistory(); // Limpiar historial al abrir nuevo archivo
                        _engine.LoadFile(_currentFilePath);
                        
                        _tracksContainer.Controls.Clear();
                        AddTrackControl(0); // Agregar la pista principal
                        
                        UpdateTimeLabel();
                        _zoomLevel = 1.0f; // Resetear zoom al cargar nuevo archivo
                        waveformScrollBar.Value = 0; // Resetear scroll
                        UpdateScrollbar(); // Resetear scrollbar
                        UpdateTracksVisuals(); // Forzar dibujado de la onda
                        UpdateControlsState(); // Habilitar controles
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al cargar el archivo: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ImportBackgroundTrack()
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Archivos de Audio|*.mp3;*.wav;*.aac";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _engine.AddTrack(openFileDialog.FileName);
                        AddTrackControl(_engine.TrackCount - 1);
                        UpdateTimeLabel();
                        UpdateTracksVisuals(); // Forzar dibujado de la nueva pista
                        UpdateControlsState();
                        MessageBox.Show("Pista agregada correctamente.", "Éxito");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al agregar pista: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ShowSettings()
        {
            using (var form = new SettingsForm(_engine))
            {
                form.ShowDialog(this);
            }
        }

        private void RebuildTrackControls()
        {
            _tracksContainer.Controls.Clear();
            for (int i = 0; i < _engine.TrackCount; i++) AddTrackControl(i);
            UpdateTracksVisuals();
        }

        private void AddTrackControl(int trackIndex)
        {
            var trackControl = new TrackControl(_engine, trackIndex);
            trackControl.RemoveRequested += TrackControl_RemoveRequested;
            trackControl.SelectionChanged += TrackControl_SelectionChanged;
            trackControl.DuplicateRequested += TrackControl_DuplicateRequested;
            trackControl.MoveUpRequested += TrackControl_MoveUpRequested;
            trackControl.MoveDownRequested += TrackControl_MoveDownRequested;
            
            _tracksContainer.Controls.Add(trackControl);
            // Asegurar que la nueva pista se agregue al final visualmente (índice mayor = más abajo)
            _tracksContainer.Controls.SetChildIndex(trackControl, _tracksContainer.Controls.Count - 1);
        }

        private void TrackControl_MoveUpRequested(object? sender, EventArgs e)
        {
            if (sender is TrackControl tc)
            {
                int index = tc.TrackIndex;
                if (index > 0)
                {
                    _engine.MoveTrack(index, -1);
                    // Intercambiar visualmente: Mover el control al índice anterior
                    _tracksContainer.Controls.SetChildIndex(tc, index - 1);
                    RefreshTrackIndices();
                }
            }
        }

        private void TrackControl_MoveDownRequested(object? sender, EventArgs e)
        {
            if (sender is TrackControl tc)
            {
                int index = tc.TrackIndex;
                if (index < _engine.TrackCount - 1)
                {
                    _engine.MoveTrack(index, 1);
                    // Intercambiar visualmente: Mover el control al índice siguiente
                    _tracksContainer.Controls.SetChildIndex(tc, index + 1);
                    RefreshTrackIndices();
                }
            }
        }

        private void RefreshTrackIndices()
        {
            // Actualizar los índices de todas las pistas según su orden visual actual
            for (int i = 0; i < _tracksContainer.Controls.Count; i++)
            {
                if (_tracksContainer.Controls[i] is TrackControl tc)
                {
                    tc.TrackIndex = i;
                }
            }
        }

        private void TrackControl_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is TrackControl tc)
            {
                if (MessageBox.Show($"¿Estás seguro de eliminar la Pista {tc.TrackIndex + 1}?", "Eliminar Pista", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    // 1. Eliminar del motor de audio
                    _engine.RemoveTrack(tc.TrackIndex);
                    
                    // 2. Eliminar de la interfaz
                    _tracksContainer.Controls.Remove(tc);
                    tc.Dispose(); // Importante: Detiene el Timer interno del control
                    
                    // 3. Actualizar índices de las pistas restantes
                    int newIndex = 0;
                    foreach (Control c in _tracksContainer.Controls)
                    {
                        if (c is TrackControl t) t.TrackIndex = newIndex++;
                    }

                    // 4. Refrescar visualización
                    UpdateTracksVisuals();
                    UpdateTimeLabel();
                    UpdateControlsState();
                }
            }
        }

        private void TrackControl_DuplicateRequested(object? sender, EventArgs e)
        {
            if (sender is TrackControl tc)
            {
                _engine.DuplicateTrack(tc.TrackIndex);
                AddTrackControl(_engine.TrackCount - 1);
                UpdateTracksVisuals();
            }
        }

        private void TrackControl_SelectionChanged(object? sender, TimeSpan[] range)
        {
            if (sender is TrackControl tc)
            {
                _selectedTrackIndex = tc.TrackIndex;
            }

            _selectionStart = range[0];
            _selectionEnd = range[1];
            
            // Actualizar Loop en el motor si está activo o si se selecciona
            TimeSpan min = _selectionStart.Value < _selectionEnd.Value ? _selectionStart.Value : _selectionEnd.Value;
            TimeSpan max = _selectionStart.Value > _selectionEnd.Value ? _selectionStart.Value : _selectionEnd.Value;
            
            _engine.LoopStart = min;
            _engine.LoopEnd = max;

            // Actualizar visuales en todas las pistas
            UpdateTracksVisuals();
        }

        private async void ShowGainDialog()
        {
            if (!_selectionStart.HasValue || !_selectionEnd.HasValue) { MessageBox.Show("Seleccione un rango de audio primero.", "Selección requerida"); return; }
            
            using (var form = new Form())
            {
                form.Text = "Aplicar Ganancia";
                form.Size = new Size(250, 150);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;

                var lbl = new Label { Text = "Ganancia (dB):", Location = new Point(20, 20), AutoSize = true, ForeColor = Color.Black };
                var num = new NumericUpDown { Location = new Point(120, 18), Minimum = -60, Maximum = 24, DecimalPlaces = 1, Value = 0 };
                var btn = new Button { Text = "Aplicar", Location = new Point(80, 60), DialogResult = DialogResult.OK };

                form.Controls.AddRange(new Control[] { lbl, num, btn });
                form.AcceptButton = btn;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    await _engine.ApplyGainToSelectionAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value, (float)num.Value);
                    UpdateTracksVisuals();
                }
            }
        }

        private async void ShowNoiseGateDialog()
        {
            if (!_selectionStart.HasValue || !_selectionEnd.HasValue) { MessageBox.Show("Seleccione un rango de audio primero.", "Selección requerida"); return; }

            using (var form = new Form())
            {
                form.Text = "Reducción de Ruido";
                form.Size = new Size(320, 200);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;

                var lbl = new Label { Text = "Umbral (dB, típico voz -35):", Location = new Point(20, 20), AutoSize = true, ForeColor = Color.Black };
                var num = new NumericUpDown { Location = new Point(220, 18), Minimum = -80, Maximum = -5, DecimalPlaces = 1, Value = -35 };

                var lblPreset = new Label { Text = "Preset:", Location = new Point(20, 60), AutoSize = true, ForeColor = Color.Black };
                var cboPreset = new ComboBox { Location = new Point(80, 58), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                cboPreset.Items.AddRange(new object[]
                {
                    "Muy suave (-30 dB)",
                    "Voz estándar (-35 dB)",
                    "Fuerte (-45 dB)"
                });
                cboPreset.SelectedIndex = 1; // Voz estándar
                cboPreset.SelectedIndexChanged += (s, e2) =>
                {
                    switch (cboPreset.SelectedIndex)
                    {
                        case 0: num.Value = -30; break;
                        case 1: num.Value = -35; break;
                        case 2: num.Value = -45; break;
                    }
                };

                var btn = new Button { Text = "Aplicar", Location = new Point(110, 110), DialogResult = DialogResult.OK };

                form.Controls.AddRange(new Control[] { lbl, num, lblPreset, cboPreset, btn });
                form.AcceptButton = btn;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    await _engine.ApplyNoiseReductionToSelectionAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value, (float)num.Value);
                    UpdateTracksVisuals();
                }
            }
        }

        private async Task CaptureNoiseProfile()
        {
            if (!_selectionStart.HasValue || !_selectionEnd.HasValue) { MessageBox.Show("Seleccione una muestra de SOLO ruido.", "Selección requerida"); return; }
            try
            {
                await _engine.CaptureNoiseProfileAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value);
                MessageBox.Show("Perfil de ruido capturado correctamente.\nAhora seleccione el audio a limpiar y use 'Aplicar Reducción de Ruido'.", "Captura Exitosa");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al capturar ruido: {ex.Message}", "Error");
            }
        }

        private async void ShowSpectralNoiseReductionDialog()
        {
            if (!_selectionStart.HasValue || !_selectionEnd.HasValue) { MessageBox.Show("Seleccione el audio que desea limpiar.", "Selección requerida"); return; }

            using (var form = new Form())
            {
                form.Text = "Reducción de Ruido Espectral";
                form.Size = new Size(340, 200);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;

                var lbl = new Label { Text = "Fuerza (0.0 - 2.0, suave 0.8):", Location = new Point(20, 20), AutoSize = true, ForeColor = Color.Black };
                var num = new NumericUpDown { Location = new Point(230, 18), Minimum = 0, Maximum = 2, DecimalPlaces = 1, Increment = 0.1M, Value = 0.8M };

                var lblPreset = new Label { Text = "Preset:", Location = new Point(20, 60), AutoSize = true, ForeColor = Color.Black };
                var cboPreset = new ComboBox { Location = new Point(80, 58), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
                cboPreset.Items.AddRange(new object[]
                {
                    "Suave (0.6)",
                    "Estándar (0.8)",
                    "Fuerte (1.2)"
                });
                cboPreset.SelectedIndex = 1; // Estándar
                cboPreset.SelectedIndexChanged += (s, e2) =>
                {
                    switch (cboPreset.SelectedIndex)
                    {
                        case 0: num.Value = 0.6M; break;
                        case 1: num.Value = 0.8M; break;
                        case 2: num.Value = 1.2M; break;
                    }
                };

                var btn = new Button { Text = "Aplicar", Location = new Point(120, 110), DialogResult = DialogResult.OK };

                form.Controls.AddRange(new Control[] { lbl, num, lblPreset, cboPreset, btn });
                form.AcceptButton = btn;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    try {
                        await _engine.ApplySpectralNoiseReductionAsync(_selectedTrackIndex, _selectionStart.Value, _selectionEnd.Value, (float)num.Value);
                        UpdateTracksVisuals();
                    } catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
                }
            }
        }

        private async void ExportAudio(string filter)
        {
            if (_engine.TotalTime == TimeSpan.Zero)
            {
                MessageBox.Show("No hay audio cargado para exportar.", "Advertencia");
                return;
            }

            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = filter;
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (var progressForm = new ProgressForm())
                        {
                            progressForm.Show(this);

                            var progress = new Progress<double>(p => progressForm.ReportProgress(p));

                            await _engine.ExportAsync(saveFileDialog.FileName, progress, progressForm.CancellationTokenSource.Token);

                            progressForm.Close();

                            if (progressForm.CancellationTokenSource.IsCancellationRequested)
                            {
                                MessageBox.Show("Exportación cancelada.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                // Opcional: Borrar archivo parcial
                                try { System.IO.File.Delete(saveFileDialog.FileName); } catch { }
                            }
                            else
                            {
                                MessageBox.Show("Archivo exportado correctamente.", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                        }
                    }
                    catch (Exception ex) { MessageBox.Show($"Error al exportar: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
        }

        private void OnRecordingFinished(string filePath)
        {
            this.BeginInvoke((Action)(() => {
                try
                {
                    // Apagar LED
                    _recordLed.BackColor = Color.FromArgb(60, 0, 0);

                    // Finalizar la grabación en el motor (reemplaza la pista monitor por la de archivo)
                    _engine.FinalizeRecording(filePath);

                    // Refrescar la UI (el TrackControl ya existe, solo necesita redibujarse con la onda real)
                    UpdateTimeLabel();
                    UpdateTracksVisuals();

                    // Forzar repintado para mostrar la onda
                    foreach(Control c in _tracksContainer.Controls) if(c is TrackControl tc) tc.RefreshWaveform(); // Aquí sí queremos regenerar datos
                }
                catch (Exception ex) { MessageBox.Show($"Error al procesar grabación: {ex.Message}"); }
            }));
        }


        private void UpdateTracksVisuals()
        {
            // Calcular rango de tiempo visible
            double totalSeconds = _engine.TotalTime.TotalSeconds;
            if (totalSeconds <= 0) return;

            double visibleDuration = totalSeconds / _zoomLevel;
            double scrollRatio = (double)waveformScrollBar.Value / waveformScrollBar.Maximum;
            double startTime = scrollRatio * totalSeconds;
            double endTime = startTime + visibleDuration;

            // Actualizar Timeline
            _timeline.StartTime = startTime;
            _timeline.EndTime = endTime;
            _timeline.ContentWidth = _tracksContainer.ClientSize.Width; // Sincronizar ancho con las pistas (por si hay scrollbar vertical)
            _timeline.CurrentTimeSeconds = _engine.CurrentTime.TotalSeconds;
            _timeline.Invalidate();

            foreach (Control c in _tracksContainer.Controls)
            {
                if (c is TrackControl tc)
                {
                    tc.StartTime = startTime;
                    tc.EndTime = endTime;
                    tc.KickMarkers = _engine.GetTrack(tc.TrackIndex)?.KickMarkers ?? new List<TimeSpan>(); // Pasar marcadores de la pista
                    
                    if (tc.TrackIndex == _selectedTrackIndex)
                    {
                        tc.SelectionStart = _selectionStart;
                        tc.SelectionEnd = _selectionEnd;
                    }
                    else
                    {
                        tc.SelectionStart = null;
                        tc.SelectionEnd = null;
                    }
                    
                    tc.SnapToGrid = chkSnap.Checked;
                    tc.Bpm = _currentBpm;
                    tc.RefreshVisuals(); // Usar método optimizado para redibujado ligero
                }
            }
        }

        private void RefreshAllTrackWaveforms()
        {
            foreach (Control c in _tracksContainer.Controls)
            {
                if (c is TrackControl tc)
                {
                    tc.RefreshWaveform();
                }
            }
        }

        // Nota: La lógica de selección con mouse se ha simplificado/eliminado en esta refactorización
        // para centrarse en la estructura de pistas. Se puede re-implementar dentro de TrackControl si es necesario.
        /*
        private void WaveformPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _engine.TotalTime.TotalSeconds > 0)
            {
                _isSelecting = true;
                _selectionStart = PixelToTime(e.X);
                _selectionEnd = _selectionStart;
                waveformPictureBox.Invalidate();
            }
        }

        private void WaveformPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isSelecting && _engine.TotalTime.TotalSeconds > 0)
            {
                _selectionEnd = PixelToTime(e.X);
                waveformPictureBox.Invalidate();
            }
        }

        private void WaveformPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
                // Asegurar orden correcto (Start < End)
                if (_selectionStart > _selectionEnd)
                {
                    var temp = _selectionStart;
                    _selectionStart = _selectionEnd;
                    _selectionEnd = temp;
                }
            }
        }
        */

        private TimeSpan PixelToTime(int x)
        {
            if (_engine.TotalTime.TotalSeconds <= 0) return TimeSpan.Zero;
            
            double totalSeconds = _engine.TotalTime.TotalSeconds;
            double visibleDuration = totalSeconds / _zoomLevel;
            double scrollRatio = (double)waveformScrollBar.Value / waveformScrollBar.Maximum;
            double startTime = scrollRatio * totalSeconds;

            // Nota: Esto requeriría saber el ancho del control específico
            double pct = (double)x / _tracksContainer.Width; 
            return TimeSpan.FromSeconds(startTime + (pct * visibleDuration));
        }

        private void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            UpdateTracksVisuals();
            UpdateTimeLabel();

            // Auto-stop manual ya que ahora usamos ReadFully = true en el mixer
            if (!_engine.IsRecording && !_engine.IsLooping && _engine.TotalTime.TotalSeconds > 0)
            {
                // Si llegamos al final
                if (_engine.CurrentTime >= _engine.TotalTime)
                {
                    BtnStop_Click(sender, e);
                }
            }
        }

        private void UpdateTimeLabel()
        {
            timeLabel.Text = $"{_engine.CurrentTime:mm\\:ss} / {_engine.TotalTime:mm\\:ss}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); _engine.Dispose(); }
    }
}