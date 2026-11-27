// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace TurboXml;

/// <summary>
/// Provides characters from a string.
/// </summary>
internal struct StringCharProvider : ICharProvider
{
    private int _index = 0;
    private readonly string _text;

    public StringCharProvider(string text)
    {
        _text = text;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadNext(ref char c)
    {
        var index = _index;
        var text = _text;
        if ((uint)index < (uint)text.Length)
        {
            c = text[index];
            _index = index + 1;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPreviewChar128(out Vector128<ushort> data)
    {
        var text = _text;
        var index = _index;
        if (SpanRange.Fits(index, Vector128<ushort>.Count, text.Length))
        {
            data = Vector128.Create(MemoryMarshal.Cast<char, ushort>(text.AsSpan(index, Vector128<ushort>.Count)));
            return true;
        }

        data = default;
        return false;
    }

    public bool TryPreviewChar256(out Vector256<ushort> data)
    {
        var text = _text;
        var index = _index;
        if (SpanRange.Fits(index, Vector256<ushort>.Count, text.Length))
        {
            data = Vector256.Create(MemoryMarshal.Cast<char, ushort>(text.AsSpan(index, Vector256<ushort>.Count)));
            return true;
        }

        data = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int countChars)
    {
        _index += countChars;
    }
}