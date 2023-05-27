using CHDReaderTest.Flac.FlacDeps;

namespace CUETools.Codecs.Flake
{
    unsafe public class FlacFrame
    {
        public int blocksize;
        public int bs_code0, bs_code1;
        public ChannelMode ch_mode;
        //public int ch_order0, ch_order1;
        public byte crc8;
        public FlacSubframeInfo[] subframes;
        public int frame_number;
        public FlacSubframe current;
        public float* window_buffer;
        public int nSeg = 0;

        public BitWriter writer = null;
        public int writer_offset = 0;

        public FlacFrame(int subframes_count)
        {
            subframes = new FlacSubframeInfo[subframes_count];
            for (int ch = 0; ch < subframes_count; ch++)
                subframes[ch] = new FlacSubframeInfo();
            current = new FlacSubframe();
        }
    }
}
