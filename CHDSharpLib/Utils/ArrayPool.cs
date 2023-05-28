using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace CHDSharpLib.Utils;

public class ArrayPool
{
    private uint _arraySize;
    private List<byte[]> _array;
    private int _count;
    private int _issuedArraysTotal;
    private int _returnedArraysTotal;

    internal ArrayPool(uint arraySize)
    {
        _array = new List<byte[]>();
        _arraySize = arraySize;
        _count = 0;
        _issuedArraysTotal = 0;
        _returnedArraysTotal = 0;
    }

    internal void Destroy()
    {
        Console.WriteLine("rented: " + _issuedArraysTotal.ToString() + ", returned: " + _returnedArraysTotal.ToString() + "                ");
        _array.Clear();
        _array = null;
        GC.Collect();
    }

    internal byte[] Rent()
    {
        lock (_array)
        {
            _issuedArraysTotal++;
            if (_count == 0)
            {
                return new byte[_arraySize];
            }

            _count--;
            
            byte[] ret = _array[_count];
            _array.RemoveAt(_count);
            return ret;
        }
    }

    internal void Return(byte[] ret)
    {
        _returnedArraysTotal++;
        lock (_array)
        {
            if (_array.Count < 256)
            {
                _array.Add(ret);
                _count++;
            }
        }
    }
    internal void ReadStats(out int issuedArraysTotal, out int returnedArraysTotal)
    {
        issuedArraysTotal = _issuedArraysTotal;
        returnedArraysTotal = _returnedArraysTotal;
    }
}
