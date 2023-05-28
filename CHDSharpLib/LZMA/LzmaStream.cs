using System;
using System.IO;
using CHDSharpLib.Utils;
using Compress.Support.Compression.LZ;

namespace Compress.Support.Compression.LZMA
{
    public class LzmaStream
    {
        private Stream inputStream;
        private long inputSize;
        private long outputSize;

        private int dictionarySize;
        private OutWindow outWindow = new OutWindow();
        private RangeCoder.Decoder rangeDecoder = new RangeCoder.Decoder();
        private Decoder decoder;

        private long position = 0;
        private bool endReached = false;
        private long availableBytes;
        private long rangeDecoderLimit;
        private long inputPosition = 0;

        // LZMA2
        private byte[] props = new byte[5];

        //private Encoder encoder;

        public LzmaStream(byte[] properties, Stream inputStream, ArrayPool arrBlockSize)
            : this(properties, inputStream, -1, -1, arrBlockSize)
        {
        }

        public LzmaStream(byte[] properties, Stream inputStream, long inputSize, long outputSize, ArrayPool arrBlockSize)
        {
            this.inputStream = inputStream;
            this.inputSize = inputSize;
            this.outputSize = outputSize;

            dictionarySize = BitConverter.ToInt32(properties, 1);
            outWindow.Create(dictionarySize, arrBlockSize);

            rangeDecoder.Init(inputStream);

            decoder = new Decoder();
            decoder.SetDecoderProperties(properties);

            availableBytes = outputSize < 0 ? long.MaxValue : outputSize;
            rangeDecoderLimit = inputSize;
        }

        public void Dispose()
        {

            outWindow.Dispose();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (endReached)
                return 0;

            int total = 0;
            while (total < count)
            {
                if (availableBytes == 0)
                {
                    break;
                }

                int toProcess = count - total;
                if (toProcess > availableBytes)
                    toProcess = (int)availableBytes;

                outWindow.SetLimit(toProcess);
                if (decoder.Code(dictionarySize, outWindow, rangeDecoder)
                         && outputSize < 0)
                {
                    availableBytes = outWindow.AvailableBytes;
                }

                int read = outWindow.Read(buffer, offset, toProcess);
                total += read;
                offset += read;
                position += read;
                availableBytes -= read;

                if (availableBytes == 0)
                {
                    rangeDecoder.ReleaseStream();
                    if (!rangeDecoder.IsFinished || (rangeDecoderLimit >= 0 && rangeDecoder.Total != rangeDecoderLimit))
                        throw new DataErrorException();
                    inputPosition += rangeDecoder.Total;
                    if (outWindow.HasPending)
                        throw new DataErrorException();
                }
            }

            if (endReached)
            {
                if (inputSize >= 0 && inputPosition != inputSize)
                    throw new DataErrorException();
                if (outputSize >= 0 && position != outputSize)
                    throw new DataErrorException();
            }

            return total;
        }
    }
}
