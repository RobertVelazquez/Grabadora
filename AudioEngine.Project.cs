using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace Grabadora
{
    public partial class AudioEngine
    {
        public void ClearHistory()
        {
            _currentFilePath = null;
            _undoStack.Clear();
            _redoStack.Clear();
        }

        public void CreateEmptyProject(int sampleRate, int channels, TimeSpan duration, string projectName, double initialBpm)
        {
            // Reiniciar completamente motor de audio y estado de proyecto
            StopAndDisposeAll(true);
            InitializeEmptyEngine(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
            ClearHistory();

            ProjectDuration = duration;
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? "Proyecto sin nombre" : projectName.Trim();

            IsLooping = false;
            LoopStart = TimeSpan.Zero;
            LoopEnd = TimeSpan.Zero;

            if (initialBpm > 0)
            {
                Bpm = initialBpm;
            }
        }

        public void Undo()
        {
            if (_undoStack.Count > 0)
            {
                string currentState = GetProjectState();
                _redoStack.Push(currentState);

                string previousState = _undoStack.Pop();
                RestoreProjectState(previousState);
            }
        }

        public void Redo()
        {
            if (_redoStack.Count > 0)
            {
                string currentState = GetProjectState();
                _undoStack.Push(currentState);

                string nextState = _redoStack.Pop();
                RestoreProjectState(nextState);
            }
        }

        public void SaveProject(string path)
        {
            var data = CreateProjectData();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(path, json);
        }

        public void LoadProject(string path)
        {
            string json = File.ReadAllText(path);
            // Reutilizamos la lógica de RestoreProjectState que es más robusta.
            RestoreProjectState(json, fromLoadProject: true);
        }

        public void RegisterUndoSnapshot()
        {
            if (_isRestoring) return; // Evita recursión si se llama desde RestoreProjectState
            if (_tracks.Count > 0 || _undoStack.Count > 0) // Allow first action on empty project to be undone
            {
                _undoStack.Push(GetProjectState());
                _redoStack.Clear();
            }
        }

        private ProjectData CreateProjectData()
        {
            var data = new ProjectData
            {
                ProjectName = ProjectName,
                MasterVolume = _masterVolume,
                Bpm = Bpm,
                IsLooping = IsLooping,
                LoopStartSeconds = LoopStart.TotalSeconds,
                LoopEndSeconds = LoopEnd.TotalSeconds,
                ProjectDurationSeconds = ProjectDuration.TotalSeconds,
                MasterEqGains = MasterEqualizer?.GetGains(),
                MasterLimiter = MasterLimiter != null ? new LimiterData
                {
                    Threshold = MasterLimiter.GetThreshold(),
                    Ratio = MasterLimiter.GetRatio(),
                    Attack = MasterLimiter.GetAttack(),
                    Release = MasterLimiter.GetRelease()
                } : null
            };

            foreach (var track in _tracks)
            {
                var td = new TrackData
                {
                    Name = track.Name,
                    Volume = track.UserVolume,
                    Pan = track.PanningProvider.Pan,
                    IsInstrumentTrack = track.Synthesizer != null,
                    IsMuted = track.IsMuted,
                    IsSolo = track.IsSolo,
                    EqGains = track.Equalizer.GetGains(),
                    Tempo = track.TimeStretchEffect.Tempo,
                    Delay = new DelayData { TimeMs = track.DelayEffect.CurrentDelay, Feedback = track.DelayEffect.Feedback, Mix = track.DelayEffect.WetMix },
                    Compressor = new CompressorData
                    {
                        Threshold = track.CompressorEffect.GetThreshold(),
                        Ratio = track.CompressorEffect.GetRatio(),
                        Attack = track.CompressorEffect.GetAttack(),
                        Release = track.CompressorEffect.GetRelease()
                    },
                    Reverb = new ReverbData { Mix = track.ReverbEffect.Mix, Size = track.ReverbEffect.RoomSize },
                    Filter = new FilterData { Type = (int)track.FilterEffect.CurrentType, Cutoff = track.FilterEffect.CurrentCutoff },
                    Distortion = new DistortionData { Drive = track.DistortionEffect.Drive, Mix = track.DistortionEffect.Mix },
                    Chorus = new ChorusData { Mix = track.ChorusEffect.Mix, Depth = track.ChorusEffect.Depth, Rate = track.ChorusEffect.Rate },
                    FadeInSeconds = track.FadeInSeconds,
                    FadeOutSeconds = track.FadeOutSeconds,
                    VolumeAutomation = track.VolumeAutomation?.ToArray(),
                    PanAutomation = track.PanAutomation?.ToArray(),
                    FxAutomation = new Dictionary<string, AutomationPoint[]>(track.FxAutomation.Count)
                };

                foreach (var kv in track.FxAutomation)
                {
                    td.FxAutomation[kv.Key] = kv.Value.ToArray();
                }

                if (track.Synthesizer != null && track.MidiNotes != null)
                {
                    var midiList = new List<MidiNoteData>();
                    foreach (var n in track.MidiNotes)
                    {
                        midiList.Add(new MidiNoteData
                        {
                            NoteNumber = n.NoteNumber,
                            StartBeat = n.StartBeat,
                            DurationBeats = n.DurationBeats,
                            Velocity = n.Velocity
                        });
                    }
                    td.MidiNotes = midiList.ToArray();
                }

                if (track.KickMarkers != null)
                {
                    td.KickMarkers = new List<double>();
                    foreach (var t in track.KickMarkers)
                    {
                        td.KickMarkers.Add(t.TotalSeconds);
                    }
                }

                data.Tracks.Add(td);
            }

            return data;
        }
    }
}
