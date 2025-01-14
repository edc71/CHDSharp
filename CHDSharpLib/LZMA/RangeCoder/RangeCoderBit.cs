using System;

namespace Compress.Support.Compression.RangeCoder
{
    internal struct BitDecoder
    {
        public const int kNumBitModelTotalBits = 11;
        public const uint kBitModelTotal = (1 << kNumBitModelTotalBits);
        const int kNumMoveBits = 5;

        uint Prob;

        public void Init() { Prob = kBitModelTotal >> 1; }

        public uint Decode(RangeCoder.Decoder rangeDecoder)
        {
            unchecked
            {
                uint newBound = (uint)(rangeDecoder.Range >> kNumBitModelTotalBits) * (uint)Prob;
                if (rangeDecoder.Code < newBound)
                {
                    rangeDecoder.Range = newBound;
                    Prob += (kBitModelTotal - Prob) >> kNumMoveBits;
                    if (rangeDecoder.Range < Decoder.kTopValue)
                    {
                        rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                        rangeDecoder.Range <<= 8;
                        rangeDecoder.Total++;
                    }
                    return 0;
                }
                else
                {
                    rangeDecoder.Range -= newBound;
                    rangeDecoder.Code -= newBound;
                    Prob -= (Prob) >> kNumMoveBits;
                    if (rangeDecoder.Range < Decoder.kTopValue)
                    {
                        rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                        rangeDecoder.Range <<= 8;
                        rangeDecoder.Total++;
                    }
                    return 1;
                }
            }
        }
    }
}