using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
// Resolvemos la ambigüedad especificando que 'Timer' se refiere al de Windows Forms
using Timer = System.Windows.Forms.Timer;

namespace Grabadora
{
    public class TrackControl : UserControl
    {
        private int _trackIndex;
        public int TrackIndex 
        { 
            get => _trackIndex;
            set 
            {
                _trackIndex = value;
                if (_lblTitle != null) _lblTitle.Text = _engine.GetTrackName(_trackIndex);
            }
        }

        // Verifica que esta línea esté escrita exactamente así (RemoveRequested):
        public event EventHandler? RemoveRequested;
        public event EventHandler? DuplicateRequested;
        public event EventHandler<TimeSpan[]>? SelectionChanged; // [Start, End]
        public event EventHandler? MoveUpRequested;
        public event EventHandler? MoveDownRequested;

        private const int DefaultTrackHeight = 150;
        private AudioEngine _engine;
        private PictureBox _waveformBox = null!;
        private Button _btnEq = null!;
        private Button _btnFx = null!;
        private Button _btnAutomation = null!;
        private Button _btnClose = null!;
        private Button _btnMoveTime = null!;
        private Button _btnTempo = null!;
        private Button _btnRecord = null!;
        private MediaButton _btnMonitor = null!; // Botón para monitorizar (Icono Micrófono)
        private Button _btnMute = null!;
        private Button _btnSolo = null!;
        private ComboBox _cmbEffects = null!;
        private Button _btnEffectsLine = null!;
        private Label _lblTitle = null!;
        private Panel _pnlVuMeter = null!;
        private Timer _meterTimer = null!;
        private VerticalFaderControl _faderVolume = null!;
        private KnobControl _knobPan = null!;
        private TextBox _txtNameEditor = null!;
        private ContextMenuStrip _contextMenu = null!;
        
        // Selección
        private bool _isSelecting;
        private Point _selectionStartPoint;
        
        // Fade Handles
        private bool _isDraggingFadeIn;
        private bool _isDraggingFadeOut;
        private const int FadeHandleSize = 8;
        private Rectangle _fadeInHandleRect;
        private Rectangle _fadeOutHandleRect;

        // Propiedades para el dibujado sincronizado
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public List<TimeSpan> KickMarkers { get; set; } = new List<TimeSpan>();
        
        public TimeSpan? SelectionStart { get; set; }
        public TimeSpan? SelectionEnd { get; set; }
        
        public bool SnapToGrid { get; set; }
        public double Bpm { get; set; }

        private enum AutomationViewMode
        {
            None,
            Volume,
            Pan,
            Both
        }

        private AutomationViewMode _automationView = AutomationViewMode.Both;

        private enum AutomationEditType
        {
            Volume,
            Pan
        }

        private bool _isDraggingAutomation;
        private AutomationEditType _automationEditType;
        private AutomationPoint? _draggedAutomationPoint;

        // Drag en iconos de esquina (mover pista / tempo)
        private bool _isDraggingMoveIcon;
        private int _moveIconDragStartScreenX;
        private double _moveIconPreviewOffsetSeconds;

        private bool _isDraggingTempoIcon;
        private int _tempoIconDragStartScreenX;
        private float _tempoBaseFactor;
        private float _tempoPreviewFactor;

        // Arrastre de pista (Move) y Time Stretch en la onda
        private bool _isMovingTrack;
        private bool _isStretchingTrack;
        private string? _currentFxAutomation;
        private readonly Dictionary<string, List<AutomationPoint>> _fxAutomationByEffect = new();
        private bool _isDraggingFxAutomation;
        private AutomationPoint? _draggedFxAutomationPoint;

        // Caché simple de forma de onda para evitar recálculos en cada repintado
        private float[]? _cachedWaveform;
        private int _cachedWidth;
        private double _cachedStartTime;
        private double _cachedEndTime;
        private int _dragStartX;
        private float _visualMoveOffsetX;

        // Línea de automatización de efectos (visual)
        private bool _showFxLine;

        public TrackControl(AudioEngine engine, int trackIndex)
        {
            _engine = engine;
            _trackIndex = trackIndex;
            
            this.Height = 150;
            this.Dock = DockStyle.Top; // Colocar arriba
            this.BackColor = Color.FromArgb(40, 40, 40);
            this.Padding = new Padding(2);

            InitializeControls();
        }

        private Panel _controlPanel = null!;

        private void InitializeControls()
        {
            // Panel de controles (lado izquierdo)
            _controlPanel = new Panel { Dock = DockStyle.Left, Width = 150, BackColor = Color.FromArgb(50, 50, 50) };
            // Botón de Cerrar (X)
            _btnClose = new Button 
            { 
                Text = "×", 
                Location = new Point(3, 3), 
                Size = new Size(18, 18), 
                BackColor = Color.FromArgb(60, 20, 20), 
                ForeColor = Color.Red, 
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0),
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnClose.FlatAppearance.BorderSize = 0;
            _btnClose.Click += (s, e) => RemoveRequested?.Invoke(this, EventArgs.Empty);

            _lblTitle = new Label { Text = _engine.GetTrackName(TrackIndex), ForeColor = Color.White, Location = new Point(25, 4), AutoSize = true, Cursor = Cursors.IBeam, Font = new Font("Segoe UI", 8.5f), UseMnemonic = false };
            _lblTitle.DoubleClick += (s, e) => StartRenaming();

            // Botones rápidos en esquina superior derecha: mover pista en el tiempo y tempo (time-stretch)
            _btnMoveTime = new Button
            {
                Text = "↔",
                Size = new Size(20, 20),
                Location = new Point(0, 0), // Se recoloca en OnResize
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.SizeWE,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            _btnMoveTime.FlatAppearance.BorderSize = 0;
            _btnMoveTime.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnMoveTime.MouseDown += BtnMoveTime_MouseDown;

            _btnTempo = new Button
            {
                Text = "[ ]",
                Size = new Size(20, 20),
                Location = new Point(0, 0), // Se recoloca en OnResize
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.SizeWE,
                Font = new Font("Segoe UI", 7, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            _btnTempo.FlatAppearance.BorderSize = 0;
            _btnTempo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnTempo.MouseDown += BtnTempo_MouseDown;

            // Menú Contextual para opciones de pista
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add("Normalizar Audio", null, async (s, e) => {
                await ((dynamic)_engine).NormalizeTrackAsync(TrackIndex);
                RefreshWaveform();
            });
            _contextMenu.Items.Add("Detectar Kicks (Golpes)", null, async (s, e) => {
                await _engine.DetectKicksAsync(TrackIndex);
                var t = _engine.GetTrack(TrackIndex);
                if (t != null) KickMarkers = t.KickMarkers;
                RefreshVisuals();
            });
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("Fade In Selección", null, async (s, e) => {
                if (SelectionStart.HasValue && SelectionEnd.HasValue) {
                    await ((dynamic)_engine).ApplyFadeInAsync(TrackIndex, SelectionStart.Value, SelectionEnd.Value);
                    RefreshWaveform();
                }
            });
            _contextMenu.Items.Add("Fade Out Selección", null, async (s, e) => {
                if (SelectionStart.HasValue && SelectionEnd.HasValue) {
                    await ((dynamic)_engine).ApplyFadeOutAsync(TrackIndex, SelectionStart.Value, SelectionEnd.Value);
                    RefreshWaveform();
                }
            });
            _contextMenu.Items.Add("Reverse Selección", null, async (s, e) => {
                if (SelectionStart.HasValue && SelectionEnd.HasValue) {
                    await ((dynamic)_engine).ReverseAsync(TrackIndex, SelectionStart.Value, SelectionEnd.Value);
                    RefreshWaveform();
                }
            });
            _contextMenu.Items.Add("Mover pista en el tiempo...", null, (s, e) => ShowMoveTrackInTimeDialog());
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("Automatizar Volumen (0 → 1 en selección)", null, (s, e) => {
                if (SelectionStart.HasValue && SelectionEnd.HasValue)
                {
                    _engine.SetVolumeAutomationLinear(TrackIndex, SelectionStart.Value, SelectionEnd.Value, 0.0f, 1.0f);
                }
                else
                {
                    MessageBox.Show("Primero seleccione un rango de tiempo en la pista.", "Automatización", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
            _contextMenu.Items.Add("Automatizar Paneo (Izq → Der en selección)", null, (s, e) => {
                if (SelectionStart.HasValue && SelectionEnd.HasValue)
                {
                    _engine.SetPanAutomationLinear(TrackIndex, SelectionStart.Value, SelectionEnd.Value, -1.0f, 1.0f);
                }
                else
                {
                    MessageBox.Show("Primero seleccione un rango de tiempo en la pista.", "Automatización", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("Duplicar Pista", null, (s, e) => DuplicateRequested?.Invoke(this, EventArgs.Empty));
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("Mover Arriba", null, (s, e) => MoveUpRequested?.Invoke(this, EventArgs.Empty));
            _contextMenu.Items.Add("Mover Abajo", null, (s, e) => MoveDownRequested?.Invoke(this, EventArgs.Empty));
            _lblTitle.ContextMenuStrip = _contextMenu;

            // TextBox para edición de nombre (oculto por defecto)
            _txtNameEditor = new TextBox 
            { 
                Location = new Point(25, 3), 
                Size = new Size(115, 20), 
                Visible = false,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtNameEditor.KeyDown += (s, e) => 
            { 
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitRename(); }
                else if (e.KeyCode == Keys.Escape) CancelRename();
            };
            // Confirmar al perder el foco
            _txtNameEditor.LostFocus += (s, e) => CommitRename();
            
            // --- Columna 1 (X=5) ---
            _btnEq = new Button { Text = "EQ", Location = new Point(5, 28), Size = new Size(42, 24), BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.Cyan, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8) };
            _btnEq.Click += (s, e) => ShowEffectsWindow(EffectsForm.ViewMode.Equalizer);

            _btnMute = new Button { Text = "M", Location = new Point(5, 56), Size = new Size(42, 24), BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8) };
            _btnMute.Click += (s, e) => 
            {
                _engine.RegisterUndoSnapshot();
                bool isMuted = _engine.ToggleTrackMute(TrackIndex);
                _btnMute.BackColor = isMuted ? Color.IndianRed : Color.FromArgb(70, 70, 70);
            };

            // Botón para mostrar/ocultar curvas de automatización (Vol/Pan)
            _btnAutomation = new Button
            {
                Text = "Auto",
                Location = new Point(105, 3),
                Size = new Size(40, 18),
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 7)
            };
            _btnAutomation.Click += (s, e) =>
            {
                // Ciclar entre: Ambos -> Solo Volumen -> Solo Pan -> Ninguno
                switch (_automationView)
                {
                    case AutomationViewMode.Both:
                        _automationView = AutomationViewMode.Volume;
                        _btnAutomation.Text = "A:V";
                        break;
                    case AutomationViewMode.Volume:
                        _automationView = AutomationViewMode.Pan;
                        _btnAutomation.Text = "A:P";
                        break;
                    case AutomationViewMode.Pan:
                        _automationView = AutomationViewMode.None;
                        _btnAutomation.Text = "A:-";
                        break;
                    default:
                        _automationView = AutomationViewMode.Both;
                        _btnAutomation.Text = "A:VP";
                        break;
                }
                RefreshWaveform();
            };

            // --- Columna 2 (X=52) ---
            _btnFx = new Button { Text = "FX", Location = new Point(52, 28), Size = new Size(42, 24), BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.Orange, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8) };
            _btnFx.Click += (s, e) => ShowEffectsWindow(EffectsForm.ViewMode.Effects);

            _btnSolo = new Button { Text = "S", Location = new Point(52, 56), Size = new Size(42, 24), BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8) };
            _btnSolo.Click += (s, e) => 
            {
                _engine.RegisterUndoSnapshot();
                bool isSolo = _engine.ToggleTrackSolo(TrackIndex);
                _btnSolo.BackColor = isSolo ? Color.Gold : Color.FromArgb(70, 70, 70);
                _btnSolo.ForeColor = isSolo ? Color.Black : Color.White;
            };

            // ComboBox de efectos, debajo de Mute/Solo (20 px)
            _cmbEffects = new ComboBox
            {
                Location = new Point(5, 110),
                Size = new Size(70, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8)
            };
            _cmbEffects.Items.AddRange(new object[]
            {
                "Filter",
                "Chorus",
                "Pitch",
                "Echo",
                "Delay",
                "Reverb"
            });
            if (_cmbEffects.Items.Count > 0)
                _cmbEffects.SelectedIndex = 0;

            // Botón pequeño al lado del ComboBox para mostrar la línea de efectos
            _btnEffectsLine = new Button
            {
                Text = "+",
                Location = new Point(5 + 70 + 2, 110), // a la derecha del combo
                Size = new Size(18, 24),
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            _btnEffectsLine.FlatAppearance.BorderSize = 0;
            _btnEffectsLine.Click += (s, e) =>
            {
                // Guardar el efecto seleccionado y alternar la visibilidad de la línea
                _currentFxAutomation = _cmbEffects.SelectedItem?.ToString();
                _showFxLine = !_showFxLine;
                RefreshVisuals();
            };

            // Botón de Grabación (R) - SOLO si es pista de grabación
            bool isRecordingTrack = _engine.IsTrackRecording(TrackIndex);
            
            if (isRecordingTrack)
            {
                _btnRecord = new Button { Text = "●", Location = new Point(5, 84), Size = new Size(42, 24), BackColor = Color.Red, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8) };
                _btnRecord.Click += (s, e) => 
                { 
                    if (_engine.IsRecording) _engine.StopRecording(); 
                    else _engine.StartRecording();
                };

                // Botón de Monitor (Icono Micrófono)
                _btnMonitor = new MediaButton 
                { 
                    ButtonType = MediaButtonType.Microphone, 
                    Location = new Point(60, 84), 
                    Size = new Size(24, 24) 
                };

                // Estado inicial: activo solo si la pista no está muteada y el motor está monitorizando
                _btnMonitor.IsActive = _engine.IsMonitoringInput && !_engine.GetTrack(TrackIndex).IsMuted; 

                _btnMonitor.Click += (s, e) => 
                {
                    // Si se está grabando, el botón de micrófono solo actúa como Mute/Unmute de la pista
                    if (_engine.IsRecording)
                    {
                        bool isMutedDuringRec = _engine.ToggleTrackMute(TrackIndex);
                        _btnMonitor.IsActive = !isMutedDuringRec;
                        return;
                    }

                    // Fuera de la grabación, el botón controla la monitorización en vivo
                    if (!_engine.IsMonitoringInput)
                    {
                        _engine.StartMonitoring();
                        // No tocar el estado de Mute de la pista grabada;
                        // el usuario usa el botón "M" para silenciar la pista.
                        _btnMonitor.IsActive = true; // Verde cuando se monitoriza
                    }
                    else
                    {
                        _engine.StopMonitoring();
                        // Solo detenemos la monitorización; la pista sigue sonando
                        // según su fader y estado de Mute/Solo.
                        _btnMonitor.IsActive = false; // Gris cuando no se monitoriza
                    }
                };
            }

            // --- Columna 3 (Derecha) ---

            // Knob de Pan (Balance L/R) - Pequeño y centrado sobre el fader
            _knobPan = new KnobControl
            {
                Location = new Point(108, 28),
                Size = new Size(30, 30),
                Minimum = -1.0f,
                Maximum = 1.0f,
                Value = _engine.GetTrackPan(TrackIndex),
                IsBalance = true // Activar modo visual de balance (centro a lados)
            };
            _knobPan.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _engine.RegisterUndoSnapshot(); };
            _knobPan.ValueChanged += (s, e) => _engine.SetTrackPan(TrackIndex, _knobPan.Value);

            // VU Meter
            _pnlVuMeter = new Panel 
            { 
                Location = new Point(105, 62), 
                Size = new Size(6, 80), 
                BackColor = Color.Black 
            };
            _pnlVuMeter.Paint += PnlVuMeter_Paint;

            // Fader de Volumen Vertical
            _faderVolume = new VerticalFaderControl
            {
                Location = new Point(118, 62),
                Size = new Size(20, 80),
                Value = _engine.GetTrackVolume(TrackIndex)
            };
            _faderVolume.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _engine.RegisterUndoSnapshot(); };
            _faderVolume.ValueChanged += (s, e) => _engine.SetTrackVolume(TrackIndex, _faderVolume.Value);

            var controls = new List<Control> { _btnClose, _lblTitle, _txtNameEditor, _btnAutomation, _btnEq, _btnMute, _btnFx, _btnSolo, _cmbEffects, _btnEffectsLine, _knobPan, _pnlVuMeter, _faderVolume };
            if (isRecordingTrack) 
            {
                controls.Add(_btnRecord);
                controls.Add(_btnMonitor);
            }
            
            _controlPanel.Controls.AddRange(controls.ToArray());

            // Timer para actualizar el medidor
            _meterTimer = new Timer();
            _meterTimer.Interval = 50; // 20 FPS
            _meterTimer.Tick += (s, e) =>
            {
                _pnlVuMeter.Invalidate();
            };
            _meterTimer.Start();

            // Spacer para dar un margen visual entre el panel de control y la onda
            var spacer = new Panel { Dock = DockStyle.Left, Width = 30, BackColor = Color.FromArgb(40, 40, 40) };

            // Visualizador de forma de onda
            _waveformBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            _waveformBox.Paint += WaveformBox_Paint;
            _waveformBox.MouseDown += WaveformBox_MouseDown;
            _waveformBox.MouseMove += WaveformBox_MouseMove;
            _waveformBox.MouseUp += WaveformBox_MouseUp;
            
            // IMPORTANTE: En WinForms, el último control agregado tiene prioridad de Docking.
            // Agregamos en orden inverso para que el Panel (agregado al final) empuje a la Onda.
            this.Controls.Add(_waveformBox);
            this.Controls.Add(spacer);
            this.Controls.Add(_controlPanel);

            // Iconos de esquina (sobre la pista completa, lado derecho)
            this.Controls.Add(_btnMoveTime);
            this.Controls.Add(_btnTempo);
            UpdateCornerButtonsLayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateCornerButtonsLayout();
        }

        private void UpdateCornerButtonsLayout()
        {
            if (_btnMoveTime == null || _btnTempo == null || _waveformBox == null) return;

            // Colocar los iconos a la altura del centro de la onda
            int centerY = _waveformBox.Top + _waveformBox.Height / 2;
            int y = centerY - _btnTempo.Height / 2;

            // Calcular posición X del final real del audio (si es visible)
            var track = _engine.GetTrack(TrackIndex);
            // CORRECCIÓN: Ajustar duración por el Tempo actual
            double totalSec = (track?.Reader?.TotalTime.TotalSeconds ?? 0) / (track?.TimeStretchEffect?.Tempo ?? 1.0f);
            double visibleDuration = EndTime - StartTime;
            int waveformLeft = _waveformBox.Left;
            int waveformRight = _waveformBox.Right;
            int targetX;

            if (track == null || track.Reader == null || visibleDuration <= 0 || totalSec <= StartTime)
            {
                // Sin audio o fuera de vista: usar borde derecho de la onda
                targetX = waveformRight;
            }
            else if (totalSec >= EndTime)
            {
                // El final del audio está más allá de la ventana visible: también usar borde derecho
                targetX = waveformRight;
            }
            else
            {
                // El final del audio cae dentro de la ventana visible
                double tRel = (totalSec - StartTime) / visibleDuration; // 0..1
                if (tRel < 0) tRel = 0;
                if (tRel > 1) tRel = 1;
                int xInWave = (int)(tRel * _waveformBox.Width);
                targetX = waveformLeft + xInWave;
            }

            int spacing = 2;
            int tempoX = targetX - _btnTempo.Width - 4; // pequeño margen a la izquierda del final
            int moveX = tempoX - _btnMoveTime.Width - spacing;

            _btnTempo.Location = new Point(tempoX, y);
            _btnMoveTime.Location = new Point(moveX, y);

            _btnMoveTime.BringToFront();
            _btnTempo.BringToFront();
        }

        private void WaveformBox_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 0. Mover Pista (Alt + Drag)
                if ((System.Windows.Forms.Control.ModifierKeys & Keys.Alt) == Keys.Alt)
                {
                    _isMovingTrack = true;
                    _dragStartX = e.X;
                    _visualMoveOffsetX = 0;
                    _waveformBox.Cursor = Cursors.SizeWE;
                    return;
                }

                // 0.1 Time Stretch (Shift + Drag cerca del borde derecho)
                var track = _engine.GetTrack(TrackIndex);
                if (track != null && track.Reader != null && (System.Windows.Forms.Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                {
                    double visibleDuration = EndTime - StartTime;
                    // CORRECCIÓN: Usar duración visual (ajustada por tempo) para el hit test
                    double totalSec = track.Reader.TotalTime.TotalSeconds / track.TimeStretchEffect.Tempo;
                    // Calcular posición X del final del audio
                    if (totalSec >= StartTime && totalSec <= EndTime && visibleDuration > 0)
                    {
                        float endX = (float)((totalSec - StartTime) / visibleDuration * _waveformBox.Width);
                        if (Math.Abs(e.X - endX) < 20) // Zona de agarre de 20px
                        {
                            _isStretchingTrack = true;
                            _dragStartX = e.X;
                            _tempoBaseFactor = track.TimeStretchEffect.Tempo;
                            _waveformBox.Cursor = Cursors.SizeWE;
                            return;
                        }
                    }
                }

                // 1. Verificar si se hizo clic en los manejadores de Fade
                if (_fadeInHandleRect.Contains(e.Location))
                {
                    _isDraggingFadeIn = true;
                    return;
                }
                if (_fadeOutHandleRect.Contains(e.Location))
                {
                    _isDraggingFadeOut = true;
                    return;
                }

                // Edición de automatización con Ctrl+Clic
                if ((System.Windows.Forms.Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    // 'track' ya fue definido arriba (línea 344), lo reutilizamos
                    if (track != null) 
                    {
                        // Primero, si la línea de FX está activa y se hace Ctrl+clic cerca de ella,
                        // editar/crear puntos de automatización de FX para el efecto seleccionado.
                        int width = _waveformBox.Width;
                        int height = _waveformBox.Height;
                        double visibleDuration = EndTime - StartTime;

                        if (_showFxLine && !string.IsNullOrEmpty(_currentFxAutomation) && visibleDuration > 0)
                        {
                            const int fxHitRadius = 6;
                            
                            lock (track.AutomationLock)
                            {
                                var fxList = GetFxAutomationList(_currentFxAutomation);
                                // Intentar arrastrar un punto existente de FX
                                foreach (var p in fxList)
                                {
                                    double tSec = p.TimeSeconds;
                                    if (tSec < StartTime || tSec > EndTime) continue;

                                    float px = (float)((tSec - StartTime) / visibleDuration * width);
                                    float py = (float)((1.0 - p.Value) * (height - 1));
                                    if (Math.Abs(px - e.X) <= fxHitRadius && Math.Abs(py - e.Y) <= fxHitRadius)
                                    {
                                        _isDraggingFxAutomation = true;
                                        _draggedFxAutomationPoint = p;
                                        return;
                                    }
                                }
                            }

                            // Si se hace clic cerca de la línea central, crear un nuevo punto de FX
                            int fxYCenter = height / 2;
                            if (Math.Abs(e.Y - fxYCenter) <= fxHitRadius)
                            {
                                TimeSpan fxAutoTime = PixelToTime(e.X);
                                if (SnapToGrid && Bpm > 0) fxAutoTime = SnapTime(fxAutoTime);

                                var newPoint = new AutomationPoint
                                {
                                    TimeSeconds = fxAutoTime.TotalSeconds,
                                    Value = 0.5f // Valor neutro sobre la línea
                                };

                                lock (track.AutomationLock)
                                {
                                    var fxList = GetFxAutomationList(_currentFxAutomation);
                                    fxList.RemoveAll(p => Math.Abs(p.TimeSeconds - newPoint.TimeSeconds) < 0.01);
                                    fxList.Add(newPoint);
                                    fxList.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                                }
                                RefreshWaveform();
                                return;
                            }
                        }

                        TimeSpan autoTime = PixelToTime(e.X);
                        if (SnapToGrid && Bpm > 0) autoTime = SnapTime(autoTime);

                        // Primero, comprobar si se ha hecho clic cerca de un punto existente para arrastrarlo
                        const int hitRadius = 6;

                        bool HitPoint(float px, float py)
                        {
                            return Math.Abs(px - e.X) <= hitRadius && Math.Abs(py - e.Y) <= hitRadius;
                        }

                        // Intentar hit-test sobre puntos de Volumen, si están visibles
                        if ((_automationView == AutomationViewMode.Both || _automationView == AutomationViewMode.Volume) &&
                            track.VolumeAutomation != null && track.VolumeAutomation.Count > 0 && visibleDuration > 0)
                        {
                            lock (track.AutomationLock)
                            {
                                foreach (var p in track.VolumeAutomation)
                                {
                                    double tSec = p.TimeSeconds;
                                    if (tSec < StartTime || tSec > EndTime) continue;

                                    float px = (float)((tSec - StartTime) / visibleDuration * width);
                                    float py = (float)((1.0 - p.Value) * (height - 1));

                                    if (HitPoint(px, py))
                                    {
                                        _engine.RegisterUndoSnapshot();
                                        _isDraggingAutomation = true;
                                        _automationEditType = AutomationEditType.Volume;
                                        _draggedAutomationPoint = p;
                                        return;
                                    }
                                }
                            }
                        }

                        // Intentar hit-test sobre puntos de Paneo
                        if ((_automationView == AutomationViewMode.Both || _automationView == AutomationViewMode.Pan) &&
                            track.PanAutomation != null && track.PanAutomation.Count > 0 && visibleDuration > 0)
                        {
                            lock (track.AutomationLock)
                            {
                                foreach (var p in track.PanAutomation)
                                {
                                    double tSec = p.TimeSeconds;
                                    if (tSec < StartTime || tSec > EndTime) continue;

                                    float norm = (p.Value + 1.0f) / 2.0f;
                                    float px = (float)((tSec - StartTime) / visibleDuration * width);
                                    float py = (float)((1.0 - norm) * (height - 1));

                                    if (HitPoint(px, py))
                                    {
                                        _engine.RegisterUndoSnapshot();
                                        _isDraggingAutomation = true;
                                        _automationEditType = AutomationEditType.Pan;
                                        _draggedAutomationPoint = p;
                                        return;
                                    }
                                }
                            }
                        }

                        // Si no se hizo clic sobre un punto, crear uno nuevo en esa posición
                        float normY = 1.0f - (float)e.Y / Math.Max(1, height);
                        float volValue = Math.Max(0.0f, Math.Min(1.0f, normY));

                        lock (track.AutomationLock)
                        {
                            if ((System.Windows.Forms.Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                            {
                                float panValue = 2.0f * volValue - 1.0f;
                                _engine.SetPanAutomationPoint(TrackIndex, autoTime, panValue);
                            }
                            else
                            {
                                _engine.SetVolumeAutomationPoint(TrackIndex, autoTime, volValue);
                            }
                        }

                        RefreshWaveform();
                    }
                    return;
                }

                _isSelecting = true;
                _selectionStartPoint = e.Location;
                TimeSpan time = PixelToTime(e.X);
                
                if (SnapToGrid && Bpm > 0) time = SnapTime(time);

                SelectionStart = time;
                SelectionEnd = time;
                SelectionChanged?.Invoke(this, new TimeSpan[] { time, time });
            }
        }

        private void WaveformBox_MouseMove(object? sender, MouseEventArgs e)
        {
            // Lógica de Mover Pista (Visual)
            if (_isMovingTrack)
            {
                _visualMoveOffsetX = e.X - _dragStartX;
                _waveformBox.Invalidate();
                return;
            }

            // Lógica de Time Stretch (Visual)
            if (_isStretchingTrack)
            {
                double visibleDuration = EndTime - StartTime;
                if (visibleDuration > 0)
                {
                    double deltaPixels = e.X - _dragStartX;
                    double deltaSeconds = (deltaPixels / _waveformBox.Width) * visibleDuration;
                    
                    var track = _engine.GetTrack(TrackIndex);
                    if (track != null && track.Reader != null)
                    {
                        double sourceLen = track.Reader.TotalTime.TotalSeconds;
                        // CORRECCIÓN: Calcular nueva longitud visual basada en el tempo base del arrastre
                        double currentVisualLen = sourceLen / _tempoBaseFactor;
                        double newLen = currentVisualLen + deltaSeconds;
                        if (newLen > 0.1)
                        {
                            // Tempo = Original / Nuevo
                            float newTempo = (float)(sourceLen / newLen);
                            _tempoPreviewFactor = Math.Max(0.5f, Math.Min(2.0f, newTempo));
                            _waveformBox.Invalidate();
                        }
                    }
                }
                return;
            }

            // Mostrar/ocultar iconos de final de pista cuando el ratón está cerca del final del audio
            if (!_isDraggingMoveIcon && !_isDraggingTempoIcon && _btnMoveTime != null && _btnTempo != null)
            {
                bool shouldShow = false;
                var track = _engine.GetTrack(TrackIndex);
                // CORRECCIÓN: Ajustar duración por tempo para visibilidad de iconos
                double totalSec = (track?.Reader?.TotalTime.TotalSeconds ?? 0) / (track?.TimeStretchEffect?.Tempo ?? 1.0f);
                double visibleDuration = EndTime - StartTime;

                if (visibleDuration > 0 && totalSec > 0)
                {
                    double endSec = totalSec;

                    // Solo mostramos iconos si el final del audio está dentro de la ventana visible
                    if (endSec > StartTime && endSec < EndTime)
                    {
                        // Mapear fin del audio a coordenada X dentro del PictureBox
                        double tRel = (endSec - StartTime) / visibleDuration;
                        if (tRel < 0) tRel = 0;
                        if (tRel > 1) tRel = 1;
                        double endX = tRel * _waveformBox.Width;

                        // Considerar "cerca" si el ratón está a unos 20px del final
                        shouldShow = Math.Abs(e.X - endX) <= 20;
                    }
                }

                if (_btnMoveTime.Visible != shouldShow)
                {
                    _btnMoveTime.Visible = shouldShow;
                    _btnTempo.Visible = shouldShow;
                }
            }

            // Arrastre de los iconos de mover/tempo tiene prioridad
            if (_isDraggingMoveIcon)
            {
                int deltaPixels = Cursor.Position.X - _moveIconDragStartScreenX;
                double seconds = deltaPixels * 0.05; // 0.05 s por píxel
                _moveIconPreviewOffsetSeconds = seconds;
                return;
            }

            if (_isDraggingTempoIcon)
            {
                int deltaPixels = Cursor.Position.X - _tempoIconDragStartScreenX;
                float factor = _tempoBaseFactor * (1.0f + deltaPixels * 0.01f);
                if (factor < 0.5f) factor = 0.5f;
                if (factor > 2.0f) factor = 2.0f;
                _tempoPreviewFactor = factor;
                return;
            }

            // Arrastre de puntos de automatización
            if (_isDraggingFxAutomation && _draggedFxAutomationPoint != null)
            {
                var track = _engine.GetTrack(TrackIndex);
                if (track == null)
                {
                    _isDraggingFxAutomation = false;
                    _draggedFxAutomationPoint = null;
                    return;
                }

                TimeSpan time = PixelToTime(e.X);
                if (SnapToGrid && Bpm > 0) time = SnapTime(time);

                double tSec = Math.Max(0.0, time.TotalSeconds);
                if (track.Reader != null)
                {
                    tSec = Math.Min(tSec, track.Reader.TotalTime.TotalSeconds);
                }

                _draggedFxAutomationPoint.TimeSeconds = tSec;

                // Actualizar valor vertical (0..1) según posición Y del ratón
                int height = _waveformBox.Height;
                float normY = 1.0f - (float)e.Y / Math.Max(1, height);
                normY = Math.Max(0.0f, Math.Min(1.0f, normY));
                _draggedFxAutomationPoint.Value = normY;

                if (!string.IsNullOrEmpty(_currentFxAutomation))
                {
                    lock (track.AutomationLock)
                    {
                        var fxList = GetFxAutomationList(_currentFxAutomation);
                        fxList.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                    }
                }

                RefreshWaveform();
                return;
            }

            if (_isDraggingAutomation && _draggedAutomationPoint != null)
            {
                var track = _engine.GetTrack(TrackIndex);
                if (track == null)
                {
                    _isDraggingAutomation = false;
                    _draggedAutomationPoint = null;
                    return;
                }

                TimeSpan time = PixelToTime(e.X);
                if (SnapToGrid && Bpm > 0) time = SnapTime(time);

                double tSec = Math.Max(0.0, time.TotalSeconds);
                if (track.Reader != null)
                {
                    tSec = Math.Min(tSec, track.Reader.TotalTime.TotalSeconds);
                }

                int height = _waveformBox.Height;
                float normY = 1.0f - (float)e.Y / Math.Max(1, height);
                normY = Math.Max(0.0f, Math.Min(1.0f, normY));

                _draggedAutomationPoint.TimeSeconds = tSec;

                if (_automationEditType == AutomationEditType.Volume)
                {
                    _draggedAutomationPoint.Value = normY;
                    lock (track.AutomationLock)
                    {
                        if (track.VolumeAutomation != null)
                            track.VolumeAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                    }
                }
                else
                {
                    float panValue = 2.0f * normY - 1.0f;
                    _draggedAutomationPoint.Value = panValue;
                    lock (track.AutomationLock)
                    {
                        if (track.PanAutomation != null)
                            track.PanAutomation.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                    }
                }

                RefreshWaveform();
                return;
            }

            // Cambiar cursor si está sobre los handles de Fade
            if (_fadeInHandleRect.Contains(e.Location) || _fadeOutHandleRect.Contains(e.Location) || _isDraggingFadeIn || _isDraggingFadeOut)
                _waveformBox.Cursor = Cursors.SizeWE;
            else
            {
                // Cursor dinámico según teclas modificadoras
                if ((System.Windows.Forms.Control.ModifierKeys & Keys.Shift) == Keys.Shift) _waveformBox.Cursor = Cursors.SizeWE; // Stretch
                else if ((System.Windows.Forms.Control.ModifierKeys & Keys.Alt) == Keys.Alt) _waveformBox.Cursor = Cursors.SizeAll; // Move
                else _waveformBox.Cursor = Cursors.Default;
            }

            if (_isDraggingFadeIn)
            {
                TimeSpan time = PixelToTime(e.X);
                var track = _engine.GetTrack(TrackIndex);
                if (track != null)
                {
                    track.FadeInSeconds = Math.Max(0, time.TotalSeconds);
                    RefreshWaveform();
                }
                return;
            }

            if (_isDraggingFadeOut)
            {
                TimeSpan time = PixelToTime(e.X);
                var track = _engine.GetTrack(TrackIndex);
                if (track != null && track.Reader != null)
                {
                    track.FadeOutSeconds = Math.Max(0, track.Reader.TotalTime.TotalSeconds - time.TotalSeconds);
                    RefreshWaveform();
                }
                return;
            }

            if (_isSelecting && SelectionStart.HasValue)
            {
                TimeSpan time = PixelToTime(e.X);
                if (SnapToGrid && Bpm > 0) time = SnapTime(time);

                SelectionEnd = time;
                SelectionChanged?.Invoke(this, new TimeSpan[] { SelectionStart.Value, time });
            }
        }

        private async void WaveformBox_MouseUp(object? sender, MouseEventArgs e)
        {
            _isSelecting = false;
            _isDraggingFadeIn = false;
            _isDraggingFadeOut = false;
            _isDraggingAutomation = false;
            _draggedAutomationPoint = null;
            _isDraggingFxAutomation = false;
            _draggedFxAutomationPoint = null;

                if (_isMovingTrack)
                {
                    _isMovingTrack = false;
                    _waveformBox.Cursor = Cursors.Default;
                    
                    double visibleDuration = EndTime - StartTime;
                    double seconds = (_visualMoveOffsetX / _waveformBox.Width) * visibleDuration;
                    _visualMoveOffsetX = 0;
                    _waveformBox.Invalidate();

                    // Aplicar movimiento si es significativo (> 10ms)
                    if (Math.Abs(seconds) > 0.01)
                    {
                        await ((dynamic)_engine).MoveTrackInTimeAsync(TrackIndex, TimeSpan.FromSeconds(seconds));
                        RefreshWaveform();
                    }
                    return;
                }

            if (_isStretchingTrack)
            {
                _isStretchingTrack = false;
                _waveformBox.Cursor = Cursors.Default;
                
                float finalTempo = _tempoPreviewFactor;
                _tempoPreviewFactor = 0;
                _waveformBox.Invalidate();

                // Aplicar Tempo si cambió
                if (finalTempo > 0 && Math.Abs(finalTempo - _tempoBaseFactor) > 0.01f)
                {
                    _engine.RegisterUndoSnapshot();
                    _engine.SetTempo(TrackIndex, finalTempo);
                    RefreshWaveform();
                }
                return;
            }
        }

        private TimeSpan PixelToTime(int x)
        {
            // Evitar divisiones por cero o estados iniciales sin tamaño/tiempo válido
            if (_waveformBox == null || _waveformBox.Width <= 0)
            {
                return TimeSpan.FromSeconds(Math.Max(0, StartTime));
            }

            double duration = EndTime - StartTime;
            if (duration <= 0)
            {
                return TimeSpan.FromSeconds(Math.Max(0, StartTime));
            }

            double timeSeconds = StartTime + ((double)x / _waveformBox.Width * duration);
            return TimeSpan.FromSeconds(Math.Max(0, timeSeconds));
        }

        private TimeSpan SnapTime(TimeSpan t)
        {
            double secondsPerBeat = 60.0 / Bpm;
            double totalBeats = t.TotalSeconds / secondsPerBeat;
            double snappedBeats = Math.Round(totalBeats);
            return TimeSpan.FromSeconds(snappedBeats * secondsPerBeat);
        }

        private void StartRenaming()
        {
            _txtNameEditor.Text = _lblTitle.Text;
            _txtNameEditor.Visible = true;
            _lblTitle.Visible = false;
            _txtNameEditor.Focus();
            _txtNameEditor.SelectAll();
        }

        private void CommitRename()
        {
            if (!_txtNameEditor.Visible) return;

            string newName = _txtNameEditor.Text.Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                _engine.SetTrackName(TrackIndex, newName);
                _lblTitle.Text = newName;
            }
            CancelRename();
        }

        private void CancelRename()
        {
            _txtNameEditor.Visible = false;
            _lblTitle.Visible = true;
        }

        private void PnlVuMeter_Paint(object? sender, PaintEventArgs e)
        {
            _engine.GetTrackLevels(TrackIndex, out float left, out float right);
            float maxLevel = Math.Max(left, right); // Usamos el máximo de los dos canales para un solo medidor fino
            
            int height = _pnlVuMeter.Height;
            int fillHeight = (int)(maxLevel * height);
            
            // Dibujar fondo
            e.Graphics.Clear(Color.Black);

            if (fillHeight > 0)
            {
                // Color dinámico (Verde -> Amarillo -> Rojo)
                Color c = Color.LimeGreen;
                if (maxLevel > 0.9f) c = Color.Red;
                else if (maxLevel > 0.7f) c = Color.Yellow;

                // Dibujar barra de abajo hacia arriba
                using (var b = new SolidBrush(c))
                    e.Graphics.FillRectangle(b, 0, height - fillHeight, _pnlVuMeter.Width, fillHeight);
            }
        }

        private void BtnMoveTime_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _engine.RegisterUndoSnapshot();
                _isDraggingMoveIcon = true;
                _moveIconDragStartScreenX = Cursor.Position.X;
                _moveIconPreviewOffsetSeconds = 0;
                this.Cursor = Cursors.SizeWE;
                this.MouseMove += BtnMoveTime_MouseMove;
                this.MouseUp += BtnMoveTime_MouseUp;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowMoveTrackInTimeDialog();
            }
        }

        private void BtnMoveTime_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isDraggingMoveIcon) return;

            int deltaPixels = Cursor.Position.X - _moveIconDragStartScreenX;
            // 0.05 s por píxel → 20 px = 1 segundo
            double seconds = deltaPixels * 0.05;
            _moveIconPreviewOffsetSeconds = seconds;

            if (Math.Abs(seconds) < 0.05)
            {
                _btnMoveTime.Text = "↔";
            }
            else if (seconds > 0)
            {
                _btnMoveTime.Text = $">{seconds:0.0}";
            }
            else
            {
                _btnMoveTime.Text = $"<{Math.Abs(seconds):0.0}";
            }
        }

        private async void BtnMoveTime_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_isDraggingMoveIcon) return;

            this.MouseMove -= BtnMoveTime_MouseMove;
            this.MouseUp -= BtnMoveTime_MouseUp;
            _isDraggingMoveIcon = false;

            this.Cursor = Cursors.Default;

            _btnMoveTime.Text = "↔";
            double seconds = _moveIconPreviewOffsetSeconds;
            _moveIconPreviewOffsetSeconds = 0;

            if (Math.Abs(seconds) < 0.05) return;

            try
            {
                var offset = TimeSpan.FromSeconds(seconds);
                await ((dynamic)_engine).MoveTrackInTimeAsync(TrackIndex, offset);
                RefreshWaveform();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al mover pista: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnTempo_MouseDown(object? sender, MouseEventArgs e)
        {
            var track = _engine.GetTrack(TrackIndex);
            if (track == null) return;

            if (e.Button == MouseButtons.Left)
            {
                _tempoBaseFactor = track.TimeStretchEffect != null ? track.TimeStretchEffect.Tempo : 1.0f;
                _tempoPreviewFactor = 0;
                _tempoIconDragStartScreenX = Cursor.Position.X;
                _isDraggingTempoIcon = true;
                this.Cursor = Cursors.SizeWE;
                this.MouseMove += BtnTempo_MouseMove;
                this.MouseUp += BtnTempo_MouseUp;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowTempoDialog();
            }
        }

        private void BtnTempo_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isDraggingTempoIcon) return;

            int deltaPixels = Cursor.Position.X - _tempoIconDragStartScreenX;
            // Cada 10 píxeles cambia un 10% aproximadamente
            float factor = _tempoBaseFactor * (1.0f + deltaPixels * 0.01f);
            if (factor < 0.5f) factor = 0.5f;
            if (factor > 2.0f) factor = 2.0f;

            _tempoPreviewFactor = factor;
            _btnTempo.Text = $"x{factor:0.0}";
        }

        private void BtnTempo_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_isDraggingTempoIcon) return;

            this.MouseMove -= BtnTempo_MouseMove;
            this.MouseUp -= BtnTempo_MouseUp;
            _isDraggingTempoIcon = false;

            this.Cursor = Cursors.Default;

            float factor = _tempoPreviewFactor > 0 ? _tempoPreviewFactor : _tempoBaseFactor;
            _tempoPreviewFactor = 0;
            _btnTempo.Text = "[ ]";

            if (Math.Abs(factor - _tempoBaseFactor) < 0.01f) return;

            _engine.RegisterUndoSnapshot();
            _engine.SetTempo(TrackIndex, factor);
            RefreshWaveform();
        }

        private async void ShowMoveTrackInTimeDialog()
        {
            using (var form = new Form())
            {
                form.Text = "Mover pista en el tiempo";
                form.Size = new Size(260, 150);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var lbl = new Label
                {
                    Text = "Desplazamiento (segundos, + derecha / - izquierda):",
                    Location = new Point(10, 15),
                    AutoSize = true
                };

                var num = new NumericUpDown
                {
                    Location = new Point(10, 45),
                    Width = 100,
                    DecimalPlaces = 2,
                    Minimum = -1200,
                    Maximum = 1200,
                    Increment = 0.10M,
                    Value = 0
                };

                var btnOk = new Button
                {
                    Text = "Aplicar",
                    Location = new Point(140, 80),
                    Size = new Size(80, 25),
                    DialogResult = DialogResult.OK
                };

                form.Controls.AddRange(new Control[] { lbl, num, btnOk });
                form.AcceptButton = btnOk;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var seconds = (double)num.Value;
                        var offset = TimeSpan.FromSeconds(seconds);
                        await ((dynamic)_engine).MoveTrackInTimeAsync(TrackIndex, offset);
                        RefreshWaveform();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error al mover pista: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ShowTempoDialog()
        {
            var track = _engine.GetTrack(TrackIndex);
            if (track == null) return;

            using (var form = new Form())
            {
                form.Text = "Cambiar tempo de la pista";
                form.Size = new Size(260, 150);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var lbl = new Label
                {
                    Text = "Factor de tempo (0.5 = mitad, 2.0 = doble):",
                    Location = new Point(10, 15),
                    AutoSize = true
                };

                var num = new NumericUpDown
                {
                    Location = new Point(10, 45),
                    Width = 100,
                    DecimalPlaces = 2,
                    Minimum = 0.50M,
                    Maximum = 2.00M,
                    Increment = 0.05M,
                    Value = (decimal)(track.TimeStretchEffect?.Tempo ?? 1.0f)
                };

                var btnOk = new Button
                {
                    Text = "Aplicar",
                    Location = new Point(140, 80),
                    Size = new Size(80, 25),
                    DialogResult = DialogResult.OK
                };

                form.Controls.AddRange(new Control[] { lbl, num, btnOk });
                form.AcceptButton = btnOk;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    _engine.RegisterUndoSnapshot();
                    _engine.SetTempo(TrackIndex, (float)num.Value);
                    RefreshWaveform();
                }
            }
        }

        private void ShowEffectsWindow(EffectsForm.ViewMode mode)
        {
            var effectsForm = new EffectsForm(_engine, TrackIndex, mode, SelectionStart, SelectionEnd);
            effectsForm.ShowDialog(this); // Modal para simplificar
            // After closing, the audio data might have changed, so refresh.
            RefreshWaveform();
        }

        /// <summary>
        /// Actualiza solo los elementos visuales dinámicos (cursor, selección) 
        /// sin regenerar la forma de onda. Usar en el timer de reproducción.
        /// </summary>
        public void RefreshVisuals()
        {
            UpdateCornerButtonsLayout();
            _waveformBox.Invalidate();
        }

        public void RefreshWaveform()
        {
            // Invalidar caché cuando se solicita un repintado explícito
            _cachedWaveform = null;
            _waveformBox.Invalidate();
        }

        private List<AutomationPoint> GetFxAutomationList(string effectName)
        {
            var track = _engine.GetTrack(TrackIndex);
            if (track == null) return new List<AutomationPoint>();

            if (string.IsNullOrWhiteSpace(effectName)) effectName = "FX";

            lock (track.AutomationLock)
            {
                if (!track.FxAutomation.TryGetValue(effectName, out var list))
                {
                    list = new List<AutomationPoint>();
                    track.FxAutomation[effectName] = list;
                }
                return list;
            }
        }

        private void WaveformBox_Paint(object? sender, PaintEventArgs e)
        {
            if (_engine.TotalTime.TotalSeconds <= 0) return;

            var g = e.Graphics;
            int width = _waveformBox.Width;
            int height = _waveformBox.Height;

            // Declarar visibleDuration aquí para usarlo en todo el método
            double visibleDuration = EndTime - StartTime;
            
            // Obtener referencia a la pista para datos de Fade
            var track = _engine.GetTrack(TrackIndex); // Método auxiliar necesario en AudioEngine o hacerlo público
            float currentTempo = track?.TimeStretchEffect?.Tempo ?? 1.0f;

            // 0. Dibujar Grid (Rejilla) basada en BPM
            if (Bpm > 0)
            {
                double secondsPerBeat = 60.0 / Bpm;
                
                if (visibleDuration > 0)
                {
                    // Calcular primer beat visible
                    double firstBeat = Math.Ceiling(StartTime / secondsPerBeat) * secondsPerBeat;
                    using (Pen gridPen = new Pen(Color.FromArgb(60, 60, 60), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    {
                        for (double t = firstBeat; t <= EndTime; t += secondsPerBeat)
                        {
                            float x = (float)((t - StartTime) / visibleDuration * width);
                            g.DrawLine(gridPen, x, 0, x, height);
                        }
                    }
                }
            }

            // Aplicar transformación visual si se está moviendo la pista
            if (_isMovingTrack) g.TranslateTransform(_visualMoveOffsetX, 0);

            // 1. Dibujar Onda (usando caché para evitar recálculos innecesarios)
            float[] samples;

            // Comprobar si la caché es válida
            if (_cachedWaveform != null &&
                _cachedWidth == width &&
                Math.Abs(_cachedStartTime - StartTime) < 1e-6 &&
                Math.Abs(_cachedEndTime - EndTime) < 1e-6)
            {
                samples = _cachedWaveform;
            }
            else
            {
                // Regenerar caché
                samples = ((dynamic)_engine).GetWaveformData(TrackIndex, width, StartTime, EndTime);
                _cachedWaveform = samples;
                _cachedWidth = width;
                _cachedStartTime = StartTime;
                _cachedEndTime = EndTime;
            }
            int center = height / 2;

            using (Pen pen = new Pen(Color.Cyan, 1))
            {
                for (int x = 0; x < samples.Length; x++)
                {
                    float amplitude = samples[x] * (height / 2);
                    g.DrawLine(pen, x, center - amplitude, x, center + amplitude);
                }
            }

            // Restaurar transformación para elementos globales (Grid, Cursor, Selección)
            if (_isMovingTrack) g.TranslateTransform(-_visualMoveOffsetX, 0);

            // 1.5. Línea vertical de referencia en el centro
            using (Pen centerPen = new Pen(Color.FromArgb(80, 80, 80), 1))
            {
                float centerX = width / 2f;
                g.DrawLine(centerPen, centerX, 0, centerX, height);
            }

            // 2. Dibujar Cursor
            if (visibleDuration > 0)
            {
                double currentSec = _engine.CurrentTime.TotalSeconds;
                if (currentSec >= StartTime && currentSec <= EndTime)
                {
                    float cursorX = (float)((currentSec - StartTime) / visibleDuration * width);
                    using (Pen cursorPen = new Pen(Color.Red, 2))
                    {
                        g.DrawLine(cursorPen, cursorX, 0, cursorX, height);
                    }
                }
            }

            // 3. Dibujar Kicks (si hay)
            if (KickMarkers != null)
            {
                using (Pen kickPen = new Pen(Color.Yellow, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                {
                    foreach (var kickTime in KickMarkers)
                    {
                        double kSec = kickTime.TotalSeconds;
                        if (kSec >= StartTime && kSec <= EndTime)
                        {
                            float x = (float)((kSec - StartTime) / visibleDuration * width);
                            g.DrawLine(kickPen, x, 0, x, height);
                        }
                    }
                }
            }

            // 4. Dibujar Selección
            if (SelectionStart.HasValue && SelectionEnd.HasValue)
            {
                double sSec = SelectionStart.Value.TotalSeconds;
                double eSec = SelectionEnd.Value.TotalSeconds;
                // Asegurar orden
                double min = Math.Min(sSec, eSec);
                double max = Math.Max(sSec, eSec);

                if (max > StartTime && min < EndTime)
                {
                    float x1 = (float)((min - StartTime) / visibleDuration * width);
                    float x2 = (float)((max - StartTime) / visibleDuration * width);
                    using (var brush = new SolidBrush(Color.FromArgb(100, 0, 100, 255))) // Azul semitransparente
                        g.FillRectangle(brush, x1, 0, x2 - x1, height);
                }
            }

            // Volver a aplicar transformación para elementos ligados a la pista (Fades, Automation, FX, notas MIDI)
            if (_isMovingTrack) g.TranslateTransform(_visualMoveOffsetX, 0);

            // 4. Dibujar notas MIDI de la pista (vista simplificada tipo piano-roll)
            if (track != null && track.MidiNotes != null && track.MidiNotes.Count > 0 && Bpm > 0 && visibleDuration > 0)
            {
                double secondsPerBeat = 60.0 / Bpm;

                // Calcular rango de notas presentes para aprovechar al máximo la altura
                int minNote = 127;
                int maxNote = 0;
                foreach (var n in track.MidiNotes)
                {
                    if (n.NoteNumber < minNote) minNote = n.NoteNumber;
                    if (n.NoteNumber > maxNote) maxNote = n.NoteNumber;
                }

                if (minNote <= maxNote)
                {
                    int noteRange = Math.Max(1, maxNote - minNote + 1);
                    float noteHeight = height / (float)noteRange;

                    using (var fill = new SolidBrush(Color.FromArgb(120, Color.MediumPurple)))
                    using (var pen = new Pen(Color.MediumPurple, 1f))
                    {
                        foreach (var note in track.MidiNotes)
                        {
                            double startSec = note.StartBeat * secondsPerBeat;
                            double endSec = startSec + note.DurationBeats * secondsPerBeat;

                            // Recortar al rango visible
                            if (endSec <= StartTime || startSec >= EndTime) continue;

                            double visibleStart = Math.Max(startSec, StartTime);
                            double visibleEnd = Math.Min(endSec, EndTime);

                            float x1 = (float)((visibleStart - StartTime) / visibleDuration * width);
                            float x2 = (float)((visibleEnd - StartTime) / visibleDuration * width);
                            if (x2 <= x1) continue;

                            int noteIndex = note.NoteNumber - minNote;
                            float y = height - (noteIndex + 1) * noteHeight;

                            var rect = new RectangleF(x1, y + 1, x2 - x1, Math.Max(2, noteHeight - 2));
                            g.FillRectangle(fill, rect);
                            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                        }
                    }
                }
            }

            // 4.5 Línea y puntos para automatización de efectos (visual)
            if (_showFxLine)
            {
                int fxYCenter = height / 2;

                using (Pen fxPen = new Pen(Color.Magenta, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                using (Brush fxPointBrush = new SolidBrush(Color.Magenta))
                {
                    // Línea base de referencia en el centro
                    g.DrawLine(fxPen, 0, fxYCenter, width, fxYCenter);

                    // Puntos de automatización para el efecto seleccionado
                    if (!string.IsNullOrEmpty(_currentFxAutomation) && track != null && visibleDuration > 0)
                    {
                        var fxList = GetFxAutomationList(_currentFxAutomation);

                        AutomationPoint? prevFx = null;
                        foreach (var p in fxList)
                        {
                            double tSec = p.TimeSeconds;
                            if (tSec < StartTime || tSec > EndTime) { prevFx = p; continue; }

                            float x = (float)((tSec - StartTime) / visibleDuration * width);
                            float y = (float)((1.0 - p.Value) * (height - 1));

                            if (prevFx != null)
                            {
                                double tPrev = prevFx.TimeSeconds;
                                if (tPrev >= StartTime && tPrev <= EndTime)
                                {
                                    float xPrev = (float)((tPrev - StartTime) / visibleDuration * width);
                                    float yPrev = (float)((1.0 - prevFx.Value) * (height - 1));
                                    g.DrawLine(fxPen, xPrev, yPrev, x, y);
                                }
                            }

                            g.FillEllipse(fxPointBrush, x - 3, y - 3, 6, 6);
                            prevFx = p;
                        }
                    }
                }
            }

            // 5. Dibujar Líneas de Fade (Fade-In / Fade-Out)
            if (track != null && visibleDuration > 0)
            {
                using (Pen fadePen = new Pen(Color.White, 2))
                using (Brush handleBrush = new SolidBrush(Color.White))
                {
                    // Fade In
                    double fadeInTime = track.FadeInSeconds / currentTempo; // Ajustar visualmente por tempo
                    if (fadeInTime > 0 || IsMouseOver(_waveformBox)) // Mostrar handle si hay fade o mouse encima
                    {
                        float xFadeIn = (float)((fadeInTime - StartTime) / visibleDuration * width);
                        float xStart = (float)((0 - StartTime) / visibleDuration * width);
                        
                        // Línea diagonal (0 a 100%)
                        if (fadeInTime > 0)
                            g.DrawLine(fadePen, xStart, height, xFadeIn, 0);
                        
                        // Handle (Círculo)
                        _fadeInHandleRect = new Rectangle((int)xFadeIn - FadeHandleSize/2, 0, FadeHandleSize, FadeHandleSize);
                        g.FillEllipse(handleBrush, _fadeInHandleRect);
                    }

                    // Fade Out
                    double totalTime = (track.Reader?.TotalTime.TotalSeconds ?? 0) / currentTempo; // Ajustar visualmente
                    double fadeOutTime = track.FadeOutSeconds / currentTempo; // Ajustar visualmente
                    if (fadeOutTime > 0 || IsMouseOver(_waveformBox))
                    {
                        double fadeOutStartTime = totalTime - fadeOutTime;
                        float xFadeOutStart = (float)((fadeOutStartTime - StartTime) / visibleDuration * width);
                        float xEnd = (float)((totalTime - StartTime) / visibleDuration * width);

                        // Línea diagonal (100% a 0)
                        if (fadeOutTime > 0)
                            g.DrawLine(fadePen, xFadeOutStart, 0, xEnd, height);

                        // Handle
                        _fadeOutHandleRect = new Rectangle((int)xFadeOutStart - FadeHandleSize/2, 0, FadeHandleSize, FadeHandleSize);
                        g.FillEllipse(handleBrush, _fadeOutHandleRect);
                    }
                }
            }

            // 6. Dibujar curvas de automatización (Volumen y Pan)
            if (track != null && visibleDuration > 0 && _automationView != AutomationViewMode.None)
            {
                // Volumen (0..1) en verde
                if ((_automationView == AutomationViewMode.Both || _automationView == AutomationViewMode.Volume) &&
                    track.VolumeAutomation != null && track.VolumeAutomation.Count > 0)
                {
                    lock (track.AutomationLock)
                    {
                        using (Pen volPen = new Pen(Color.Lime, 2f))
                        using (Brush volPointBrush = new SolidBrush(Color.Lime))
                        {
                            AutomationPoint? prev = null;
                            foreach (var p in track.VolumeAutomation)
                            {
                                double tSec = p.TimeSeconds / currentTempo; // Ajustar visualmente
                                if (tSec < StartTime || tSec > EndTime) { prev = p; continue; }

                                float x = (float)((tSec - StartTime) / visibleDuration * width);
                                float y = (float)((1.0 - p.Value) * (height - 1));

                                if (prev != null && prev.TimeSeconds >= StartTime && prev.TimeSeconds <= EndTime)
                                {
                                    float xPrev = (float)((prev.TimeSeconds / currentTempo - StartTime) / visibleDuration * width);
                                    float yPrev = (float)((1.0 - prev.Value) * (height - 1));
                                    g.DrawLine(volPen, xPrev, yPrev, x, y);
                                }

                                g.FillEllipse(volPointBrush, x - 3, y - 3, 6, 6);
                                prev = p;
                            }
                        }
                    }
                }

                // Paneo (-1..1) en naranja
                if ((_automationView == AutomationViewMode.Both || _automationView == AutomationViewMode.Pan) &&
                    track.PanAutomation != null && track.PanAutomation.Count > 0)
                {
                    lock (track.AutomationLock)
                    {
                        using (Pen panPen = new Pen(Color.Orange, 2f))
                        using (Brush panPointBrush = new SolidBrush(Color.Orange))
                        {
                            AutomationPoint? prev = null;
                            foreach (var p in track.PanAutomation)
                            {
                                double tSec = p.TimeSeconds / currentTempo; // Ajustar visualmente
                                if (tSec < StartTime || tSec > EndTime) { prev = p; continue; }

                                // Normalizar pan (-1..1) a 0..1
                                float norm = (p.Value + 1.0f) / 2.0f;
                                float x = (float)((tSec - StartTime) / visibleDuration * width);
                                float y = (float)((1.0 - norm) * (height - 1));

                                if (prev != null && prev.TimeSeconds >= StartTime && prev.TimeSeconds <= EndTime)
                                {
                                    float normPrev = (prev.Value + 1.0f) / 2.0f;
                                    float xPrev = (float)((prev.TimeSeconds / currentTempo - StartTime) / visibleDuration * width);
                                    float yPrev = (float)((1.0 - normPrev) * (height - 1));
                                    g.DrawLine(panPen, xPrev, yPrev, x, y);
                                }

                                g.FillEllipse(panPointBrush, x - 3, y - 3, 6, 6);
                                prev = p;
                            }
                        }
                    }
                }
            }

            // Feedback visual de Time Stretch (Texto con el nuevo Tempo)
            if (_isStretchingTrack && _tempoPreviewFactor > 0)
            {
                // Resetear transform para dibujar texto fijo en pantalla
                if (_isMovingTrack) g.ResetTransform();

                string text = $"Tempo: x{_tempoPreviewFactor:0.00}";
                using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Yellow))
                {
                    g.DrawString(text, font, brush, width - 120, 10);
                }
            }
            
            // Asegurar reset final
            g.ResetTransform();
        }
        
        private bool IsMouseOver(Control c) => c.ClientRectangle.Contains(c.PointToClient(Cursor.Position));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _meterTimer?.Dispose();
            base.Dispose(disposing);
        }
    }
}