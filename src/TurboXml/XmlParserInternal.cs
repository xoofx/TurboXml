// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace TurboXml;

/// <summary>
/// Internal class used to parse an XML document.
/// </summary>
/// <typeparam name="THandler">The handler to use to process the XML document.</typeparam>
/// <typeparam name="TCharProvider">The char provider to read the XML document.</typeparam>
internal ref struct XmlParserInternal
    <THandler, TCharProvider>
    where THandler : IXmlReadHandler
    where TCharProvider : ICharProvider
{
    private int _line;
    private int _column;
    private ref THandler _handler;
    private ref TCharProvider _stream;
    private int _contentLine;
    private int _contentColumn;
    private bool _xmlParsingBody;
    private bool _isInProlog;
    private bool _hasDocumentTypeDeclaration;
    private readonly bool _ignoreDtd;
    private readonly bool _rejectFragments;
    private bool _hasRootElement;

    private char[] _buffer;
    private int _length;
    private int _lastNameStackIndex;

    // For a string input, the content in progress is the slice of the input at _sliceStart, until a character differs from the input.
    private int _sliceStart;
    private int _sliceLength;

    public XmlParserInternal(ref THandler handler, ref TCharProvider stream, XmlParserOptions options)
    {
        _handler = ref handler;
        _stream = ref stream;
        _buffer = ArrayPool<char>.Shared.Rent(128);
        _sliceStart = -1;
        _column = -1;
        _isInProlog = true;
        _ignoreDtd = options.IgnoreDtd;
        _rejectFragments = options.RejectFragments;
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null!;
        }
    }

    public void Parse()
    {
        try
        {
            ParseImpl();
        }
        catch (XmlThrowHelper.InternalXmlException e)
        {
            _handler.OnError(e.Message, e.Line, e.Column);
        }
    }

    private void ParseImpl()
    {
        StartContent();
        while (true)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // Process the content of the attribute using SIMDc
                while (_stream.TryPreviewChar128(out var data))
                {
                    // If there is a surrogate character, we need to stop the parsing
                    var test = Vector128.LessThan(data, Vector128.Create((ushort)' '));
                    test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)XmlChar.HIGH_SURROGATE_START));
                    test |= Vector128.Equals(data, Vector128.Create((ushort)'&'));
                    test |= Vector128.Equals(data, Vector128.Create((ushort)'<'));
                    test |= Vector128.Equals(data, Vector128.Create((ushort)']'));
                    if (test != Vector128<ushort>.Zero)
                    {
                        break;
                    }

                    AppendContent(data);
                    _stream.Advance(Vector128<ushort>.Count);
                    _column += Vector128<ushort>.Count;
                }
            }

            char c = default;
            if (!TryReadNext(ref c))
            {
                break;
            }

        ProcessNextChar:
            switch (c)
            {
                case '<':
                    FlushCharacterBuffer();
                    c = ReadNext();

                    if (c == '?')
                    {
                        ParseXmlDeclarationOrProcessingInstruction();
                    }
                    else if (c == '!')
                    {
                        c = ReadNext();
                        if (c == '-')
                        {
                            ParseComment();
                        }
                        else if (c == '[')
                        {
                            if (_rejectFragments && !HasElementsInStack)
                                ThrowInvalidXml(XmlThrow.InvalidTextOutsideRootElement);

                            _isInProlog = false;
                            ParseCData();
                        }
                        else if (c == 'D' && _ignoreDtd)
                        {
                            ParseDocumentTypeDeclaration();
                        }
                        else
                            ParseUnsupportedXmlDirective();
                    }
                    else if (c == '/')
                    {
                        _isInProlog = false;
                        ParseEndTag();
                    }
                    else
                    {
                        _isInProlog = false;
                        ParseBeginTag(c);
                    }

                    _xmlParsingBody = true;

                    _contentLine = _line;
                    _contentColumn = _column + 1;
                    StartContent();

                    break;
                case '&':
                    // [27] Misc ::= Comment | PI | S, so a reference is not allowed outside the root element
                    if (_rejectFragments && !HasElementsInStack)
                        ThrowInvalidXml(XmlThrow.InvalidTextOutsideRootElement);

                    _xmlParsingBody = true;
                    _isInProlog = false;
                    ParseEntity(ref c);
                    break;

                case ']':
                    // [14] CharData ::= [^<&]* - ([^<&]* ']]>' [^<&]*)
                    _xmlParsingBody = true;
                    _isInProlog = false;
                    AppendContent(c);

                    if (!TryReadNext(ref c))
                    {
                        goto EndOfStream;
                    }

                    if (c != ']')
                    {
                        goto ProcessNextChar;
                    }

                    do
                    {
                        AppendContent(c);
                        if (!TryReadNext(ref c))
                        {
                            goto EndOfStream;
                        }
                    } while (c == ']');

                    if (c == '>')
                    {
                        XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCDATAEndInText, _line, _column - 2);
                    }

                    goto ProcessNextChar;

                case '\n':
                    _xmlParsingBody = true;
                    _line++;
                    _column = -1;
                    AppendContent(c);
                    break;

                case '\r':
                    _xmlParsingBody = true;
                    // As per the XML spec, we need to normalize \r and \r\n to \n
                    AppendContent('\n');
                    _line++;
                    _column = -1;

                    if (!TryReadNext(ref c))
                    {
                        goto EndOfStream;
                    }

                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        goto ProcessNextChar;
                    }

                    break;

                default:
                    _xmlParsingBody = true;
                    if (!XmlChar.IsWhiteSpace(c))
                    {
                        _isInProlog = false;
                    }

                    if (XmlChar.IsChar(c))
                    {
                        AppendContent(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendContent(c);
                            AppendContent(ReadLowSurrogate());
                        }
                        else
                        {
                            ThrowInvalidXml(XmlThrow.InvalidCharacterFound);
                        }
                    }
                    break;

            }
        }

    EndOfStream:
        FlushCharacterBuffer();

        // Flush any errors of opened tags
        if (HasElementsInStack)
        {
            CheckLastClosingElements();
        }
        else if (_rejectFragments && !_hasRootElement)
        {
            // [1] document ::= prolog element Misc*
            ThrowInvalidXml(XmlThrow.InvalidMissingRootElement);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckLastClosingElements()
    {
        while (HasElementsInStack)
        {
            var name = PopTagName();
            _handler.OnError($"Invalid tag {name} not closed at the end of the document.", _line, _column);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetTextSpan() => GetTextSpan(_lastNameStackIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetTextSpan(int index)
    {
        return _buffer.AsSpan(index, _length - index);
    }

    private void ParseBeginTag(char c)
    {
        // Start-tag
        // [40]    STag    ::=    '<' Name (S Attribute)* S? '>' [WFC: Unique Att Spec]

        int line = _line;
        int column = _column;
        int startNameIndex = _length;
        if (!TryParseName(ref c))
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidBeginTag, line, column);

        if (_rejectFragments && !HasElementsInStack)
        {
            if (_hasRootElement)
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidSecondRootElement, line, column);

            _hasRootElement = true;
        }

        var name = GetTextSpan(startNameIndex);
        AppendInt(name.Length);
        _lastNameStackIndex = _length;

        _handler.OnBeginTag(name, line, column);

        while (true)
        {
            var hasSpaces = SkipSpaces(ref c);

            switch (c)
            {
                case '>':
                    return;
                case '/':
                    c = ReadNext();

                    if (c != '>')
                        ThrowInvalidXml(XmlThrow.InvalidEndTagEmpty);

                    _handler.OnEndTagEmpty();

                    // We pop the tag name as it is an empty tag
                    // No need to check if the stack is empty as we are in a start tag
                    PopTagName();

                    return;
                default:
                    if (!hasSpaces)
                        ThrowInvalidXml(XmlThrow.InvalidCharacterExpectingWhiteSpace);

                    ParseAttribute(c);
                    break;
            }

            c = ReadNext();
        }
    }

    private bool TryParseName(ref char c)
    {
        if (XmlChar.IsNameStartChar(c))
        {
            AppendCharacter(c);
        }
        else if (XmlChar.IsNameHighSurrogate(c))
        {
            AppendCharacter(c);
            AppendCharacter(ReadLowSurrogate());
        }
        else
        {
            return false;
        }

        // Try batch to read a name
        if (Vector128.IsHardwareAccelerated)
        {
            // Process the content of the attribute using SIMD
            while (_stream.TryPreviewChar128(out var data))
            {
                if (XmlChar.IsCommonNameChar(data))
                {
                    AppendCharacters(data);
                    _stream.Advance(Vector128<ushort>.Count);
                    _column += Vector128<ushort>.Count;
                }
                else
                {
                    break;
                }
            }
        }

        while (TryReadNext(ref c))
        {
            if (XmlChar.IsNameChar(c))
            {
                AppendCharacter(c);
            }
            else if (XmlChar.IsNameHighSurrogate(c))
            {
                AppendCharacter(c);
                AppendCharacter(ReadLowSurrogate());
            }
            else
            {
                break;
            }
        }

        return true;
    }

    [SkipLocalsInit]
    private void ParseAttribute(char c)
    {
        // [41]    Attribute    ::=    Name Eq AttValue
        int nameLine = _line;
        int nameColumn = _column;
        if (!TryParseName(ref c))
            ThrowInvalidXml(XmlThrow.InvalidAttributeName);

        // We don't clear the buffer after parsing the attribute name as we are going to use it for the attribute value
        var attributeName = GetTextSpan();

        SkipSpaces(ref c);
        if (c != '=')
            ThrowInvalidXml(XmlThrow.InvalidCharacterExpectingEqual);

        c = ReadNext();
        SkipSpaces(ref c);

        int valueLine = _line;
        int valueColumn = _column;
        var attributeValue = ParseAttributeValue(c);

        _handler.OnAttribute(attributeName, attributeValue, nameLine, nameColumn, valueLine, valueColumn);
        ClearCharacterBuffer();
    }


    private ReadOnlySpan<char> ParseAttributeValue(char c)
    {
        // [10] AttValue   ::=   '"' ([^<&"] | Reference)* '"'
        //                    |  "'" ([^<&'] | Reference)* "'"

        if (c != '"' && c != '\'')
            ThrowInvalidXml(XmlThrow.InvalidAttributeValueExpectingQuoteOrDoubleQuote);

        var startChar = c;
        int startIndex = _length;
        StartContent();

        if (Vector256.IsHardwareAccelerated)
        {
            // Process the content of the attribute using SIMD
            while (_stream.TryPreviewChar256(out var data))
            {
                // If there is a surrogate character, we need to stop the parsing
                var test = Vector256.Equals(data, Vector256.Create((ushort)startChar));
                test |= Vector256.GreaterThanOrEqual(data, Vector256.Create((ushort)XmlChar.HIGH_SURROGATE_START));
                test |= Vector256.Equals(data, Vector256.Create((ushort)'&'));
                test |= Vector256.LessThan(data, Vector256.Create((ushort)' '));
                test |= Vector256.Equals(data, Vector256.Create((ushort)'<'));
                if (test != Vector256<ushort>.Zero)
                {
                    break;
                }

                AppendContent(data);

                _stream.Advance(Vector256<ushort>.Count);
                _column += Vector256<ushort>.Count;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // Process the content of the attribute using SIMD
            while (_stream.TryPreviewChar128(out var data))
            {
                // If there is a surrogate character, we need to stop the parsing
                var test = Vector128.Equals(data, Vector128.Create((ushort)startChar));
                test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)XmlChar.HIGH_SURROGATE_START));
                test |= Vector128.Equals(data, Vector128.Create((ushort)'&'));
                test |= Vector128.LessThan(data, Vector128.Create((ushort)' '));
                test |= Vector128.Equals(data, Vector128.Create((ushort)'<'));
                if (test != Vector128<ushort>.Zero)
                {
                    break;
                }

                AppendContent(data);

                _stream.Advance(Vector128<ushort>.Count);
                _column += Vector128<ushort>.Count;
            }
        }

        while (true)
        {
            c = ReadNext();

        ProcessNextChar:

            switch (c)
            {
                case '<':
                    ThrowInvalidXml(XmlThrow.InvalidCharacterLessThanInAttributeValue);
                    break;
                case '&':
                    ParseEntity(ref c);
                    continue;

                case '\n':
                    _line++;
                    _column = -1;

                    AppendContent(c);
                    break;
                case '\r':
                    // As per the XML spec, we need to normalize \r and \r\n to \n
                    AppendContent('\n');

                    c = ReadNext();

                    _line++;
                    if (c != '\n')
                    {
                        _column = 0;
                        goto ProcessNextChar;
                    }
                    else
                    {
                        _column = -1;
                    }

                    break;
                default:
                    if (c == startChar)
                    {
                        return GetContentSpan(startIndex);
                    }

                    AppendContent(c);

                    if (!XmlChar.IsAttrValueChar(c))
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendContent(ReadLowSurrogate());
                        }
                        else
                        {
                            ThrowInvalidXml(XmlThrow.InvalidCharacterInAttributeValue);
                        }
                    }
                    break;
            }
        }
    }

    private const int MaxCodePoint = 0x10FFFF;

    private void ParseEntity(ref char c)
    {
        // Parse EntityName
        c = ReadNext();
        int line = _line;
        int column = _column;

        if (c == '#')
        {
            c = ReadNext();

            int charCodePoint;
            if (c == 'x')
            {
                c = ReadNext();

                if (!XmlChar.TryGetHexDigit(c, out charCodePoint))
                    ThrowInvalidXml(XmlThrow.InvalidHexDigit);

                while (TryReadNext(ref c))
                {
                    if (XmlChar.TryGetHexDigit(c, out int newValue))
                    {
                        charCodePoint = (charCodePoint << 4) + newValue;
                        if (charCodePoint > MaxCodePoint)
                            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCharacterFound, line, column);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                charCodePoint = c - '0';
                if ((uint)charCodePoint > 9)
                    ThrowInvalidXml(XmlThrow.InvalidDigit);

                while (TryReadNext(ref c))
                {
                    var d = c - '0';
                    if ((uint)d <= 9)
                    {
                        charCodePoint = charCodePoint * 10 + d;
                        if (charCodePoint > MaxCodePoint)
                            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCharacterFound, line, column);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (!Rune.TryCreate(charCodePoint, out var rune))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCharacterFound, line, column);

            AppendValidRune(rune);

            if (c != ';')
                ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingSemiComma);

            // [WFC: Legal Character] Characters referred to using character references MUST match the production for Char.
            if (charCodePoint <= char.MaxValue && !XmlChar.IsChar((char)charCodePoint))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCharacterFound, line, column);
            return;
        }


        var rc = (char)0;

        switch (c)
        {
            case 'l':
                if (TryReadNext(ref c) && c == 't')
                {
                    rc = '<';
                }

                break;
            case 'g':
                if (TryReadNext(ref c) && c == 't')
                {
                    rc = '>';
                }

                break;
            case 'a':
                if (TryReadNext(ref c))
                {
                    if (c == 'm' && TryReadNext(ref c) && c == 'p')
                    {
                        rc = '&';
                    }
                    else if (c == 'p' && TryReadNext(ref c) && c == 'o' && TryReadNext(ref c) && c == 's')
                    {
                        rc = '\'';
                    }
                }

                break;
            case 'q':
                if (TryReadNext(ref c) & c == 'u' && TryReadNext(ref c) && c == 'o' && TryReadNext(ref c) && c == 't')
                {
                    rc = '"';
                }

                break;
        }

        if (rc == 0)
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEntityName, line, column);

        c = ReadNext();
        if (c != ';')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingSemiComma);

        AppendContent(rc);
    }

    private void ExpectSpaces(ref char c)
    {
        if (!XmlChar.IsWhiteSpace(c))
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingWhitespace);

        SkipSpaces(ref c);
    }

    private bool SkipSpaces(ref char c)
    {
        bool hasSpaces = false;
        while (true)
        {
        NextCharacter:
            switch (c)
            {
                case ' ':
                case '\t':
                    hasSpaces = true;
                    break;
                case '\n':
                    _line++;
                    _column = -1;
                    hasSpaces = true;
                    break;
                case '\r':
                    c = ReadNext();
                    hasSpaces = true;

                    _line++;
                    if (c != '\n')
                    {
                        _column = 0;
                        goto NextCharacter;
                    }
                    else
                    {
                        _column = -1;
                    }

                    break;
                default:
                    return hasSpaces;
            }

            c = ReadNext();
        }
    }

    private void ParseEndTag()
    {
        // Skip /
        var column = _column + 1;
        var line = _line;

        if (!HasElementsInStack)
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndTagNameFoundNotMatchingBeginTag, line, column);

        var name = PopTagName();

        var remaining = name;

        // We check for Vector128 but we process with Vector256, as it can translate to use Vector128 behind the scene
        if (Vector128.IsHardwareAccelerated)
        {
            while (remaining.Length >= Vector256<ushort>.Count && _stream.TryPreviewChar256(out var data))
            {
                if (data != Vector256.Create(MemoryMarshal.Cast<char, ushort>(remaining)))
                    XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndTagName, line, column);

                _stream.Advance(Vector256<ushort>.Count);
                // A surrogate pair counts as one column
                var lowSurrogates = Vector256.Equals(data & Vector256.Create((ushort)0xFC00), Vector256.Create((ushort)0xDC00));
                _column += Vector256<ushort>.Count - BitOperations.PopCount(lowSurrogates.ExtractMostSignificantBits());
                remaining = remaining.Slice(Vector256<ushort>.Count);
            }
        }

        char c;
        foreach (var expected in remaining)
        {
            c = ReadNext();
            if (c != expected)
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndTagName, line, column);

            // A surrogate pair counts as one column
            if (char.IsLowSurrogate(c))
                _column--;
        }

        c = ReadNext();

        SkipSpaces(ref c);

        if (c != '>')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingClosingLessThan);

        _handler.OnEndTag(name, line, column);
    }

    private void ParseUnsupportedXmlDirective()
        => ThrowInvalidXml(XmlThrow.UnsupportedXmlDirective);

    private void ParseDocumentTypeDeclaration()
    {
        if (!_isInProlog || _hasDocumentTypeDeclaration)
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

        _hasDocumentTypeDeclaration = true;

        var c = 'D';
        foreach (var expected in "OCTYPE")
        {
            c = ReadNext();
            if (c != expected)
                ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
        }

        c = ReadNext();
        ExpectSpaces(ref c);
        if (!TryParseName(ref c))
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

        ClearCharacterBuffer();
        SkipSpaces(ref c);

        if (c == '>')
            return;

        if (c == '[')
        {
            ParseDtdInternalSubset();
            return;
        }

        ParseDtdExternalId(ref c);
        SkipSpaces(ref c);

        if (c == '>')
            return;

        if (c == '[')
        {
            ParseDtdInternalSubset();
            return;
        }

        ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
    }

    private void ParseDtdExternalId(ref char c)
    {
        if (c == 'S')
        {
            ExpectDtdKeyword("SYSTEM", ref c);
            ExpectSpaces(ref c);
            ParseDtdLiteral(ref c, false);
            return;
        }

        if (c == 'P')
        {
            ExpectDtdKeyword("PUBLIC", ref c);
            ExpectSpaces(ref c);
            ParseDtdLiteral(ref c, true);
            ExpectSpaces(ref c);
            ParseDtdLiteral(ref c, false);
            return;
        }

        ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
    }

    private void ExpectDtdKeyword(string keyword, ref char c)
    {
        foreach (var expected in keyword)
        {
            if (c != expected)
                ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

            c = ReadNext();
        }
    }

    private void ParseDtdLiteral(ref char c, bool isPublicIdentifier)
    {
        if (c != '"' && c != '\'')
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

        var delimiter = c;
        while (true)
        {
            c = ReadNext();

        ProcessCharacter:
            if (c == delimiter)
            {
                c = ReadNext();
                return;
            }

            switch (c)
            {
                case '\n':
                    _line++;
                    _column = -1;
                    break;
                case '\r':
                    c = ReadNext();
                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessCharacter;
                    }

                    break;
                default:
                    if (XmlChar.IsChar(c) && (!isPublicIdentifier || IsPublicIdentifierChar(c)))
                    {
                        break;
                    }

                    if (char.IsHighSurrogate(c))
                    {
                        ReadLowSurrogate();
                        break;
                    }

                    ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
                    break;
            }
        }
    }

    private void ParseDtdInternalSubset()
    {
        var c = ReadNext();
        while (true)
        {
        ProcessCharacter:
            switch (c)
            {
                case ']':
                    c = ReadNext();
                    SkipSpaces(ref c);
                    if (c != '>')
                        ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

                    return;
                case '\'':
                case '"':
                    ParseDtdLiteral(ref c, false);
                    goto ProcessCharacter;
                case '<':
                    c = ReadNext();
                    if (c == '!')
                    {
                        c = ReadNext();
                        if (c == '-')
                        {
                            ParseDtdComment();
                        }
                        else
                        {
                            ParseDtdMarkupDeclaration(ref c);
                        }
                    }
                    else if (c == '?')
                    {
                        ParseDtdProcessingInstruction();
                    }
                    else
                    {
                        ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
                    }

                    break;
                case '\n':
                    _line++;
                    _column = -1;
                    break;
                case '\r':
                    c = ReadNext();
                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessCharacter;
                    }

                    break;
                default:
                    if (XmlChar.IsChar(c))
                    {
                        break;
                    }

                    if (char.IsHighSurrogate(c))
                    {
                        ReadLowSurrogate();
                        break;
                    }

                    ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
                    break;
            }

            c = ReadNext();
        }
    }

    private void ParseDtdMarkupDeclaration(ref char c)
    {
        while (true)
        {
        ProcessCharacter:
            switch (c)
            {
                case '>':
                    return;
                case '\'':
                case '"':
                    ParseDtdLiteral(ref c, false);
                    goto ProcessCharacter;
                case '\n':
                    _line++;
                    _column = -1;
                    break;
                case '\r':
                    c = ReadNext();
                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessCharacter;
                    }

                    break;
                default:
                    if (XmlChar.IsChar(c))
                    {
                        break;
                    }

                    if (char.IsHighSurrogate(c))
                    {
                        ReadLowSurrogate();
                        break;
                    }

                    ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
                    break;
            }

            c = ReadNext();
        }
    }

    private void ParseDtdComment()
    {
        if (ReadNext() != '-')
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

        while (true)
        {
            var c = ReadNext();

        ProcessCharacter:
            switch (c)
            {
                case '-':
                    c = ReadNext();
                    if (c == '-')
                    {
                        if (ReadNext() != '>')
                            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

                        return;
                    }

                    goto ProcessCharacter;
                case '\n':
                    _line++;
                    _column = -1;
                    break;
                case '\r':
                    c = ReadNext();
                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessCharacter;
                    }

                    break;
                default:
                    if (XmlChar.IsCommentChar(c))
                    {
                        break;
                    }

                    if (char.IsHighSurrogate(c))
                    {
                        ReadLowSurrogate();
                        break;
                    }

                    ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
                    break;
            }
        }
    }

    private void ParseDtdProcessingInstruction()
    {
        var c = ReadNext();
        if (!TryParseName(ref c))
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);

        var target = GetTextSpan();
        if (target.Length == 3
            && (target[0] == 'x' || target[0] == 'X')
            && (target[1] == 'm' || target[1] == 'M')
            && (target[2] == 'l' || target[2] == 'L'))
        {
            ThrowInvalidXml(XmlThrow.InvalidDocumentTypeDeclaration);
        }

        ClearCharacterBuffer();
        ParseProcessingInstruction(0, 0, 0, 0, ref c, false);
    }

    private static bool IsPublicIdentifierChar(char c)
    {
        return char.IsAsciiLetterOrDigit(c)
            || c is ' ' or '\r' or '\n' or '-' or '\'' or '(' or ')' or '+' or ',' or '.' or '/' or ':' or '=' or '?' or ';' or '!' or '*' or '#' or '@' or '$' or '_' or '%';
    }

    private void ParseCData()
    {
        char c;
        foreach (var check in "CDATA[")
        {
            c = ReadNext();
            if (c != check)
                ThrowInvalidXml(XmlThrow.InvalidCDATAStart);
        }

        int startLine = _line;
        int startColumn = _column + 1;
        StartContent();

        while (true)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // Process the content of the attribute using SIMD
                while (_stream.TryPreviewChar128(out var data))
                {
                    var test = Vector128.Equals(data, Vector128.Create((ushort)']'));
                    test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)XmlChar.HIGH_SURROGATE_START));
                    test |= Vector128.LessThan(data, Vector128.Create((ushort)' '));
                    if (test != Vector128<ushort>.Zero)
                    {
                        break;
                    }

                    AppendContent(data);
                    _stream.Advance(Vector128<ushort>.Count);
                    _column += Vector128<ushort>.Count;
                }
            }

            c = ReadNext();

        ProcessNextChar:
            switch (c)
            {
                case ']':
                    c = ReadNext();

                    if (c == ']')
                    {
                        c = ReadNext();
                        if (c == '>')
                        {
                            _handler.OnCData(GetContentSpan(_lastNameStackIndex), startLine, startColumn);
                            ClearCharacterBuffer();
                            return;
                        }

                        AppendContent(']');
                    }

                    AppendContent(']');
                    goto ProcessNextChar;

                case '\n':
                    _line++;
                    _column = -1;
                    AppendContent(c);
                    break;
                case '\r':
                    // As per the XML spec, we need to normalize \r and \r\n to \n
                    AppendContent('\n');
                    c = ReadNext();

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessNextChar;
                    }

                    break;
                default:
                    if (XmlChar.IsCDATAChar(c))
                    {
                        AppendContent(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendContent(c);
                            AppendContent(ReadLowSurrogate());
                        }
                        else
                        {
                            ThrowInvalidXml(XmlThrow.InvalidCharacterFound);
                        }
                    }
                    break;
            }
        }
    }

    private void ParseComment()
    {
        var c = ReadNext();
        if (c != '-')
            ThrowInvalidXml(XmlThrow.InvalidCommentStart);

        int startLine = _line;
        int startColumn = _column + 1;
        StartContent();

        while (true)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // Process the content of the attribute using SIMD
                while (_stream.TryPreviewChar128(out var data))
                {
                    var test = Vector128.Equals(data, Vector128.Create((ushort)'-'));
                    test |= Vector128.GreaterThanOrEqual(data, Vector128.Create((ushort)XmlChar.HIGH_SURROGATE_START));
                    test |= Vector128.LessThan(data, Vector128.Create((ushort)' '));
                    if (test != Vector128<ushort>.Zero)
                    {
                        break;
                    }

                    AppendContent(data);
                    _stream.Advance(Vector128<ushort>.Count);
                    _column += Vector128<ushort>.Count;
                }
            }

            c = ReadNext();

        ProcessNextChar:
            switch (c)
            {
                case '-':
                    c = ReadNext();

                    if (c == '-')
                    {
                        c = ReadNext();
                        if (c != '>')
                            ThrowInvalidXml(XmlThrow.InvalidDoubleDashFoundExpectingClosingLessThan);

                        var span = GetContentSpan(_lastNameStackIndex);
                        _handler.OnComment(span, startLine, startColumn);
                        ClearCharacterBuffer();
                        return;
                    }

                    AppendContent('-');
                    goto ProcessNextChar;

                case '\n':
                    _line++;
                    _column = -1;
                    AppendContent(c);
                    break;

                case '\r':
                    // As per the XML spec, we need to normalize \r and \r\n to \n
                    AppendContent('\n');
                    c = ReadNext();

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessNextChar;
                    }

                    break;

                default:
                    if (XmlChar.IsCommentChar(c))
                    {
                        AppendContent(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendContent(c);
                            AppendContent(ReadLowSurrogate());
                        }
                        else
                        {
                            ThrowInvalidXml(XmlThrow.InvalidCharacterFound);
                        }
                    }
                    break;
            }
        }
    }

    private void ParseXmlDeclarationOrProcessingInstruction()
    {
        var startLine = _line;
        var startColumn = _column;

        char c = ReadNext();

        var targetStart = _length;
        if (!TryParseName(ref c))
            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingXml);

        var targetLength = _length - targetStart;
        var target = GetTextSpan(targetStart).Slice(0, targetLength);
        if (target.SequenceEqual("xml".AsSpan()))
        {
            if (_xmlParsingBody)
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidXMLDeclarationMustBeFirst, startLine, startColumn);

            ClearCharacterBuffer();
            ParseXmlDeclaration(startLine, startColumn, ref c);
            return;
        }

        if (target.Length == 3
            && (target[0] == 'x' || target[0] == 'X')
            && (target[1] == 'm' || target[1] == 'M')
            && (target[2] == 'l' || target[2] == 'L'))
        {
            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingXml);
        }

        ParseProcessingInstruction(targetStart, targetLength, startLine, startColumn, ref c);
    }

    private void ParseProcessingInstruction(int targetStart, int targetLength, int startLine, int startColumn, ref char c, bool reportInstruction = true)
    {
        var dataStart = _length;
        var hasDataSeparator = XmlChar.IsWhiteSpace(c);
        if (reportInstruction)
        {
            // The data starts at the character that the caller already read.
            StartContent(1);
        }

        if (c == '?')
        {
            c = ReadNext();
            if (c == '>')
            {
                if (reportInstruction)
                {
                    _handler.OnProcessingInstruction(GetTextSpan(targetStart).Slice(0, targetLength), ReadOnlySpan<char>.Empty, startLine, startColumn);
                }

                ClearCharacterBuffer();
                return;
            }

            if (reportInstruction)
                AppendContent('?');
        }

        while (true)
        {
        ProcessNextChar:
            switch (c)
            {
                case '?':
                    c = ReadNext();
                    if (c == '>')
                    {
                        if (!hasDataSeparator)
                            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingWhitespaceOrQuestionGreaterThan);

                        if (reportInstruction)
                        {
                            _handler.OnProcessingInstruction(GetTextSpan(targetStart).Slice(0, targetLength), GetContentSpan(dataStart), startLine, startColumn);
                        }

                        ClearCharacterBuffer();
                        return;
                    }

                    if (reportInstruction)
                        AppendContent('?');

                    goto ProcessNextChar;
                case '\n':
                    _line++;
                    _column = -1;
                    if (reportInstruction)
                        AppendContent(c);

                    break;
                case '\r':
                    if (!TryReadNext(ref c))
                        ThrowInvalidXml(XmlThrow.InvalidEndOfXMLStream);

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                        if (reportInstruction)
                        {
                            AppendContent('\r');
                            AppendContent(c);
                        }
                    }
                    else
                    {
                        _column = 0;
                        if (reportInstruction)
                            AppendContent('\r');

                        goto ProcessNextChar;
                    }

                    break;
                default:
                    if (XmlChar.IsChar(c))
                    {
                        if (reportInstruction)
                            AppendContent(c);

                        break;
                    }

                    if (char.IsHighSurrogate(c))
                    {
                        var lowSurrogate = ReadLowSurrogate();
                        if (reportInstruction)
                        {
                            AppendContent(c);
                            AppendContent(lowSurrogate);
                        }

                        break;
                    }

                    ThrowInvalidXml(XmlThrow.InvalidCharacterFound);
                    break;
            }

            c = ReadNext();
        }
    }

    private void ParseXmlDeclaration(int startLine, int startColumn, ref char c)
    {
        // Prolog
        // [22]    prolog    ::=    XMLDecl? Misc* (doctypedecl Misc*)?
        // [23]    XMLDecl    ::=    '<?xml' VersionInfo EncodingDecl? SDDecl? S? '?>'
        // [24]    VersionInfo    ::=    S 'version' Eq ("'" VersionNum "'" | '"' VersionNum '"')
        // [25]    Eq    ::=    S? '=' S?
        // [26]    VersionNum    ::=    '1.' [0-9]+
        // [27]    Misc    ::=    Comment | PI | S

        ExpectSpaces(ref c);

        var line = _line;
        var column = _column;
        // Parse version
        if (!TryParseName(ref c) || !GetTextSpan().SequenceEqual("version".AsSpan()))
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingVersionAttribute, line, column);

        ClearCharacterBuffer();

        SkipSpaces(ref c);
        if (c != '=')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingEqual);

        c = ReadNext();
        SkipSpaces(ref c);
        var version = ParseAttributeValue(c);

        c = ReadNext();
        var hasSpaces = SkipSpaces(ref c);

        ReadOnlySpan<char> encoding = ReadOnlySpan<char>.Empty;

        // Parse Encoding
        if (c == 'e')
        {
            if (!hasSpaces)
                ThrowInvalidXml(XmlThrow.InvalidCharacterAfterVersionExpectingWhitespace);

            int indexEncoding = _length;

            line = _line;
            column = _column;
            if (!TryParseName(ref c) || !GetTextSpan(indexEncoding).SequenceEqual("encoding".AsSpan()))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingEncodingAttribute, line, column);

            SkipSpaces(ref c);
            if (c != '=')
                ThrowInvalidXml(XmlThrow.InvalidCharacterFoundAfterEncodingExpectingEqual);

            c = ReadNext();
            SkipSpaces(ref c);
            encoding = ParseAttributeValue(c);

            c = ReadNext();
            hasSpaces = SkipSpaces(ref c);
        }

        ReadOnlySpan<char> standalone = ReadOnlySpan<char>.Empty;
        if (c == 's')
        {
            if (!hasSpaces)
                ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingWhitespace);

            int indexStandalone = _length;

            line = _line;
            column = _column;
            SkipSpaces(ref c);
            if (!TryParseName(ref c) || !GetTextSpan(indexStandalone).SequenceEqual("standalone".AsSpan()))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidInstructionExpectingStandaloneAttribute, line, column);

            if (c != '=')
                ThrowInvalidXml(XmlThrow.InvalidCharacterFoundAfterStandaloneExpectingEqual);

            c = ReadNext();
            SkipSpaces(ref c);
            standalone = ParseAttributeValue(c);

            c = ReadNext();
            SkipSpaces(ref c);
        }

        if (c != '?')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingQuestionGreaterThan);
        c = ReadNext();

        if (c != '>')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingQuestionGreaterThan);

        _handler.OnXmlDeclaration(version, encoding, standalone, startLine, startColumn);
        ClearCharacterBuffer();
    }

    private bool HasElementsInStack => _lastNameStackIndex > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> PopTagName()
    {
        var buffer = _buffer;
        var length = _length;
        Debug.Assert(length - 2 >= 0);
        // The name length is stored as two characters, low 16 bits first (see AppendInt).
        var nameLength = buffer[length - 2] | (buffer[length - 1] << 16);
        var lastNameIndex = length - nameLength - 2;
        _length = lastNameIndex;
        _lastNameStackIndex = lastNameIndex;
        return new ReadOnlySpan<char>(buffer, lastNameIndex, nameLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Resize(int minSizeAdd = 1)
    {
        var buffer = _buffer;
        var length = _length;
        var newBuffer = ArrayPool<char>.Shared.Rent(Math.Max(buffer.Length * 2, buffer.Length + minSizeAdd));
        Array.Copy(buffer, newBuffer, length);
        ArrayPool<char>.Shared.Return(buffer);
        _buffer = newBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacter(char c)
    {
        Debug.Assert(_sliceStart < 0);
        var charBuffer = _buffer;
        var charBufferLength = _length;
        if ((uint)charBufferLength < (uint)charBuffer.Length)
        {
            charBuffer[charBufferLength] = c;
            _length = charBufferLength + 1;
        }
        else
        {
            AppendCharacterAfterResize(c);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendCharacterAfterResize(char c)
    {
        Resize();
        _buffer[_length++] = c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendInt(int value)
    {
        var buffer = _buffer;
        var length = _length;
        if (length + 2 > buffer.Length)
        {
            Resize(2);
            buffer = _buffer;
        }

        buffer[length] = (char)value;
        buffer[length + 1] = (char)(value >> 16);
        _length = length + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendValidRune(Rune rune)
    {
        if (rune.IsBmp)
        {
            AppendContent((char)rune.Value);
        }
        else
        {
            var highSurrogate = (char)(0xD7C0 + (rune.Value >> 10));
            var lowSurrogate = (char)(0xDC00 + (rune.Value & 0x3FF));
            AppendContent(highSurrogate);
            AppendContent(lowSurrogate);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacters(Vector128<ushort> span)
    {
        Debug.Assert(_sliceStart < 0);
        var charBuffer = _buffer;
        var charBufferLength = _length;
        if (SpanRange.Fits(charBufferLength, Vector128<ushort>.Count, charBuffer.Length))
        {
            span.CopyTo(MemoryMarshal.Cast<char, ushort>(charBuffer.AsSpan(charBufferLength, Vector128<ushort>.Count)));
            _length = charBufferLength + Vector128<ushort>.Count;
        }
        else
        {
            AppendCharactersAfterResize(span);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendCharactersAfterResize(Vector128<ushort> span)
    {
        Resize(Vector128<ushort>.Count);
        span.CopyTo(MemoryMarshal.Cast<char, ushort>(_buffer.AsSpan(_length, Vector128<ushort>.Count)));
        _length += Vector128<ushort>.Count;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacters(Vector256<ushort> span)
    {
        Debug.Assert(_sliceStart < 0);
        var charBuffer = _buffer;
        var charBufferLength = _length;
        if (SpanRange.Fits(charBufferLength, Vector256<ushort>.Count, charBuffer.Length))
        {
            span.CopyTo(MemoryMarshal.Cast<char, ushort>(charBuffer.AsSpan(charBufferLength, Vector256<ushort>.Count)));
            _length = charBufferLength + Vector256<ushort>.Count;
        }
        else
        {
            AppendCharactersAfterResize(span);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendCharactersAfterResize(Vector256<ushort> span)
    {
        Resize(Vector256<ushort>.Count);
        span.CopyTo(MemoryMarshal.Cast<char, ushort>(_buffer.AsSpan(_length, Vector256<ushort>.Count)));
        _length += Vector256<ushort>.Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearCharacterBuffer()
    {
        _length = _lastNameStackIndex;
        _sliceStart = -1;
    }

    private void FlushCharacterBuffer()
    {
        if (_sliceStart >= 0)
        {
            var slice = GetContentSpan(_lastNameStackIndex);
            if (slice.Length > 0)
            {
                // [27] Misc ::= Comment | PI | S, so only whitespace is allowed outside the root element
                if (_rejectFragments && !HasElementsInStack && slice.ContainsAnyExcept(" \t\r\n"))
                    XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidTextOutsideRootElement, _contentLine, _contentColumn);

                _handler.OnText(slice, _contentLine, _contentColumn);
            }
            return;
        }

        var lastNameStackIndex = _lastNameStackIndex;
        var charBufferLength = _length;
        var length = charBufferLength - lastNameStackIndex;
        if (length > 0)
        {
            var text = new ReadOnlySpan<char>(_buffer, lastNameStackIndex, length);

            // [27] Misc ::= Comment | PI | S, so only whitespace is allowed outside the root element
            if (_rejectFragments && !HasElementsInStack && text.ContainsAnyExcept(" \t\r\n"))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidTextOutsideRootElement, _contentLine, _contentColumn);

            _handler.OnText(text, _contentLine, _contentColumn);
            _length = lastNameStackIndex;
        }
    }

    /// <summary>
    /// Starts a content (text, CDATA, comment, attribute value or processing instruction data) at the next character to read,
    /// or <paramref name="charsBack"/> characters before it. For a string input, the content stays a slice of the input
    /// until <see cref="AppendContent(char)"/> gets a character that differs from the input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartContent(int charsBack = 0)
    {
        if (_stream.TryGetText(out _, out var index))
        {
            _sliceStart = index - charsBack;
            _sliceLength = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendContent(char c)
    {
        if (_stream.TryGetText(out var text, out _) && _sliceStart >= 0)
        {
            var next = _sliceStart + _sliceLength;
            if ((uint)next < (uint)text.Length && text[next] == c)
            {
                _sliceLength++;
                return;
            }

            CopySlice(text);
        }

        AppendCharacter(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendContent(Vector128<ushort> data)
    {
        // The data is the input at the current position, so it extends the slice when the slice ends there.
        if (_stream.TryGetText(out var text, out var index) && _sliceStart >= 0)
        {
            if (_sliceStart + _sliceLength == index)
            {
                _sliceLength += Vector128<ushort>.Count;
                return;
            }

            CopySlice(text);
        }

        AppendCharacters(data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendContent(Vector256<ushort> data)
    {
        if (_stream.TryGetText(out var text, out var index) && _sliceStart >= 0)
        {
            if (_sliceStart + _sliceLength == index)
            {
                _sliceLength += Vector256<ushort>.Count;
                return;
            }

            CopySlice(text);
        }

        AppendCharacters(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopySlice(string text)
    {
        var slice = text.AsSpan(_sliceStart, _sliceLength);
        _sliceStart = -1;
        if (_length + slice.Length > _buffer.Length)
        {
            Resize(slice.Length);
        }

        slice.CopyTo(_buffer.AsSpan(_length));
        _length += slice.Length;
    }

    /// <summary>
    /// Gets the content that started at the buffer position <paramref name="index"/>, and ends it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetContentSpan(int index)
    {
        if (_stream.TryGetText(out var text, out _) && _sliceStart >= 0)
        {
            Debug.Assert(_length == index);
            var span = text.AsSpan(_sliceStart, _sliceLength);
            _sliceStart = -1;
            return span;
        }

        return GetTextSpan(index);
    }

    [DoesNotReturn]
    private void ThrowInvalidEndOfXmlStream()
        => XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndOfXMLStream, _line, _column);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadNext()
    {
        _column++;

        char c = default;
        if (!_stream.TryReadNext(ref c))
            ThrowInvalidEndOfXmlStream();
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadLowSurrogate()
    {
        char c = default;
        if (!_stream.TryReadNext(ref c))
            ThrowInvalidEndOfXmlStream();

        if (!char.IsLowSurrogate(c))
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingLowSurrogate);

        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadNext(ref char c)
    {
        _column++;
        return _stream.TryReadNext(ref c);
    }

    [DoesNotReturn]
    private void ThrowInvalidXml(XmlThrow xmlThrow)
        => XmlThrowHelper.ThrowInvalidXml(xmlThrow, _line, _column);
}
