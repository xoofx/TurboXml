// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
internal ref struct XmlParserInternal<THandler, TCharProvider>
    where THandler : IXmlReadHandler
    where TCharProvider: ICharProvider
{
    private int _line;
    private int _column;
    private ref THandler _handler;
    private ref TCharProvider _stream;
    private int _contentLine;
    private int _contentColumn;
    private bool _xmlParsingBody;

    private char[] _charBuffer;
    private int _charBufferLength;
    private char[] _stackNames;
    private int _stackLength;

    public XmlParserInternal(ref THandler handler, ref TCharProvider stream)
    {
        _handler = ref handler;
        _stream = ref stream;
        _charBuffer = ArrayPool<char>.Shared.Rent(128);
        _column = -1;
        _stackNames = ArrayPool<char>.Shared.Rent(128);
    }

    public void Dispose()
    {
        if (_charBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_charBuffer);
            _charBuffer = null!;
        }

        if (_stackNames != null)
        {
            ArrayPool<char>.Shared.Return(_stackNames);
            _stackNames = null!;
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
                    if (test != Vector128<ushort>.Zero)
                    {
                        break;
                    }

                    AppendCharacters(data);
                    _stream.Advance(Vector128<ushort>.Count);
                    _column += Vector128<ushort>.Count;
                }
            }

            if (!TryReadNext(out var c))
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
                        ParseXmlDeclaration();
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
                            ParseCData();
                        }
                        else
                            ParseUnsupportedXmlDirective();
                    }
                    else if (c == '/')
                    {
                        ParseEndTag();
                    }
                    else
                    {
                        ParseBeginTag(c);
                    }

                    _xmlParsingBody = true;

                    _contentLine = _line;
                    _contentColumn = _column + 1;

                    break;
                case '&':
                    _xmlParsingBody = true;
                    ParseEntity(ref c);
                    break;

                case '\n':
                    _xmlParsingBody = true;
                    _line++;
                    _column = -1;
                    AppendCharacter(c);
                    break;

                case '\r':
                    _xmlParsingBody = true;
                    AppendCharacter(c);

                    if (!TryReadNext(out c))
                    {
                        return;
                    }

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                        AppendCharacter(c);
                    }
                    else
                    {
                        _column = 0;
                        goto ProcessNextChar;
                    }

                    break;

                default:
                    _xmlParsingBody = true;

                    if (XmlChar.IsChar(c))
                    {
                        AppendCharacter(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendCharacter(c);
                            AppendCharacter(ReadLowSurrogate());
                        }
                        else
                        {
                            ThrowInvalidXml(XmlThrow.InvalidCharacterFound);
                        }
                    }
                    break;

            }
        }

        FlushCharacterBuffer();

        // Flush any errors of opened tags
        if (HasElementsInStack)
        {
            CheckLastClosingElements();
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
    private ReadOnlySpan<char> GetTextSpan(int index = 0)
    {
        return new ReadOnlySpan<char>(_charBuffer, index, _charBufferLength - index);
    }

    private void ParseBeginTag(char c)
    {
        // Start-tag
        // [40]    STag    ::=    '<' Name (S Attribute)* S? '>' [WFC: Unique Att Spec]

        int line = _line;
        int column = _column;
        if (!TryParseName(ref c))
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidBeginTag, line, column);

        var name = GetTextSpan();
        _handler.OnBeginTag(name, line, column);

        PushTagName(name);

        ClearCharacterBuffer();

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

        while (TryReadNext(out c))
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
        int startIndex = _charBufferLength;

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

                AppendCharacters(data);
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

                AppendCharacters(data);
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
                    AppendCharacter(c);
                    break;
                case '\r':
                    // As per the XML spec, we need to normalize \r and \r\n to \n
                    AppendCharacter('\n');
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
                        return GetTextSpan(startIndex);
                    }

                    if (XmlChar.IsAttrValueChar(c))
                    {
                        AppendCharacter(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendCharacter(c);
                            AppendCharacter(ReadLowSurrogate());
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

                while (TryReadNext(out c))
                {
                    if (XmlChar.TryGetHexDigit(c, out int newValue))
                    {
                        charCodePoint = (charCodePoint << 4) + newValue;
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

                while (TryReadNext(out c))
                {
                    var d = c - '0';
                    if ((uint)d <= 9)
                    {
                        charCodePoint = charCodePoint * 10 + d;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (!Rune.IsValid(charCodePoint))
                XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidCharacterFound, line, column);

            AppendValidRune(Unsafe.BitCast<int, Rune>(charCodePoint));

            if (c != ';')
                ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingSemiComma);
            return;
        }


        var rc = (char)0;

        switch (c)
        {
            case 'l':
                if (TryReadNext(out c) && c == 't')
                {
                    rc = '<';
                }

                break;
            case 'g':
                if (TryReadNext(out c) && c == 't')
                {
                    rc = '>';
                }

                break;
            case 'a':
                if (TryReadNext(out c))
                {
                    if (c == 'm' && TryReadNext(out c) && c == 'p')
                    {
                        rc = '&';
                    }
                    else if (c == 'p' && TryReadNext(out c) && c == 'o' && TryReadNext(out c) && c == 's')
                    {
                        rc = '\'';
                    }
                }

                break;
            case 'q':
                if (TryReadNext(out c) & c == 'u' && TryReadNext(out c) && c == 'o' && TryReadNext(out c) && c == 't')
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

        AppendCharacter(rc);
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
        var c = ReadNext();

        var column = _column;
        var line = _line;

        if (!TryParseName(ref c))
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndTagName, line, column);

        SkipSpaces(ref c);

        if (c != '>')
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingClosingLessThan);

        var name = GetTextSpan();
        if (!HasElementsInStack || !PopTagName().SequenceEqual(name))
            XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndTagNameFoundNotMatchingBeginTag, line, column);

        _handler.OnEndTag(name, line, column);
        ClearCharacterBuffer();
    }

    private void ParseUnsupportedXmlDirective()
        => ThrowInvalidXml(XmlThrow.UnsupportedXmlDirective);

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

                    AppendCharacters(data);
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
                            _handler.OnCData(GetTextSpan(), startLine, startColumn);
                            ClearCharacterBuffer();
                            return;
                        }

                        AppendCharacter(']');
                    }

                    AppendCharacter(']');
                    goto ProcessNextChar;

                case '\n':
                    _line++;
                    _column = -1;
                    AppendCharacter(c);
                    break;
                case '\r':
                    AppendCharacter(c);
                    c = ReadNext();

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                        AppendCharacter(c);
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
                        AppendCharacter(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendCharacter(c);
                            AppendCharacter(ReadLowSurrogate());
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

                    AppendCharacters(data);
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

                        var span = GetTextSpan();
                        _handler.OnComment(span, startLine, startColumn);
                        ClearCharacterBuffer();
                        return;
                    }

                    AppendCharacter('-');
                    goto ProcessNextChar;

                case '\n':
                    _line++;
                    _column = -1;
                    AppendCharacter(c);
                    break;

                case '\r':
                    AppendCharacter(c);
                    c = ReadNext();

                    _line++;
                    if (c == '\n')
                    {
                        _column = -1;
                        AppendCharacter(c);
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
                        AppendCharacter(c);
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            AppendCharacter(c);
                            AppendCharacter(ReadLowSurrogate());
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

    private void ParseXmlDeclaration()
    {
        // Prolog
        // [22]    prolog    ::=    XMLDecl? Misc* (doctypedecl Misc*)?
        // [23]    XMLDecl    ::=    '<?xml' VersionInfo EncodingDecl? SDDecl? S? '?>'
        // [24]    VersionInfo    ::=    S 'version' Eq ("'" VersionNum "'" | '"' VersionNum '"')
        // [25]    Eq    ::=    S? '=' S?
        // [26]    VersionNum    ::=    '1.' [0-9]+
        // [27]    Misc    ::=    Comment | PI | S

        if (_xmlParsingBody)
            ThrowInvalidXml(XmlThrow.InvalidXMLDeclarationMustBeFirst);

        var startLine = _line;
        var startColumn = _column;

        char c = ReadNext();
        if (c != 'x')
            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingXml);

        c = ReadNext();
        if (c != 'm')
            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingXml);

        c = ReadNext();
        if (c != 'l')
            ThrowInvalidXml(XmlThrow.InvalidProcessingInstructionExpectingXml);

        c = ReadNext();
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

            int indexEncoding = _charBufferLength;

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

            int indexStandalone = _charBufferLength;

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

    private void PushTagName(ReadOnlySpan<char> name)
    {
        var stackNames = _stackNames;
        var stackLength = _stackLength;
        var nameLength = name.Length;
        if (stackLength + nameLength > stackNames.Length)
        {
            var newBuffer = ArrayPool<char>.Shared.Rent(Math.Max(stackNames.Length * 2, stackNames.Length + nameLength));
            Array.Copy(stackNames, newBuffer, stackLength);
            ArrayPool<char>.Shared.Return(stackNames);
            stackNames = newBuffer;
            _stackNames = stackNames;
        }

        name.CopyTo(new Span<char>(stackNames, stackLength, nameLength));

        stackLength += nameLength;
        // We store the length of the name after the name
        ref int length = ref Unsafe.As<char, int>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(stackNames), stackLength));
        length = nameLength;
        _stackLength = stackLength + sizeof(int);
    }

    private bool HasElementsInStack => _stackLength > 0;

    private ReadOnlySpan<char> PopTagName()
    {
        var stackNames = _stackNames;
        var stackLength = _stackLength;
        Debug.Assert(stackLength - sizeof(int) >= 0);
        var length = Unsafe.As<char, int>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(stackNames), stackLength - sizeof(int)));
        _stackLength = stackLength - length - sizeof(int);
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(stackNames), _stackLength), length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Resize(int minSizeAdd = 1)
    {
        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        var newBuffer = ArrayPool<char>.Shared.Rent(Math.Max(charBuffer.Length * 2, charBuffer.Length + minSizeAdd));
        Array.Copy(charBuffer, newBuffer, charBufferLength);
        ArrayPool<char>.Shared.Return(charBuffer);
        _charBuffer = newBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacter(char c)
    {
        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        if (charBufferLength == charBuffer.Length)
        {
            Resize();
            charBuffer = _charBuffer;
        }

        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(charBuffer), charBufferLength++) = c;
        _charBufferLength = charBufferLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendValidRune(Rune rune)
    {
        if (rune.IsBmp)
        {
            AppendCharacter((char)rune.Value);
        }
        else
        {
            var highSurrogate = (char)(0xD7C0 + (rune.Value >> 10));
            var lowSurrogate = (char)(0xDC00 + (rune.Value & 0x3FF));
            AppendCharacter(highSurrogate);
            AppendCharacter(lowSurrogate);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacters(Vector128<ushort> span)
    {
        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        if (charBufferLength + Vector128<ushort>.Count > charBuffer.Length)
        {
            Resize(Vector128<ushort>.Count);
            charBuffer = _charBuffer;
        }
        Unsafe.As<char, Vector128<ushort>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(charBuffer), charBufferLength)) = span;
        _charBufferLength = charBufferLength + Vector128<ushort>.Count;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacters(Vector256<ushort> span)
    {
        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        if (charBufferLength + Vector256<ushort>.Count > charBuffer.Length)
        {
            Resize(Vector256<ushort>.Count);
            charBuffer = _charBuffer;
        }
        Unsafe.As<char, Vector256<ushort>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(charBuffer), charBufferLength)) = span;
        _charBufferLength = charBufferLength + Vector256<ushort>.Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearCharacterBuffer()
    {
        _charBufferLength = 0;
    }

    private void FlushCharacterBuffer()
    {
        var charBufferLength = _charBufferLength;
        if (charBufferLength > 0)
        {
            _handler.OnText(new ReadOnlySpan<char>(_charBuffer, 0, charBufferLength), _contentLine, _contentColumn);
            ClearCharacterBuffer();
        }
    }


    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInvalidEndOfXmlStream()
        => XmlThrowHelper.ThrowInvalidXml(XmlThrow.InvalidEndOfXMLStream, _line, _column);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadNext()
    {
        _column++;

        if (!_stream.TryReadNext(out char c))
            ThrowInvalidEndOfXmlStream();
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadLowSurrogate()
    {
        if (!_stream.TryReadNext(out char c))
            ThrowInvalidEndOfXmlStream();

        if (!char.IsLowSurrogate(c))
            ThrowInvalidXml(XmlThrow.InvalidCharacterFoundExpectingLowSurrogate);

        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadNext(out char c)
    {
        _column++;
        return _stream.TryReadNext(out c);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInvalidXml(XmlThrow xmlThrow)
        => XmlThrowHelper.ThrowInvalidXml(xmlThrow, _line, _column);
}
