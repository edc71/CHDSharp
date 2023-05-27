using System;
using System.Collections.Generic;

namespace CHDReaderTest.Flac.FlacDeps
{
    public interface IAudioSource
    {
        IAudioDecoderSettings Settings { get; }

        AudioPCMConfig PCM { get; }

        TimeSpan Duration { get; }
        long Length { get; }
        long Position { get; set; }
        long Remaining { get; }

        int Read(AudioBuffer buffer, int maxLength);
        void Close();
    }

    public interface IAudioTitle
    {
        List<TimeSpan> Chapters { get; }
        AudioPCMConfig PCM { get; }
        string Codec { get; }
        string Language { get; }
        int StreamId { get; }
        //IAudioSource Open { get; }
    }

    public interface IAudioTitleSet
    {
        List<IAudioTitle> AudioTitles { get; }
    }

    public class SingleAudioTitle : IAudioTitle
    {
        public SingleAudioTitle(IAudioSource source) { this.source = source; }
        public List<TimeSpan> Chapters => new List<TimeSpan> { TimeSpan.Zero, source.Duration };
        public AudioPCMConfig PCM => source.PCM;
        public string Codec => source.Settings.Extension;
        public string Language => "";
        public int StreamId => 0;
        IAudioSource source;
    }

    public class SingleAudioTitleSet : IAudioTitleSet
    {
        public SingleAudioTitleSet(IAudioSource source) { this.source = source; }
        public List<IAudioTitle> AudioTitles => new List<IAudioTitle> { new SingleAudioTitle(source) };
        IAudioSource source;
    }
}
