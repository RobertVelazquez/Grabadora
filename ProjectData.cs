using System;
using System.Collections.Generic;

#nullable disable

namespace Grabadora
{
    public class ProjectData
    {
        public string ProjectName { get; set; }
        public float MasterVolume { get; set; }
        public double Bpm { get; set; }
        public bool IsLooping { get; set; }
        public double LoopStartSeconds { get; set; }
        public double LoopEndSeconds { get; set; }
        public double ProjectDurationSeconds { get; set; }
        public float[] MasterEqGains { get; set; }
        public LimiterData MasterLimiter { get; set; }
        public List<TrackData> Tracks { get; set; } = new List<TrackData>();
    }

    public class AutomationPoint
    {
        public double TimeSeconds { get; set; }
        public float Value { get; set; }
    }

    public class TrackData
    {
        public string FilePath { get; set; }
        public string Name { get; set; }
        public float Volume { get; set; }
        public float Pan { get; set; }
        public bool IsRecordingTrack { get; set; }
        public bool IsInstrumentTrack { get; set; }
        public bool IsMuted { get; set; }
        public bool IsSolo { get; set; }
        
        // Efectos
        public float[] EqGains { get; set; }
        public DelayData Delay { get; set; }
        public CompressorData Compressor { get; set; }
        public ReverbData Reverb { get; set; }
        public FilterData Filter { get; set; }
        public DistortionData Distortion { get; set; }
        public ChorusData Chorus { get; set; }
        public float Tempo { get; set; }
        public double FadeInSeconds { get; set; }
        public double FadeOutSeconds { get; set; }
        public AutomationPoint[] VolumeAutomation { get; set; }
        public AutomationPoint[] PanAutomation { get; set; }
        public Dictionary<string, AutomationPoint[]> FxAutomation { get; set; }
        public MidiNoteData[] MidiNotes { get; set; }
        public List<double> KickMarkers { get; set; }
    }

    public class MidiNoteData
    {
        public int NoteNumber { get; set; }
        public double StartBeat { get; set; }
        public double DurationBeats { get; set; }
        public int Velocity { get; set; }
    }

    public class DelayData 
    { 
        public int TimeMs { get; set; } 
        public float Feedback { get; set; } 
        public float Mix { get; set; } 
    }
    
    public class CompressorData 
    { 
        public float Threshold { get; set; } 
        public float Ratio { get; set; } 
        public float Attack { get; set; } 
        public float Release { get; set; } 
    }
    public class ReverbData { public float Mix { get; set; } public float Size { get; set; } }
    public class FilterData { public int Type { get; set; } public float Cutoff { get; set; } }
    public class LimiterData { public float Threshold { get; set; } public float Ratio { get; set; } public float Attack { get; set; } public float Release { get; set; } }
    public class DistortionData { public float Drive { get; set; } public float Mix { get; set; } }
    public class ChorusData { public float Mix { get; set; } public float Depth { get; set; } public float Rate { get; set; } }
}