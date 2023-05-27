using System;

namespace Compress.Support.Compression.RangeCoder
{
    internal class Decoder
    {
        public const uint kTopValue = (1 << 24);
        public uint Range;
        public uint Code = 0;
        // public Buffer.InBuffer Stream = new Buffer.InBuffer(1 << 16);
        public System.IO.Stream Stream;
        public long Total;

        public void Init(System.IO.Stream stream)
        {
            unchecked
            {
                // Stream.Init(stream);
                Stream = stream;

                Code = 0;
                Range = 0xFFFFFFFF;
                for (int i = 0; i < 5; i++)
                    Code = (Code << 8) | (byte)Stream.ReadByte();
                Total = 5;
            }
        }

        public void ReleaseStream()
        {
            // Stream.ReleaseStream();
            Stream = null;
        }

        public void CloseStream()
        {
            Stream.Dispose();
        }

        public uint DecodeDirectBits(int numTotalBits)
        {
            unchecked
            {
                uint range = Range;
                uint code = Code;
                uint result = 0;
                for (int i = numTotalBits; i > 0; i--)
                {
                    range >>= 1;

                    uint t = (code - range) >> 31;
                    code -= range & (t - 1);
                    result = (result << 1) | (1 - t);

                    if (range < kTopValue)
                    {
                        code = (code << 8) | (byte)Stream.ReadByte();
                        range <<= 8;
                        Total++;
                    }
                }
                Range = range;
                Code = code;
                return result;
            }
        }

        public bool IsFinished
        {
            get
            {
                return Code == 0;
            }
        }
    }
}
