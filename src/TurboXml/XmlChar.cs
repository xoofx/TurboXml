// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace TurboXml;


[Flags]
internal enum XmlCharCategory : byte
{
    None = 0,
    Char = 1 << 0,
    NameStartChar = 1 << 1,
    NameChar = 1 << 2,
    AttrValueChar = 1 << 3,
    CommentChar = 1 << 4,
    WhiteSpace = 1 << 5,
    CDATAChar = 1 << 6,
}

/// <summary>
/// Internal class used to classify characters for the parser.
/// </summary>
internal static partial class XmlChar
{
    public const char HIGH_SURROGATE_START = '\ud800';
    private const char HIGH_SURROGATE_END = '\udbff';
    private const char LOW_SURROGATE_START = '\udc00';
    private const char LOW_SURROGATE_END = '\udfff';
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static XmlCharCategory GetCharCategory(char ch) => (XmlCharCategory)Unsafe.Add(ref MemoryMarshal.GetReference(CharCategories), (int)ch);
    
    /// <summary>
    /// Is a valid XML character
    /// </summary>
    /// <remarks>
    /// We don't validate the validity of surrogate pairs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsChar(char c)
    {
        // [2]   	Char	   ::=   	#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]	/* any Unicode character, excluding the surrogate blocks, FFFE, and FFFF. */
        return (GetCharCategory(c) & XmlCharCategory.Char) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAttrValueChar(char c)
    {
        return (GetCharCategory(c) & XmlCharCategory.AttrValueChar) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCommentChar(char c)
    {
        return (GetCharCategory(c) & XmlCharCategory.CommentChar) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCDATAChar(char c)
    {
        return (GetCharCategory(c) & XmlCharCategory.CDATAChar) != 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpace(char c)
    {
        return (GetCharCategory(c) & XmlCharCategory.WhiteSpace) != 0;
    }

    /// <summary>
    /// Test a subset of characters that are valid for a name.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCommonNameChar(Vector256<ushort> data)
    {
        // A-Z, a-z
        var test = Vector256.LessThanOrEqual((data - Vector256.Create((ushort)'A')) & Vector256.Create((ushort)0xFFDF), Vector256.Create((ushort)25));
        // _
        test |= Vector256.Equals(data, Vector256.Create((ushort)'_'));
        // :
        test |= Vector256.Equals(data, Vector256.Create((ushort)':'));
        // -
        test |= Vector256.Equals(data, Vector256.Create((ushort)'-'));
        // 0-9
        test |= Vector256.GreaterThanOrEqual(data, Vector256.Create((ushort)'0')) & Vector256.LessThanOrEqual(data, Vector256.Create((ushort)'9'));
        return ~test == Vector256<ushort>.Zero;
    }
    
    /// <summary>
    /// Test a subset of characters that are valid for a name.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCommonNameChar(Vector128<ushort> data)
    {
        // A-Z, a-z
        var test = Vector128.LessThanOrEqual((data - Vector128.Create((ushort)'A')) & Vector128.Create((ushort)0xFFDF), Vector128.Create((ushort)25));
        // _
        test |= Vector128.Equals(data, Vector128.Create((ushort)'_'));
        // :
        test |= Vector128.Equals(data, Vector128.Create((ushort)':'));
        // -
        test |= Vector128.Equals(data, Vector128.Create((ushort)'-'));
        // 0-9
        test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)'0')) & Vector128.LessThanOrEqual(data, Vector128.Create((ushort)'9'));
        return ~test == Vector128<ushort>.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameStartChar(char c)
    {
        // [4] NameStartChar	   ::=   	":" | [A-Z] | "_" | [a-z] | [#xC0-#xD6] | [#xD8-#xF6] | [#xF8-#x2FF] | [#x370-#x37D] | [#x37F-#x1FFF] | [#x200C-#x200D] | [#x2070-#x218F] | [#x2C00-#x2FEF] | [#x3001-#xD7FF] | [#xF900-#xFDCF] | [#xFDF0-#xFFFD] | [#x10000-#xEFFFF]

        return (GetCharCategory(c) & XmlCharCategory.NameStartChar) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameHighSurrogate(char c)
    {
        // The range #x10000-#xEFFFF encode
        // - the high surrogate from 0xD800 to 0xDB7F (not 0xDBFF)
        // - the low surrogate from 0xDC00 to 0xDFFF (full range)
        return char.IsBetween(c, HIGH_SURROGATE_START, (char)0xDB7F);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char UnsafeGetUtf16SurrogatesFromCodePoint(int value, out char lowSurrogateCodePoint)
    {
        // Assume that the codepoint has been validated before calling this method.
        Debug.Assert(Rune.IsValid((uint)value));
        // This calculation comes from the Unicode specification, Table 3-5.
        lowSurrogateCodePoint = (char)((value & 0x3FFu) + 0xDC00u);
        return (char)((value + ((0xD800u - 0x40u) << 10)) >> 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int UnsafeConvertToUtf32(char highSurrogate, char lowSurrogate)
    {
        // First, extend both to 32 bits, then calculate the offset of
        // each candidate surrogate char from the start of its range.

        uint highSurrogateOffset = (uint)highSurrogate - HIGH_SURROGATE_START;
        // The 0x40u << 10 below is to account for uuuuu = wwww + 1 in the surrogate encoding.
        return ((int)highSurrogateOffset << 10) + (lowSurrogate - LOW_SURROGATE_START) + (0x40 << 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RuneBetween(Rune value, int min, int max) => (uint)(value.Value - min) <= (uint)(max - min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameChar(char c)
    {
        // [4a] NameStartChar | "-" | "." | [0-9] | #xB7 | [#x0300-#x036F] | [#x203F-#x2040]
        return (GetCharCategory(c) & XmlCharCategory.NameChar) != 0;
    }

    public static bool TryGetHexDigit(char c, out int value)
    {
        // TODO: could be optimized with a lookup table
        if (char.IsBetween(c, '0', '9'))
        {
            value = c - '0';
            return true;
        }

        if (char.IsBetween(c, 'A', 'F'))
        {
            value = c - 'A' + 10;
            return true;
        }

        if (char.IsBetween(c, 'a', 'f'))
        {
            value = c - 'a' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}