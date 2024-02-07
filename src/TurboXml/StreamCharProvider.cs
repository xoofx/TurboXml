// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace TurboXml;

/// <summary>
/// Provides characters from a stream.
/// </summary>
internal struct StreamCharProvider : ICharProvider, IDisposable
{
    private Stream _stream;
    private Decoder _decoder;
    private readonly byte[] _buffer;
    private int _bufferOffset;
    private readonly char[] _text;
    private int _length;
    private int _index;

    public StreamCharProvider(Stream stream, Encoding? encoding)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(16384);
        _length = 0;
        _index = 0;
        _bufferOffset = 0;

        var detectedEncoding = DetectEncodingOrNull(stream, out var offset);
        if (detectedEncoding != null)
        {
            encoding ??= detectedEncoding;
        }
        encoding ??= Encoding.UTF8;

        _decoder = encoding.GetDecoder();
        _text = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(_buffer.Length));
        if (offset > 0)
        {
            _stream.Position += offset;
        }
    }

    public bool TryReadNext(out char c)
    {
        var index = _index;
        Unsafe.SkipInit(out c);
        if (index >= _length)
        {
            if (_length < 0)
            {
                return false;
            }

            FillBuffer();
            index = 0;
            _index = index;

            if (_length <= 0)
            {
                return false;
            }
        }

        c = Unsafe.Add(ref MemoryMarshal.GetReference(_text.AsSpan()), index);
        _index = index + 1;
        return true;
    }

    private void FillBuffer()
    {
        var bufferOffset = _bufferOffset;
        var buffer = _buffer;
        var read = _stream.Read(buffer, bufferOffset, buffer.Length - bufferOffset);
        if (read == 0)
        {
            _length = -1;
            return;
        }

        _decoder.Convert(buffer, 0, bufferOffset + read, _text, 0, _text.Length, false, out var byteConverted, out _length, out _);
        if (byteConverted > 0)
        {
            bufferOffset = read - byteConverted;
            Buffer.BlockCopy(buffer, byteConverted, buffer, 0, bufferOffset);
        }
        else
        {
            _length = -1;
            bufferOffset = 0;
        }

        _bufferOffset = bufferOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPreviewChar128(out Vector128<ushort> data)
    {
        var index = _index;
        Unsafe.SkipInit(out data);

        if (index >= _length)
        {
            if (_length < 0)
            {
                return false;
            }

            FillBuffer();
            index = 0;
            _index = index;

            if (_length <= 0)
            {
                return false;
            }
        }

        if (index + 8 <= _length)
        {
            data = Unsafe.As<char, Vector128<ushort>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_text.AsSpan()), index));
            return true;
        }

        Unsafe.SkipInit(out data);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int countChars)
    {
        _index += countChars;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<char>.Shared.Return(_text);
    }

    /// <summary>
    /// Detects the encoding of the specified stream.
    /// </summary>
    /// <param name="stream">The stream to detect encoding from.</param>
    /// <param name="offset">The offset to the stream to skip any BOM.</param>
    /// <returns>The detected encoding or null if no particular encoding detected.</returns>
    private static Encoding? DetectEncodingOrNull(Stream stream, out int offset)
    {
        // https://www.w3.org/TR/xml/#sec-guessing-no-ext-info
        Span<byte> buffer = stackalloc byte[4];
        var position = stream.Position;
        var length = stream.Read(buffer);
        stream.Position = position;
        offset = 0;

        if (length < 2)
        {
            return null;
        }

        // With a Byte Order Mark:
        if (length >= 3)
        {
            // UTF8
            if (buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                offset = 3;
                return Encoding.UTF8;
            }
        }

        if (length < 4)
        {
            return null;
        }

        // UCS-4
        if (buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
        {
            offset = 4;
            return new UTF32Encoding(true, false);
        }

        if (buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            offset = 4;
            return Encoding.UTF32;
        }

        // UTF-16
        if (buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            offset = 2;
            return Encoding.BigEndianUnicode;
        }

        if (buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            offset = 2;
            return Encoding.Unicode;
        }

        // Without a Byte Order Mark
        if (buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x3C)
        {
            return new UTF32Encoding(true, false);
        }

        if (buffer[0] == 0x3C && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            return new UTF32Encoding(false, false);
        }

        // UTF-16 big-endian
        if (buffer[0] == 0x00 && buffer[1] == 0x3C && buffer[2] == 0x00 && buffer[3] == 0x3F)
        {
            return Encoding.BigEndianUnicode;
        }

        // UTF-16 little-endian
        if (buffer[0] == 0x3C && buffer[1] == 0x00 && buffer[2] == 0x3F && buffer[3] == 0x00)
        {
            return Encoding.Unicode;
        }

        // ASCII/UTF-8 little-endian
        if (buffer[0] == 0x3C && buffer[1] == 0x3F && buffer[2] == 0x78 && buffer[3] == 0x6D)
        {
            return Encoding.UTF8;
        }

        return null;
    }
}