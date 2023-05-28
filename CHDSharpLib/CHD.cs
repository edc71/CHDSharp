using CHDReaderTest.Flac.FlacDeps;
using CHDSharpLib.Utils;
using Compress.Support.Compression.LZMA;
using CUETools.Codecs.Flake;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CHDSharpLib;

public class CHDHeader
{
    public chd_codec[] compression;

    public ulong totalbytes;
    public uint blocksize;
    public uint totalblocks;

    public mapentry[] map;

    public byte[] md5; // just compressed data
    public byte[] rawsha1; // just compressed data
    public byte[] sha1; // includes the meta data
    public ulong metaoffset;
}

public class PreLoadBlockHelper
{
    public int block;
    public int UseCount;
}

public class mapentry
{
    public compression_type comptype;
    public uint length; // length of compressed data
    public ulong offset; // offset of compressed data in file. Also index of source block for COMPRESSION_SELF 
    public uint? crc = null; // V3 & V4
    public ushort? crc16 = null; // V5

    public mapentry selfMapEntry; // link to self mapentry data used in COMPRESSION_SELF (replaces offset index)

    //Used to optimmize block reading so that any block in only decompressed once.
    public int UseCount;

    //public byte[] buffIn = null;
    public byte[] buffOutCache = null;
    public byte[] buffOut = null;

    // Used in Parallel decompress to keep the blocks in order when hashing.
    public bool Processed = false;

}


public class CHD
{

    //private int dedupe_usage_treshold = 25;

    public void TestCHD(CHD chd, string filename, int tasks)
    {
        //Console.WriteLine("");
        //Console.WriteLine($"Testing :{filename}");
        using (Stream s = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 32768))
        {
            if (!CheckHeader(s, out uint length, out uint version))
                return;

            //Console.WriteLine($@"CHD Version {version}");

            chd_error valid = chd_error.CHDERR_INVALID_DATA;
            CHDHeaders chdheaders = new CHDHeaders(); ; 
            CHDHeader chdheader;

            switch (version)
            {
                case 1:
                    valid = chdheaders.ReadHeaderV1(s, out chdheader);
                    break;
                case 2:
                    valid = chdheaders.ReadHeaderV2(s, out chdheader);
                    break;
                case 3:
                    valid = chdheaders.ReadHeaderV3(s, out chdheader);
                    break;
                case 4:
                    valid = chdheaders.ReadHeaderV4(s, out chdheader);
                    break;
                case 5:
                    valid = chdheaders.ReadHeaderV5(s, out chdheader);
                    break;
                default:
                    Console.WriteLine($"Unknown version {version}");
                    return;
            }
            if (valid != chd_error.CHDERR_NONE)
            {
                SendMessage($"Error Reading Header: {valid}", ConsoleColor.Red);
            }

            //if (((ulong)chd.totalblocks * (ulong)chd.blocksize) != chd.totalbytes)
            //{
            //    SendMessage($"{(ulong)chd.totalblocks * (ulong)chd.blocksize} != {chd.totalbytes}", ConsoleColor.Cyan);
            //}

            FindRepeatedBlocks(chdheader,s);

            valid = DecompressDataParallel(chd, s, chdheader, tasks);
            if (valid != chd_error.CHDERR_NONE)
            {
                Console.WriteLine("File: " + filename);
                SendMessage($"Data Decompress Failed: {valid}", ConsoleColor.Red);
                Console.WriteLine("key");
                Console.Read();
                return;
            }

            valid = CHDMetaData.ReadMetaData(s, chdheader);

            if (valid != chd_error.CHDERR_NONE)
            {
                SendMessage($"Meta Data Failed: {valid}", ConsoleColor.Red);
                return;
            }

            Interlocked.Increment(ref CHDCommon.processedfiles);

            //SendMessage($"Valid", ConsoleColor.Green);
        }
    }

    private void SendMessage(string msg, ConsoleColor cc)
    {
        ConsoleColor consoleColor = Console.ForegroundColor;
        Console.ForegroundColor = cc;
        Console.WriteLine(msg);
        Console.ForegroundColor = consoleColor;
    }

    private readonly uint[] HeaderLengths = new uint[] { 0, 76, 80, 120, 108, 124 };
    private readonly byte[] id = { (byte)'M', (byte)'C', (byte)'o', (byte)'m', (byte)'p', (byte)'r', (byte)'H', (byte)'D' };

    public bool CheckHeader(Stream file, out uint length, out uint version)
    {
        for (int i = 0; i < id.Length; i++)
        {
            byte b = (byte)file.ReadByte();
            if (b != id[i])
            {
                length = 0;
                version = 0;
                return false;
            }
        }

        using (BinaryReader br = new BinaryReader(file, Encoding.UTF8, true))
        {
            length = br.ReadUInt32BE();
            version = br.ReadUInt32BE();
            return HeaderLengths[version] == length;
        }
    }

    public chd_error DecompressDataParallel(CHD chd, Stream file, CHDHeader chdheader, int tasks)
    {
        ArrayPool arrBlockSize = new ArrayPool(chdheader.blocksize);
        //using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        CHDCodec preloadcodec = new CHDCodec();
        chd_error err = PreLoadRepeatedBlocks(chd, chdheader, file, arrBlockSize, preloadcodec);

        using MD5 md5Check = chdheader.md5 != null ? MD5.Create() : null;
        using SHA1 sha1Check = chdheader.rawsha1 != null ? SHA1.Create() : null;

        int taskCount = tasks;
        BlockingCollection<int> bCollection = new BlockingCollection<int>(boundedCapacity: taskCount * 5);
        BlockingCollection<int> bCleanup = new BlockingCollection<int>(boundedCapacity: taskCount * 5);
        chd_error errMaster = chd_error.CHDERR_NONE;

        Task producerThread = Task.Factory.StartNew(() =>
        {
            for (int block = 0; block < chdheader.totalblocks; block++)
            {
                if (errMaster != chd_error.CHDERR_NONE)
                    break;

                mapentry mapentry = chdheader.map[block];
                bCollection.Add(block);
            }
            for (int i = 0; i < taskCount; i++)
                bCollection.Add(-1);
        });

        Task[] consumerThread = new Task[taskCount];
        
        for (int i = 0; i < taskCount; i++)
        {
            consumerThread[i] = Task.Factory.StartNew((i) =>
            {
                CHDCodec codec = new CHDCodec();

                while (true)
                {
                    int block = bCollection.Take();
                    if (block == -1)
                        return;
                    mapentry mapEntry = chdheader.map[block];
                    byte[] buffOut = arrBlockSize.Rent();
                    chd_error err = ReadBlock(arrBlockSize, chd, file, mapEntry, chdheader.compression, codec, (int)chdheader.blocksize, false, ref buffOut);
                    mapEntry.buffOut = buffOut;
                    if (err != chd_error.CHDERR_NONE)
                    {
                        bCollection.TryTake(out int tmpitem, 1);
                        bCleanup.Add(-1);
                        errMaster = err;
                        return;
                    }
                    bCleanup.Add(block);

                    long tmp = GC.GetTotalMemory(false);
                    if (tmp > CHDCommon.maxmem) { CHDCommon.maxmem = tmp; }

                }
            }, i);
        }

        ulong sizetoGo = chdheader.totalbytes;
        int proc = 0;
        Task cleanupThread = Task.Factory.StartNew(() =>
        {
            while (true)
            {
                int item = bCleanup.Take();
                if (item == -1)
                    return;

                chdheader.map[item].Processed = true;
                while (chdheader.map[proc].Processed == true)
                {
                    int sizenext = sizetoGo > (ulong)chdheader.blocksize ? (int)chdheader.blocksize : (int)sizetoGo;

                    mapentry mapentry = chdheader.map[proc];

                    md5Check?.TransformBlock(mapentry.buffOut, 0, sizenext, null, 0);
                    sha1Check?.TransformBlock(mapentry.buffOut, 0, sizenext, null, 0);

                    arrBlockSize.Return(mapentry.buffOut);
                    mapentry.buffOut = null;

                    /* prepare for the next block */
                    sizetoGo -= (ulong)sizenext;

                    proc++;
                    if (proc == chdheader.totalblocks)
                        break;
                }
                if (proc == chdheader.totalblocks)
                    break;
            }
        });

        Task.WaitAll(cleanupThread);

        //Console.WriteLine($"Verifying, 100% complete.");
        
        arrBlockSize.Destroy();
        arrBlockSize = null;

        if (errMaster != chd_error.CHDERR_NONE)
            return errMaster;

        byte[] tmp = new byte[0];
        md5Check?.TransformFinalBlock(tmp, 0, 0);
        sha1Check?.TransformFinalBlock(tmp, 0, 0);

        // here it is now using the rawsha1 value from the header to validate the raw binary data.
        if (chdheader.md5 != null && !Util.ByteArrEquals(chdheader.md5, md5Check.Hash))
        {
            return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }
        if (chdheader.rawsha1 != null && !Util.ByteArrEquals(chdheader.rawsha1, sha1Check.Hash))
        {
            return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }

        return chd_error.CHDERR_NONE;
    }

    //maximum mem used for caching dupe blocks
    const ulong maxbuffersize = 1 * 1024 * 1024 * 1024;
    
    //private ulong totalbuffersize = 0;

    private chd_error PreLoadRepeatedBlocks(CHD chd, CHDHeader chdr, Stream file, ArrayPool arrBlockSize, CHDCodec codec)
    {
        List<PreLoadBlockHelper> list = new List<PreLoadBlockHelper>();
        for (int i = 0; i < chdr.map.Length; i++)
            if (chdr.map[i].UseCount > 0)
                list.Add(new PreLoadBlockHelper() { block = i, UseCount = chdr.map[i].UseCount });
        
        codec.totalbuffersize = 0;

        foreach (PreLoadBlockHelper dupe in list.OrderByDescending(x => x.UseCount))
        {
            if (codec.totalbuffersize + chdr.blocksize > maxbuffersize)
                break;

            mapentry me = chdr.map[dupe.block];
            byte[] buffOut = arrBlockSize.Rent();
            chd_error err = ReadBlock(arrBlockSize, chd, file, me, chdr.compression, codec, (int)chdr.blocksize, true, ref buffOut);
            if (err != chd_error.CHDERR_NONE)
                return err;

            me.buffOutCache = buffOut;
            codec.totalbuffersize += chdr.blocksize;
        }

        return chd_error.CHDERR_NONE;
    }

    private void FindRepeatedBlocks(CHDHeader chdr, Stream file)
    {
        int totalFound = 0;
        Parallel.ForEach(chdr.map, me =>
        {
            CHDCodec codec = new CHDCodec();
            if (me.comptype != compression_type.COMPRESSION_SELF)
                return;

            me.selfMapEntry = chdr.map[me.offset];
            switch (me.selfMapEntry.comptype)
            {
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:
                case compression_type.COMPRESSION_NONE:
                    break;
                default:
                    Console.WriteLine($"Error {me.selfMapEntry.comptype}");
                    break;
            }
            Interlocked.Increment(ref me.selfMapEntry.UseCount);
            Interlocked.Increment(ref totalFound);
        });

    }

    public chd_error ReadBlock(ArrayPool arrBlockSize,CHD chd, Stream file, mapentry mapEntry, chd_codec[] compression, CHDCodec codec, int buffOutLength, bool preload,  ref byte[] buffOut)
    {
        try
        {
            byte[] buffIn = null;
            bool checkCrc = true;
            uint blockSize = (uint)buffOutLength;
            if (mapEntry.length > 0)
            {
                lock (file)
                {
                    buffIn = arrBlockSize.Rent();
                    file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                    file.Read(buffIn, 0, (int)mapEntry.length);
                    Interlocked.Add(ref CHDCommon.processedsize, mapEntry.length);
                }
            }

            switch (mapEntry.comptype)
            {
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:
                    {
                        lock (mapEntry)
                        {
                            if (mapEntry.buffOutCache == null)
                            {
                                chd_error ret = chd_error.CHDERR_UNSUPPORTED_FORMAT;
                                switch (compression[(int)mapEntry.comptype])
                                {
                                    case chd_codec.CHD_CODEC_ZLIB:
                                        ret = zlib(buffIn, buffOut, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    case chd_codec.CHD_CODEC_LZMA:
                                        ret = lzma(arrBlockSize, buffIn, buffOut, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    case chd_codec.CHD_CODEC_HUFFMAN:
                                        ret = huffman(buffIn, buffOut, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    case chd_codec.CHD_CODEC_FLAC:
                                        ret = flac(buffIn, buffOut, codec, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_ZLIB:
                                        ret = cdzlib(arrBlockSize, buffIn, buffOut, (int)mapEntry.length, (int)blockSize, codec);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_LZMA:
                                        ret = cdlzma(arrBlockSize, buffIn, buffOut, (int)mapEntry.length, (int)blockSize, codec);
                                        break;
                                    case chd_codec.CHD_CODEC_CD_FLAC:
                                        ret = cdflac(arrBlockSize, buffIn, buffOut, codec, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    case chd_codec.CHD_CODEC_AVHUFF:
                                        ret = avHuff(buffIn, buffOut, codec, (int)mapEntry.length, (int)blockSize);
                                        break;
                                    default:
                                        Console.WriteLine("Unknown compression type");
                                        break;
                                }

                                if (ret != chd_error.CHDERR_NONE)
                                    return ret;

                                // if this block is re-used keep a copy of it (if enough mem available).
                                if (mapEntry.UseCount > 0 && !preload && codec.totalbuffersize + blockSize < maxbuffersize)
                                {
                                    mapEntry.buffOutCache = arrBlockSize.Rent();
                                    Array.Copy(buffOut, 0, mapEntry.buffOutCache, 0, blockSize);
                                    codec.totalbuffersize += blockSize;
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref CHDCommon.repeatedblocks);
                                Array.Copy(mapEntry.buffOutCache, 0, buffOut, 0, (int)blockSize);
                                Interlocked.Decrement(ref mapEntry.UseCount);

                                if (mapEntry.UseCount == 0)
                                {
                                    arrBlockSize.Return(mapEntry.buffOutCache);
                                    mapEntry.buffOutCache = null;
                                    codec.totalbuffersize -= blockSize;
                                }

                                checkCrc = false;
                            }
                        }
                        break;

                    }
                case compression_type.COMPRESSION_NONE:
                    {
                        lock (mapEntry)
                        {
                            if (mapEntry.buffOutCache == null)
                            {
                                Interlocked.Increment(ref CHDCommon.repeatedblocks);
                                Array.Copy(buffIn, buffOut, buffOutLength);

                                if (mapEntry.UseCount > 0 && !preload && codec.totalbuffersize + blockSize < maxbuffersize)
                                {
                                    mapEntry.buffOutCache = arrBlockSize.Rent();
                                    Array.Copy(buffOut, 0, mapEntry.buffOutCache, 0, blockSize);
                                    codec.totalbuffersize += blockSize;
                                }
                            }
                            else
                            {
                                Array.Copy(mapEntry.buffOutCache, 0, buffOut, 0, (int)blockSize);
                                Interlocked.Decrement(ref mapEntry.UseCount);
                                if (mapEntry.UseCount == 0)
                                {
                                    arrBlockSize.Return(mapEntry.buffOutCache);
                                    mapEntry.buffOutCache = null;
                                    codec.totalbuffersize -= blockSize;
                                }
                                checkCrc = false;
                            }
                        }
                        break;

                    }

                case compression_type.COMPRESSION_MINI:
                    {
                        Array.Clear(buffOut, 0, (int)blockSize);
                        byte[] tmp = BitConverter.GetBytes(mapEntry.offset);
                        for (int i = 0; i < 8; i++)
                        {
                            buffOut[i] = tmp[7 - i];
                        }

                        for (int i = 8; i < blockSize; i++)
                        {
                            buffOut[i] = buffOut[i - 8];
                        }

                        break;
                    }

                case compression_type.COMPRESSION_SELF:
                    {
                        chd_error retcs = ReadBlock(arrBlockSize, chd, file, mapEntry.selfMapEntry, compression, codec, buffOutLength, false, ref buffOut);
                        if (retcs != chd_error.CHDERR_NONE)
                            return retcs;
                        checkCrc = false;
                        break;
                    }
                default:
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;

            }

            if (buffIn != null)
            {
                arrBlockSize.Return(buffIn);
                buffIn = null;
            }

            if (checkCrc)
            {
                if (mapEntry.crc != null && !CRC.VerifyDigest((uint)mapEntry.crc, buffOut, 0, blockSize))
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
                if (mapEntry.crc16 != null && CRC16.calc(buffOut, (int)blockSize) != mapEntry.crc16)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
            }
        }
        catch (System.Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            Console.WriteLine((compression_type)mapEntry.comptype);
            buffOut = null;
            return chd_error.CHDERR_CODEC_ERROR;
        }

        return chd_error.CHDERR_NONE;
    }
    internal chd_error zlib(byte[] buffIn, byte[] buffOut, int buffInLength, int buffOutLength)
    {
        return zlib(buffIn, 0, buffInLength, buffOut, buffOutLength);
    }
    internal chd_error zlib(byte[] buffIn, int start, int compsize, byte[] buffOut, int buffOutLength)
    {
        MemoryStream memStream = null;
        try
        {
            memStream = new MemoryStream(buffIn, start, compsize);
        }
        catch (System.Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            Console.WriteLine(buffIn.Length);
            Console.WriteLine(start);
            Console.WriteLine(compsize);
        }
        using var compStream = new DeflateStream(memStream, CompressionMode.Decompress, true);
        int bytesRead = 0;
        while (bytesRead < buffOutLength)
        {
            int bytes = compStream.Read(buffOut, bytesRead, buffOutLength - bytesRead);
            if (bytes == 0)
                return chd_error.CHDERR_INVALID_DATA;
            bytesRead += bytes;
        }

        return chd_error.CHDERR_NONE;
    }


    internal chd_error lzma(ArrayPool arrBlockSize, byte[] buffIn, byte[] buffOut, int buffInLength, int buffOutLength)
    {
        return lzma(arrBlockSize, buffIn, 0, buffInLength, buffOut, buffOutLength);
    }

    internal chd_error lzma(ArrayPool arrBlockSize, byte[] buffIn, int start, int compsize, byte[] buffOut, int buffOutLength)
    {
        //hacky header creator
        byte[] properties = new byte[5];
        int posStateBits = 2;
        int numLiteralPosStateBits = 0;
        int numLiteralContextBits = 3;
        int dictionarySize = buffOutLength;
        properties[0] = (byte)((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
        for (int j = 0; j < 4; j++)
            properties[1 + j] = (Byte)((dictionarySize >> (8 * j)) & 0xFF);

        using var memStream = new MemoryStream(buffIn, start, compsize);
        LzmaStream compStream = new LzmaStream(properties, memStream, arrBlockSize);
        int bytesRead = 0;
        while (bytesRead < buffOutLength)
        {
            int bytes = compStream.Read(buffOut, bytesRead, buffOutLength - bytesRead);
            if (bytes == 0)
                return chd_error.CHDERR_INVALID_DATA;
            bytesRead += bytes;
        }

        compStream.Dispose();
        
        return chd_error.CHDERR_NONE;
    }

    internal chd_error huffman(byte[] buffIn, byte[] buffOut, int buffInLength, int buffOutLength)
    {
        BitStream bitbuf = new BitStream(buffIn);
        HuffmanDecoder hd = new HuffmanDecoder(256, 16, bitbuf);

        if (hd.ImportTreeHuffman() != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;

        for (int j = 0; j < buffOutLength; j++)
        {
            buffOut[j] = (byte)hd.DecodeOne();
        }

        return chd_error.CHDERR_NONE;
    }


    internal chd_error flac(byte[] buffIn, byte[] buffOut, CHDCodec codec, int buffInLength, int buffOutLength)
    {
        byte endianType = buffIn[0];
        //CHD adds a leading char to indicate endian. Not part of the flac format.
        bool swapEndian = (endianType == 'B'); //'L'ittle / 'B'ig
        return flac(buffIn, 1, buffOut, swapEndian, codec, buffInLength, buffOutLength, out _);
    }


    internal chd_error flac(byte[] buffIn, int start, byte[] buffOut, bool swapEndian, CHDCodec codec, int buffInLength, int buffOutLength, out int srcPos)
    {
        codec.FLAC_settings ??= new AudioPCMConfig(16, 2, 44100);
        codec.FLAC_audioDecoder ??= new AudioDecoder(codec.FLAC_settings);
        codec.FLAC_audioBuffer ??= new AudioBuffer(codec.FLAC_settings, buffOutLength); //audio buffer to take decoded samples and read them to bytes.

        srcPos = start;
        int dstPos = 0;
        //this may require some error handling. Hopefully the while condition is reliable
        while (dstPos < buffOutLength)
        {
            int read = codec.FLAC_audioDecoder.DecodeFrame(buffIn, srcPos, buffInLength - srcPos);
            codec.FLAC_audioDecoder.Read(codec.FLAC_audioBuffer, (int)codec.FLAC_audioDecoder.Remaining);
            Array.Copy(codec.FLAC_audioBuffer.Bytes, 0, buffOut, dstPos, codec.FLAC_audioBuffer.ByteLength);
            dstPos += codec.FLAC_audioBuffer.ByteLength;
            srcPos += read;
        }

        //Nanook - hack to support 16bit byte flipping - tested passes hunk CRC test
        if (swapEndian)
        {
            byte tmp;
            for (int i = 0; i < buffOutLength; i += 2)
            {
                tmp = buffOut[i];
                buffOut[i] = buffOut[i + 1];
                buffOut[i + 1] = tmp;
            }
        }

        return chd_error.CHDERR_NONE;
    }

    /******************* CD decoders **************************/

    private const int CD_MAX_SECTOR_DATA = 2352;
    private const int CD_MAX_SUBCODE_DATA = 96;
    private static readonly int CD_FRAME_SIZE = CD_MAX_SECTOR_DATA + CD_MAX_SUBCODE_DATA;

    private static readonly byte[] s_cd_sync_header = new byte[] { 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 };

    internal chd_error cdzlib(ArrayPool arrBlockSize, byte[] buffIn, byte[] buffOut, int buffInLength, int buffOutLength, CHDCodec codec)
    {
        /* determine header bytes */
        int frames = buffOutLength / CD_FRAME_SIZE;
        int complen_bytes = (buffOutLength < 65536) ? 2 : 3;
        int ecc_bytes = (frames + 7) / 8;
        int header_bytes = ecc_bytes + complen_bytes;

        /* extract compressed length of base */
        int complen_base = (buffIn[ecc_bytes + 0] << 8) | buffIn[ecc_bytes + 1];
        if (complen_bytes > 2)
            complen_base = (complen_base << 8) | buffIn[ecc_bytes + 2];

        //byte[] bSector = ArrayPool<byte>.Shared.Rent(frames * CD_MAX_SECTOR_DATA);
        //byte[] bSubcode = ArrayPool<byte>.Shared.Rent(frames * CD_MAX_SUBCODE_DATA);
        codec.bSector ??= new byte[frames * CD_MAX_SECTOR_DATA];
        codec.bSubcode ??= new byte[frames * CD_MAX_SUBCODE_DATA];

        chd_error err = zlib(buffIn, (int)header_bytes, complen_base, codec.bSector, frames * CD_MAX_SECTOR_DATA);
        if (err != chd_error.CHDERR_NONE)
        {
            return err;
        }

        err = zlib(buffIn, header_bytes + complen_base, buffInLength - header_bytes - complen_base, codec.bSubcode, frames * CD_MAX_SUBCODE_DATA);
        if (err != chd_error.CHDERR_NONE)
        {
            return err;
        }

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(codec.bSector, framenum * CD_MAX_SECTOR_DATA, buffOut, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(codec.bSubcode, framenum * CD_MAX_SUBCODE_DATA, buffOut, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

            // reconstitute the ECC data and sync header 
            int sectorStart = framenum * CD_FRAME_SIZE;
            if ((buffIn[framenum / 8] & (1 << (framenum % 8))) != 0)
            {
                Array.Copy(s_cd_sync_header, 0, buffOut, sectorStart, s_cd_sync_header.Length);
                cdRom.ecc_generate(buffOut, sectorStart);
            }
        }
        return chd_error.CHDERR_NONE;
    }

    internal chd_error cdlzma(ArrayPool arrBlockSize, byte[] buffIn, byte[] buffOut, int buffInLength, int buffOutLength, CHDCodec codec)
    {
        /* determine header bytes */
        int frames = buffOutLength / CD_FRAME_SIZE;
        int complen_bytes = (buffOutLength < 65536) ? 2 : 3;
        int ecc_bytes = (frames + 7) / 8;
        int header_bytes = ecc_bytes + complen_bytes;

        /* extract compressed length of base */
        int complen_base = ((buffIn[ecc_bytes + 0] << 8) | buffIn[ecc_bytes + 1]);
        if (complen_bytes > 2)
            complen_base = (complen_base << 8) | buffIn[ecc_bytes + 2];

        codec.bSector ??= new byte[frames * CD_MAX_SECTOR_DATA];
        codec.bSubcode ??= new byte[frames * CD_MAX_SUBCODE_DATA];

        //byte[] bSector = ArrayPool<byte>.Shared.Rent(frames * CD_MAX_SECTOR_DATA);
        //byte[] bSubcode = ArrayPool<byte>.Shared.Rent(frames * CD_MAX_SUBCODE_DATA);

        chd_error err = lzma(arrBlockSize, buffIn, header_bytes, complen_base, codec.bSector, frames * CD_MAX_SECTOR_DATA);
        if (err != chd_error.CHDERR_NONE)
        {
            return err;
        }

        err = zlib(buffIn, header_bytes + complen_base, buffInLength - header_bytes - complen_base, codec.bSubcode, frames * CD_MAX_SUBCODE_DATA);
        if (err != chd_error.CHDERR_NONE)
        {
            return err;
        }

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(codec.bSector, framenum * CD_MAX_SECTOR_DATA, buffOut, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(codec.bSubcode, framenum * CD_MAX_SUBCODE_DATA, buffOut, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

            // reconstitute the ECC data and sync header 
            int sectorStart = framenum * CD_FRAME_SIZE;
            if ((buffIn[framenum / 8] & (1 << (framenum % 8))) != 0)
            {
                Array.Copy(s_cd_sync_header, 0, buffOut, sectorStart, s_cd_sync_header.Length);
                cdRom.ecc_generate(buffOut, sectorStart);
            }
        }
        return chd_error.CHDERR_NONE;
    }

    internal chd_error cdflac(ArrayPool arrBlockSize, byte[] buffIn, byte[] buffOut, CHDCodec codec, int buffInLength, int buffOutLength)
    {
        int frames = buffOutLength / CD_FRAME_SIZE;

        codec.bSector ??= new byte[frames * CD_MAX_SECTOR_DATA];
        codec.bSubcode ??= new byte[frames * CD_MAX_SUBCODE_DATA];

        chd_error err = flac(buffIn, 0, codec.bSector, true, codec, buffInLength, frames * CD_MAX_SECTOR_DATA, out int pos);
        if (err != chd_error.CHDERR_NONE)
            return err;

        err = zlib(buffIn, pos, buffInLength - pos, codec.bSubcode, frames * CD_MAX_SUBCODE_DATA);
        if (err != chd_error.CHDERR_NONE)
        {
            return err;
        }

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(codec.bSector, framenum * CD_MAX_SECTOR_DATA, buffOut, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(codec.bSubcode, framenum * CD_MAX_SUBCODE_DATA, buffOut, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);
        }
        return chd_error.CHDERR_NONE;
    }
    /*
        Source input buffer structure:

        Header:
        00     =  Size of the Meta Data to be put into the output buffer right after the header.
        01     =  Number of Audio Channel.
        02,03  =  Number of Audio sampled values per chunk.
        04,05  =  width in pixels of image.
        06,07  =  height in pixels of image.
        08,09  =  Size of the source data for the audio channels huffman trees. (set to 0xffff is using FLAC.)

        10,11  =  size of compressed audio channel 1
        12,13  =  size of compressed audio channel 2
        .
        .         (Max audio channels coded to 16)
        Total Header size = 10 + 2 * Number of Audio Channels.


        Meta Data: (Size from header 00)

        Audio Huffman Tree: (Size from header 08,09)

        Audio Compressed Data Channels: (Repeated for each Audio Channel, Size from Header starting at 10,11)

        Video Compressed Data:   Rest of Input Chuck.

       */

    internal chd_error avHuff(byte[] buffIn, byte[] buffOut, CHDCodec codec, int buffInLength, int buffOutLength)
    {
        // extract info from the header
        if (buffInLength < 8)
            return chd_error.CHDERR_INVALID_DATA;
        uint metaDataLength = buffIn[0];
        uint audioChannels = buffIn[1];
        uint audioSamplesPerBlock = buffIn.ReadUInt16BE(2);
        uint videoWidth = buffIn.ReadUInt16BE(4);
        uint videoHeight = buffIn.ReadUInt16BE(6);

        uint sourceTotalSize = 10 + 2 * audioChannels;
        // validate that the sizes make sense
        if (buffInLength < sourceTotalSize)
            return chd_error.CHDERR_INVALID_DATA;

        sourceTotalSize += metaDataLength;

        uint audioHuffmanTreeSize = buffIn.ReadUInt16BE(8);
        if (audioHuffmanTreeSize != 0xffff)
            sourceTotalSize += audioHuffmanTreeSize;

        uint?[] audioChannelCompressedSize = new uint?[16];
        for (int chnum = 0; chnum < audioChannels; chnum++)
        {
            audioChannelCompressedSize[chnum] = buffIn.ReadUInt16BE(10 + 2 * chnum);
            sourceTotalSize += (uint)audioChannelCompressedSize[chnum];
        }

        if (sourceTotalSize >= buffInLength)
            return chd_error.CHDERR_INVALID_DATA;

        // starting offsets of source data
        uint buffInIndex = 10 + 2 * audioChannels;


        uint destOffset = 0;
        // create a header
        buffOut[0] = (byte)'c';
        buffOut[1] = (byte)'h';
        buffOut[2] = (byte)'a';
        buffOut[3] = (byte)'v';
        buffOut[4] = (byte)metaDataLength;
        buffOut[5] = (byte)audioChannels;
        buffOut[6] = (byte)(audioSamplesPerBlock >> 8);
        buffOut[7] = (byte)audioSamplesPerBlock;
        buffOut[8] = (byte)(videoWidth >> 8);
        buffOut[9] = (byte)videoWidth;
        buffOut[10] = (byte)(videoHeight >> 8);
        buffOut[11] = (byte)videoHeight;
        destOffset += 12;



        uint metaDestStart = destOffset;
        if (metaDataLength > 0)
        {
            Array.Copy(buffIn, (int)buffInIndex, buffOut, (int)metaDestStart, (int)metaDataLength);
            buffInIndex += metaDataLength;
            destOffset += metaDataLength;
        }

        uint?[] audioChannelDestStart = new uint?[16];
        for (int chnum = 0; chnum < audioChannels; chnum++)
        {
            audioChannelDestStart[chnum] = destOffset;
            destOffset += 2 * audioSamplesPerBlock;
        }
        uint videoDestStart = destOffset;


        // decode the audio channels
        if (audioChannels > 0)
        {
            // decode the audio
            chd_error err = DecodeAudio(audioChannels, audioSamplesPerBlock, buffIn, buffInIndex, audioHuffmanTreeSize, audioChannelCompressedSize, buffOut, audioChannelDestStart, codec);
            if (err != chd_error.CHDERR_NONE)
                return err;

            // advance the pointers past the data
            if (audioHuffmanTreeSize != 0xffff)
                buffInIndex += audioHuffmanTreeSize;
            for (int chnum = 0; chnum < audioChannels; chnum++)
                buffInIndex += (uint)audioChannelCompressedSize[chnum];
        }

        // decode the video data
        if (videoWidth > 0 && videoHeight > 0)
        {
            uint videostride = 2 * videoWidth;
            // decode the video
            chd_error err = decodeVideo(videoWidth, videoHeight, buffIn, buffInIndex, (uint)buffInLength - buffInIndex, buffOut, videoDestStart, videostride);
            if (err != chd_error.CHDERR_NONE)
                return err;
        }

        uint videoEnd = videoDestStart + videoWidth * videoHeight * 2;
        for (uint index = videoEnd; index < buffOutLength; index++)
            buffOut[index] = 0;

        return chd_error.CHDERR_NONE;
    }

    private chd_error DecodeAudio(uint channels, uint samples, byte[] buffIn, uint buffInOffset, uint treesize, uint?[] audioChannelCompressedSize, byte[] buffOut, uint?[] audioChannelDestStart, CHDCodec codec)
    {
        // if the tree size is 0xffff, the streams are FLAC-encoded
        if (treesize == 0xffff)
        {
            int blockSize = (int)samples * 2;

            // loop over channels
            for (int channelNumber = 0; channelNumber < channels; channelNumber++)
            {
                // extract the size of this channel
                uint sourceSize = audioChannelCompressedSize[channelNumber] ?? 0;

                uint? curdest = audioChannelDestStart[channelNumber];
                if (curdest != null)
                {
                    codec.AVHUFF_settings ??= new AudioPCMConfig(16, 1, 48000);
                    codec.AVHUFF_audioDecoder ??= new AudioDecoder(codec.AVHUFF_settings); //read the data and decode it in to a 1D array of samples - the buffer seems to want 2D :S
                    AudioBuffer audioBuffer = new AudioBuffer(codec.AVHUFF_settings, blockSize); //audio buffer to take decoded samples and read them to bytes.
                    int read;
                    int inPos = (int)buffInOffset;
                    int outPos = (int)audioChannelDestStart[channelNumber];

                    while (outPos < blockSize + audioChannelDestStart[channelNumber])
                    {
                        if ((read = codec.AVHUFF_audioDecoder.DecodeFrame(buffIn, inPos, (int)sourceSize)) == 0)
                            break;
                        if (codec.AVHUFF_audioDecoder.Remaining != 0)
                        {
                            codec.AVHUFF_audioDecoder.Read(audioBuffer, (int)codec.AVHUFF_audioDecoder.Remaining);
                            Array.Copy(audioBuffer.Bytes, 0, buffOut, outPos, audioBuffer.ByteLength);
                            outPos += audioBuffer.ByteLength;
                        }
                        inPos += read;
                    }

                    byte tmp;
                    for (int i = (int)audioChannelDestStart[channelNumber]; i < blockSize + audioChannelDestStart[channelNumber]; i += 2)
                    {
                        tmp = buffOut[i];
                        buffOut[i] = buffOut[i + 1];
                        buffOut[i + 1] = tmp;
                    }

                }

                // advance to the next channel's data
                buffInOffset += sourceSize;
            }
            return chd_error.CHDERR_NONE;
        }

        HuffmanDecoder m_audiohi_decoder = null;
        HuffmanDecoder m_audiolo_decoder = null;
        if (treesize != 0)
        {
            BitStream bitbuf = new BitStream(buffIn, (int)buffInOffset);
            m_audiohi_decoder = new HuffmanDecoder(256, 16, bitbuf);
            m_audiolo_decoder = new HuffmanDecoder(256, 16, bitbuf);
            huffman_error hufferr = m_audiohi_decoder.ImportTreeRLE();
            if (hufferr != huffman_error.HUFFERR_NONE)
                return chd_error.CHDERR_INVALID_DATA;
            bitbuf.flush();
            hufferr = m_audiolo_decoder.ImportTreeRLE();
            if (hufferr != huffman_error.HUFFERR_NONE)
                return chd_error.CHDERR_INVALID_DATA;
            if (bitbuf.flush() != treesize)
                return chd_error.CHDERR_INVALID_DATA;
            buffInOffset += treesize;
        }

        // loop over channels
        for (int chnum = 0; chnum < channels; chnum++)
        {
            // only process if the data is requested
            uint? curdest = audioChannelDestStart[chnum];
            if (curdest != null)
            {
                int prevsample = 0;

                // if no huffman length, just copy the data
                if (treesize == 0)
                {
                    uint cursource = buffInOffset;
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        int delta = (buffIn[cursource + 0] << 8) | buffIn[cursource + 1];
                        cursource += 2;

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        buffOut[(uint)curdest + 0] = (byte)(newsample >> 8);
                        buffOut[(uint)curdest + 1] = (byte)newsample;
                        curdest += 2;
                    }
                }

                // otherwise, Huffman-decode the data
                else
                {
                    BitStream bitbuf = new BitStream(buffIn, (int)buffInOffset);
                    m_audiohi_decoder.AssignBitStream(bitbuf);
                    m_audiolo_decoder.AssignBitStream(bitbuf);
                    for (int sampnum = 0; sampnum < samples; sampnum++)
                    {
                        short delta = (short)(m_audiohi_decoder.DecodeOne() << 8);
                        delta |= (short)m_audiolo_decoder.DecodeOne();

                        int newsample = prevsample + delta;
                        prevsample = newsample;

                        buffOut[(uint)curdest + 0] = (byte)(newsample >> 8);
                        buffOut[(uint)curdest + 1] = (byte)newsample;
                        curdest += 2;
                    }
                    if (bitbuf.overflow())
                        return chd_error.CHDERR_INVALID_DATA;
                }
            }

            // advance to the next channel's data
            buffInOffset += (uint)audioChannelCompressedSize[chnum];
        }
        return chd_error.CHDERR_NONE;
    }



    private chd_error decodeVideo(uint width, uint height, byte[] buffIn, uint buffInOffset, uint buffInLength, byte[] buffOut, uint buffOutOffset, uint dstride)
    {
        // if the high bit of the first byte is set, we decode losslessly
        if ((buffIn[buffInOffset] & 0x80) != 0)
            return DecodeVideoLossless(width, height, buffIn, buffInOffset, buffInLength, buffOut, buffOutOffset, dstride);
        else
            return chd_error.CHDERR_INVALID_DATA;
    }



    private chd_error DecodeVideoLossless(uint width, uint height, byte[] buffIn, uint buffInOffset, uint buffInLength, byte[] buffOut, uint buffOutOffset, uint dstride)
    {
        // skip the first byte
        BitStream bitbuf = new BitStream(buffIn, (int)buffInOffset);
        bitbuf.read(8);

        HuffmanDecoderRLE m_ycontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);
        HuffmanDecoderRLE m_cbcontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);
        HuffmanDecoderRLE m_crcontext = new HuffmanDecoderRLE(256 + 16, 16, bitbuf);

        // import the tables
        huffman_error hufferr = m_ycontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();
        hufferr = m_cbcontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();
        hufferr = m_crcontext.ImportTreeRLE();
        if (hufferr != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;
        bitbuf.flush();

        // decode to the destination
        m_ycontext.Reset();
        m_cbcontext.Reset();
        m_crcontext.Reset();

        for (int dy = 0; dy < height; dy++)
        {
            uint row = buffOutOffset + (uint)dy * dstride;
            for (int dx = 0; dx < width / 2; dx++)
            {
                buffOut[row + 0] = (byte)m_ycontext.DecodeOne();
                buffOut[row + 1] = (byte)m_cbcontext.DecodeOne();
                buffOut[row + 2] = (byte)m_ycontext.DecodeOne();
                buffOut[row + 3] = (byte)m_crcontext.DecodeOne();
                row += 4;
            }
            m_ycontext.FlushRLE();
            m_cbcontext.FlushRLE();
            m_crcontext.FlushRLE();
        }

        // check for errors if we overflowed or decoded too little data
        if (bitbuf.overflow() || bitbuf.flush() != buffInLength)
            return chd_error.CHDERR_INVALID_DATA;
        return chd_error.CHDERR_NONE;
    }
}
