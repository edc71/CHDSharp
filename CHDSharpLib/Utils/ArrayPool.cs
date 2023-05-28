using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace CHDSharpLib.Utils;

public class ArrayPool
{
    private uint _arraySize;
    private List<byte[]> _array;
    private int _issuedArraysTotal;
    private int _returnedArraysTotal;

    internal ArrayPool(uint arraySize)
    {
        _array = new List<byte[]>();
        _arraySize = arraySize;
        _issuedArraysTotal = 0;
        _returnedArraysTotal = 0;
    }

    internal void Destroy()
    {
        PrintStats();
        _array.Clear();
        _array = null;
        GC.Collect();
    }

    internal void PrintStats()
    {
        Console.WriteLine("rented: " + _issuedArraysTotal.ToString() + ", returned: " + _returnedArraysTotal.ToString() + ", diff: " + (_issuedArraysTotal - _returnedArraysTotal).ToString() + ", lost: " + _arraySize * (_issuedArraysTotal - _returnedArraysTotal));
    }

    internal byte[] Rent()
    {
        lock (_array)
        {
            _issuedArraysTotal++;
            if (_array.Count == 0)
            {
                return new byte[_arraySize];
            }
            
            byte[] ret = _array[0];
            _array.RemoveAt(0);
            return ret;
        }
    }

    internal void Return(byte[] ret)
    {
        _returnedArraysTotal++;
        lock (_array)
        {
            if (_array.Count < 128)
            {
                _array.Add(ret);
            }
        }
    }
    internal void ReadStats(out int issuedArraysTotal, out int returnedArraysTotal)
    {
        issuedArraysTotal = _issuedArraysTotal;
        returnedArraysTotal = _returnedArraysTotal;
    }
}
