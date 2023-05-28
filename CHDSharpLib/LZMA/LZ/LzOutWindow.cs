using CHDSharpLib.Utils;
using Compress.Support.Compression.LZMA;

namespace Compress.Support.Compression.LZ
{
    internal class OutWindow
    {
        byte[] _buffer = null;
        int _windowSize = 0;
        int _pos;
        int _streamPos;
        int _pendingLen;
        int _pendingDist;

        ArrayPool _arrBlockSize = null;
        public long Total;
        public long Limit;

        public void Create(int windowSize, ArrayPool arrBlockSize)
        {
            _arrBlockSize = arrBlockSize;
            if (_windowSize != windowSize)
                if (_buffer == null)
                    _buffer = _arrBlockSize.Rent();
            _buffer[windowSize - 1] = 0;
            _windowSize = windowSize;
            _pos = 0;
            _streamPos = 0;
            _pendingLen = 0;
            Total = 0;
            Limit = 0;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                _arrBlockSize.Return(_buffer);
                _buffer = null;
            }
        }

        public void Flush()
        {
        }

        public void CopyBlock(int distance, int len)
        {
            int size = len;
            int pos = _pos - distance - 1;
            if (pos < 0)
                pos += _windowSize;
            for (; size > 0 && _pos < _windowSize && Total < Limit; size--)
            {
                if (pos >= _windowSize)
                    pos = 0;
                _buffer[_pos++] = _buffer[pos++];
                Total++;
                if (_pos >= _windowSize)
                    Flush();
            }
            _pendingLen = size;
            _pendingDist = distance;
        }

        public void PutByte(byte b)
        {
            _buffer[_pos++] = b;
            Total++;
            if (_pos >= _windowSize)
                Flush();
        }

        public byte GetByte(int distance)
        {
            int pos = _pos - distance - 1;
            if (pos < 0)
                pos += _windowSize;
            return _buffer[pos];
        }


        public void SetLimit(long size)
        {
            Limit = Total + size;
        }

        public bool HasSpace
        {
            get
            {
                return _pos < _windowSize && Total < Limit;
            }
        }

        public bool HasPending
        {
            get
            {
                return _pendingLen > 0;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_streamPos >= _pos)
                return 0;

            int size = _pos - _streamPos;
            if (size > count)
                size = count;
            System.Buffer.BlockCopy(_buffer, _streamPos, buffer, offset, size);
            _streamPos += size;
            if (_streamPos >= _windowSize)
            {
                _pos = 0;
                _streamPos = 0;
            }
            return size;
        }

        public void CopyPending()
        {
            if (_pendingLen > 0)
                CopyBlock(_pendingDist, _pendingLen);
        }

        public int AvailableBytes
        {
            get
            {
                return _pos - _streamPos;
            }
        }
    }
    
}
