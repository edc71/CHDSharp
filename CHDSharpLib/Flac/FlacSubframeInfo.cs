using System;
using CHDReaderTest.Flac.FlacDeps;

namespace CUETools.Codecs.Flake
{
    unsafe public class FlacSubframeInfo
    {
        public FlacSubframeInfo()
        {
            best = new FlacSubframe();
            sf = new LpcSubframeInfo();
            best_fixed = new ulong[5];
            lpc_ctx = new LpcContext[lpc.MAX_LPC_WINDOWS];
            for (int i = 0; i < lpc.MAX_LPC_WINDOWS; i++)
                lpc_ctx[i] = new LpcContext();
        }

        public FlacSubframe best;
        public int obits;
        public int wbits;
        public int* samples;
        public uint done_fixed;
        public ulong[] best_fixed;
        public LpcContext[] lpc_ctx;
        public LpcSubframeInfo sf;
    };
}
