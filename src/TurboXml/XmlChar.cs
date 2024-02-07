// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace TurboXml;

/// <summary>
/// Internal class used to classify characters for the parser.
/// </summary>
internal static class XmlChar
{
    /// <summary>
    /// Is a valid XML character
    /// </summary>
    /// <remarks>
    /// We don't validate the validity of surrogate pairs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(char c)
    {
        // [2]   	Char	   ::=   	#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]	/* any Unicode character, excluding the surrogate blocks, FFFE, and FFFF. */
        return char.IsBetween(c, ' ', (char)0xFFFF) || c == '\t' || c == '\n' || c == '\r';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpace(char c)
    {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r';
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
        test |= Vector128.Equals(data, Vector128.Create((ushort)'.'));
        // .
        test |= Vector128.Equals(data, Vector128.Create((ushort)'-'));
        // 0-9
        test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)'0')) & Vector128.LessThanOrEqual(data, Vector128.Create((ushort)'9'));
        return ~test == Vector128<ushort>.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameStartChar(char c)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return IsNameStartCharScalar(c);
        }

        var v1 = Vector128.Create(
            (ushort)'A',
            (ushort)'a',
            (ushort)':',
            (ushort)'_',
            (ushort)0xC0,
            (ushort)0xD8,
            (ushort)0xF8,
            (ushort)0x370);

        var v2 = Vector128.Create(
            (ushort)'Z',
            (ushort)'z',
            (ushort)':',
            (ushort)'_',
            (ushort)0xD6,
            (ushort)0xF6,
            (ushort)0x2FF,
            (ushort)0x37D);

        var vc = Vector128.Create((ushort)c);
        if ((Vector128.GreaterThanOrEqual(vc, v1) & Vector128.LessThanOrEqual(vc, v2)) != Vector128<ushort>.Zero)
        {
            return true;
        }

        v1 = Vector128.Create(
            (ushort)0x37F,
            (ushort)0x200C,
            (ushort)0x2070,
            (ushort)0x2C00,
            (ushort)0x3001,
            (ushort)0xF900,
            (ushort)0xFDF0,
            (ushort)'A');

        v2 = Vector128.Create(
            (ushort)0x1FFF,
            (ushort)0x200D,
            (ushort)0x218F,
            (ushort)0x2FEF,
            (ushort)0xD7FF,
            (ushort)0xFDCF,
            (ushort)0xFFFD,
            (ushort)'Z');

        return (Vector128.GreaterThanOrEqual(vc, v1) & Vector128.LessThanOrEqual(vc, v2)) != Vector128<ushort>.Zero;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameStartCharScalar(char c)
    {
        // [4] NameStartChar	   ::=   	":" | [A-Z] | "_" | [a-z] | [#xC0-#xD6] | [#xD8-#xF6] | [#xF8-#x2FF] | [#x370-#x37D] | [#x37F-#x1FFF] | [#x200C-#x200D] | [#x2070-#x218F] | [#x2C00-#x2FEF] | [#x3001-#xD7FF] | [#xF900-#xFDCF] | [#xFDF0-#xFFFD] | [#x10000-#xEFFFF]
        return char.IsAsciiLetter(c)
               || c == ':'
               || c == '_'
               || char.IsBetween(c, '\xC0', '\xD6')
               || char.IsBetween(c, '\xD8', '\xF6')
               || char.IsBetween(c, '\xF8', '\x2FF')
               || char.IsBetween(c, '\x370', '\x37D')
               || char.IsBetween(c, '\x37F', '\x1FFF')
               || char.IsBetween(c, '\x200C', '\x200D')
               || char.IsBetween(c, '\x2070', '\x218F')
               || char.IsBetween(c, '\x2C00', '\x2FEF')
               || char.IsBetween(c, '\x3001', '\xD7FF')
               || char.IsBetween(c, '\xF900', '\xFDCF')
               || char.IsBetween(c, '\xFDF0', '\xFFFD');
    }
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameStartChar(Rune rune)
    {
        // [4] NameStartChar	   ::=   	":" | [A-Z] | "_" | [a-z] | [#xC0-#xD6] | [#xD8-#xF6] | [#xF8-#x2FF] | [#x370-#x37D] | [#x37F-#x1FFF] | [#x200C-#x200D] | [#x2070-#x218F] | [#x2C00-#x2FEF] | [#x3001-#xD7FF] | [#xF900-#xFDCF] | [#xFDF0-#xFFFD] | [#x10000-#xEFFFF]

        return RuneBetween(rune, 'A', 'Z')
               || RuneBetween(rune, 'a', 'z')
               || rune.Value == ':'
               || rune.Value == '_'
               || RuneBetween(rune, '\xC0', '\xD6')
               || RuneBetween(rune, '\xD8', '\xF6')
               || RuneBetween(rune, '\xF8', '\x2FF')
               || RuneBetween(rune, '\x370', '\x37D')
               || RuneBetween(rune, '\x37F', '\x1FFF')
               || RuneBetween(rune, '\x200C', '\x200D')
               || RuneBetween(rune, '\x2070', '\x218F')
               || RuneBetween(rune, '\x2C00', '\x2FEF')
               || RuneBetween(rune, '\x3001', '\xD7FF')
               || RuneBetween(rune, '\xF900', '\xFDCF')
               || RuneBetween(rune, '\xFDF0', '\xFFFD')
               || RuneBetween(rune, 0x10000, 0xEFFFF);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RuneBetween(Rune value, int min, int max) => (uint)(value.Value - min) <= (uint)(max - min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameChar(char c)
    {
        // [4a] NameStartChar | "-" | "." | [0-9] | #xB7 | [#x0300-#x036F] | [#x203F-#x2040]
        return IsNameStartChar(c)
               || char.IsBetween(c, '0', '9')
               || c == '.'
               || c == '-'
               || c == '\xB7'
               || char.IsBetween(c, '\x0300', '\x036f')
               || char.IsBetween(c, '\x203f', '\x2040');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNameChar(Rune rune)
    {
        // [4a] NameStartChar | "-" | "." | [0-9] | #xB7 | [#x0300-#x036F] | [#x203F-#x2040]
        return IsNameStartChar(rune)
               || RuneBetween(rune, '0', '9')
               || rune.Value == '.'
               || rune.Value == '-'
               || rune.Value == '\xB7'
               || RuneBetween(rune, '\x0300', '\x036f')
               || RuneBetween(rune, '\x203f', '\x2040');
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