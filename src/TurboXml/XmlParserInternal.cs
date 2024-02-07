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
/// <typeparam name="TConfigSimd">The configuration for SIMD.</typeparam>
/// <typeparam name="TConfigTagBeginEnd">The configuration for Begin/End tag.</typeparam>
internal ref struct XmlParserInternal<THandler, TCharProvider, TConfigSimd, TConfigTagBeginEnd>
    where THandler : IXmlReadHandler
    where TCharProvider: ICharProvider
    where TConfigSimd : IXmlConfigItem
    where TConfigTagBeginEnd : IXmlConfigItem
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
        _stackNames = TConfigTagBeginEnd.Enabled ? ArrayPool<char>.Shared.Rent(128) : Array.Empty<char>();
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
        catch (InternalXmlException e)
        {
            _handler.OnError(e.Message, e.Line, e.Column);
        }
    }

    private void ParseImpl()
    {
        while (true)
        {
            if (Vector128.IsHardwareAccelerated && TConfigSimd.Enabled)
            {
                // Process the content of the attribute using SIMDc
                while (_stream.TryPreviewChar128(out var data))
                {
                    // If there is a surrogate character, we need to stop the parsing
                    var test = Vector128.LessThan(data, Vector128.Create((ushort)' '));
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
                    ValidateAndAppendCharacter(c);
                    break;

            }
        }
        FlushCharacterBuffer();

        // Flush any errors of opened tags
        if (TConfigTagBeginEnd.Enabled)
        {
            while (HasElementsInStack)
            {
                var name = PopTagName();
                _handler.OnError($"Invalid tag {name} not closed at the end of the document.", _line, _column);
            }
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
            ThrowInvalidXml($"Invalid start tag name", line, column);

        var name = GetTextSpan();
        _handler.OnBeginTag(name, line, column);

        if (TConfigTagBeginEnd.Enabled)
        {
            PushTagName(name);
        }

        ClearCharacterBuffer();

        while(true)
        {
            var hasSpaces = SkipSpaces(ref c);

            switch (c)
            {
                case '>':
                    return;
                case '/':
                    c = ReadNext();

                    if (c != '>')
                        ThrowInvalidXml($"Invalid character `{c}` after /");

                    _handler.OnEndTagEmpty();

                    // We pop the tag name as it is an empty tag
                    // No need to check if the stack is empty as we are in a start tag
                    if (TConfigTagBeginEnd.Enabled)
                    {
                        PopTagName();
                    }
                    return;
                default:
                    if (!hasSpaces)
                        ThrowInvalidXml($"Invalid character found. Expecting a whitespace or />");

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
        else if (char.IsHighSurrogate(c))
        {
            var rune = new Rune(c, ReadLowSurrogate());
            if (!XmlChar.IsNameStartChar(rune))
                ThrowInvalidXml("Invalid Unicode character found while parsing name");

            AppendRune(rune);
        }
        else
        {
            return false;
        }

        // Try batch to read a name
        if (Vector128.IsHardwareAccelerated && TConfigSimd.Enabled)
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
            else if (char.IsHighSurrogate(c))
            {
                var rune = new Rune(c, ReadLowSurrogate());

                if (!XmlChar.IsNameChar(rune))
                    ThrowInvalidXml("Invalid Unicode character found while parsing name");

                AppendRune(rune);
            }
            else
            {
                break;
            }
        }

        return true;
    }

    private void ParseAttribute(char c)
    {
        // [41]    Attribute    ::=    Name Eq AttValue
        int nameLine = _line;
        int nameColumn = _column;
        if (!TryParseName(ref c))
            ThrowInvalidXml($"Invalid attribute name");

        // We don't clear the buffer after parsing the attribute name as we are going to use it for the attribute value
        var attributeName = GetTextSpan();

        SkipSpaces(ref c);
        if (c != '=')
            ThrowInvalidXml($"Invalid character after attribute name");

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
            ThrowInvalidXml($"Invalid attribute value character after =. Expecting a simple quote ' or double quote \"");

        var startChar = c;
        int startIndex = _charBufferLength;

        while (true)
        {
            if (Vector128.IsHardwareAccelerated && TConfigSimd.Enabled)
            {
                // Process the content of the attribute using SIMD
                while (_stream.TryPreviewChar128(out var data))
                {
                    // If there is a surrogate character, we need to stop the parsing
                    var test = Vector128.Equals(data, Vector128.Create((ushort)startChar));
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

            c = ReadNext();

            ProcessNextChar:
            switch (c)
            {
                case '<':
                    ThrowInvalidXml("Invalid character < in attribute value");
                    break;
                case '&':
                    ParseEntity(ref c);
                    break;
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

                    ValidateAndAppendCharacter(c);
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateAndAppendCharacter(char c)
    {
        if (!XmlChar.IsValid(c))
            ThrowInvalidXml($"Invalid character \\u{(ushort)c:X4}");

        AppendCharacter(c);
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

            if (c == 'x')
            {
                c = ReadNext();

                if (!XmlChar.TryGetHexDigit(c, out int charCodePoint))
                    ThrowInvalidXml($"Invalid hex digit");

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

                if (!Rune.IsValid(charCodePoint))
                    ThrowInvalidXml($"Invalid character \\x{charCodePoint:X}", line, column);

                AppendRune(Unsafe.BitCast<int, Rune>(charCodePoint));
            }
            else
            {
                int charCodePoint = c - '0';
                if ((uint)charCodePoint > 9)
                    ThrowInvalidXml($"Invalid digit");

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

                if (!Rune.IsValid(charCodePoint))
                    ThrowInvalidXml($"Invalid character \\x{charCodePoint:X}", line, column);

                AppendRune(Unsafe.BitCast<int, Rune>(charCodePoint));
            }

            if (c != ';')
                ThrowInvalidXml($"Invalid character found for character reference. Expecting a closing ;");
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
            ThrowInvalidXml($"Invalid entity name. Only &lt; or &gt; or &amp; or &apos; or &quot; are supported", line, column);

        c = ReadNext();
        if (c != ';')
            ThrowInvalidXml($"Invalid character after entity reference. Expecting a closing ;");

        AppendCharacter(rc);
    }

    private void ExpectSpaces(ref char c)
    {
        if (!XmlChar.IsWhiteSpace(c))
            ThrowInvalidXml($"Invalid character. Expecting a whitespace");

        SkipSpaces(ref c);
    }

    private bool SkipSpaces(ref char c)
    {
        bool hasSpaces = false;
        while (true)
        {
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
                        return true;
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
            ThrowInvalidXml($"Invalid end tag name", line, column);

        SkipSpaces(ref c);

        if (c != '>')
            ThrowInvalidXml($"Invalid character. Expecting a closing >");

        var name = GetTextSpan();
        if (TConfigTagBeginEnd.Enabled)
        {
            if (!HasElementsInStack || !PopTagName().SequenceEqual(name))
                ThrowInvalidXml($"Invalid end tag. No matching start tag found for {name.ToString()}", line, column);
        }

        _handler.OnEndTag(name, line, column);
        ClearCharacterBuffer();
    }

    private void ParseUnsupportedXmlDirective()
        => ThrowInvalidXml("Unsupported XML directive starting with !");

    private void ParseCData()
    {
        char c;
        foreach (var check in "CDATA[")
        {
            c = ReadNext();
            if (c != check)
                ThrowInvalidXml($"Invalid CDATA start");
        }

        int startLine = _line;
        int startColumn = _column + 1;

        while (true)
        {
            if (Vector128.IsHardwareAccelerated && TConfigSimd.Enabled)
            {
                // Process the content of the attribute using SIMD
                while (_stream.TryPreviewChar128(out var data))
                {
                    var test = Vector128.Equals(data, Vector128.Create((ushort)']'));
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
                    ValidateAndAppendCharacter(c);
                    break;
            }
        }
    }

    private void ParseComment()
    {
        var c = ReadNext();
        if (c != '-')
            ThrowInvalidXml($"Invalid comment start");

        int startLine = _line;
        int startColumn = _column + 1;

        while (true)
        {
            if (Vector128.IsHardwareAccelerated && TConfigSimd.Enabled)
            {
                // Process the content of the attribute using SIMD
                while (_stream.TryPreviewChar128(out var data))
                {
                    var test= Vector128.Equals(data, Vector128.Create((ushort)'-'));
                    test |= Vector128.Equals(data, Vector128.Create((ushort)'\r'));
                    test |= Vector128.Equals(data, Vector128.Create((ushort)'\n'));
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
                            ThrowInvalidXml("Invalid character found after --. Expecting a closing >");

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
                    AppendCharacter(c);
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
            ThrowInvalidXml($"Invalid XML declaration. It must be the first node in the document");

        var startLine = _line;
        var startColumn = _column;

        char c = ReadNext();
        if (c != 'x')
            ThrowInvalidXml($"Invalid processing instruction name. Expecting <?xml");

        c= ReadNext();
        if (c != 'm')
            ThrowInvalidXml($"Invalid processing instruction name. Expecting <?xml");

        c = ReadNext();
        if (c != 'l')
            ThrowInvalidXml($"Invalid processing instruction name. Expecting <?xml");

        c = ReadNext();
        ExpectSpaces(ref c);

        var line = _line;
        var column = _column;
        // Parse version
        if (!TryParseName(ref c) || !GetTextSpan().SequenceEqual("version".AsSpan()))
            ThrowInvalidXml($"Invalid processing instruction name. Expecting XML version attribute.", line, column);

        ClearCharacterBuffer();

        SkipSpaces(ref c);
        if (c != '=')
            ThrowInvalidXml($"Invalid character after version. Expecting =");

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
                ThrowInvalidXml($"Invalid character after version. Expecting a whitespace");

            int indexEncoding = _charBufferLength;

            line = _line;
            column = _column;
            if (!TryParseName(ref c) || !GetTextSpan(indexEncoding).SequenceEqual("encoding".AsSpan()))
                ThrowInvalidXml($"Invalid processing instruction name. Expecting XML encoding attribute", line, column);

            SkipSpaces(ref c);
            if (c != '=')
                ThrowInvalidXml($"Invalid character after encoding. Expecting =");

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
                ThrowInvalidXml($"Invalid character. Expecting a whitespace");

            int indexStandalone = _charBufferLength;

            line = _line;
            column = _column;
            SkipSpaces(ref c);
            if (!TryParseName(ref c) || !GetTextSpan(indexStandalone).SequenceEqual("standalone".AsSpan()))
                ThrowInvalidXml($"Invalid processing instruction name. Expecting XML standalone attribute ", line, column);

            if (c != '=')
                ThrowInvalidXml($"Invalid character after standalone. Expecting =");

            c = ReadNext();
            SkipSpaces(ref c);
            standalone = ParseAttributeValue(c);

            c = ReadNext();
            SkipSpaces(ref c);
        }

        if (c != '?')
            ThrowInvalidXml($"Invalid character after processing instruction attributes. Expecting ?>");
        c = ReadNext();

        if (c != '>')
            ThrowInvalidXml($"Invalid character after processing instruction attributes. Expecting > after ?");
        
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSize(int minSizeAdd)
    {
        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        if (charBufferLength + minSizeAdd > charBuffer.Length)
        {
            var newBuffer = ArrayPool<char>.Shared.Rent(Math.Max(charBuffer.Length * 2, charBuffer.Length + minSizeAdd));
            Array.Copy(charBuffer, newBuffer, charBufferLength);
            ArrayPool<char>.Shared.Return(charBuffer);
            _charBuffer = newBuffer;
        }
    }

    private void Resize()
    {
        var charBuffer = _charBuffer;
        var newBuffer = ArrayPool<char>.Shared.Rent(charBuffer.Length * 2);
        Array.Copy(charBuffer, newBuffer, _charBufferLength);
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

    private void AppendRune(Rune rune)
    {
        EnsureSize(2);

        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        if (rune.IsBmp)
        {
            charBuffer[charBufferLength++] = (char)rune.Value;
        }
        else
        {
            var length = rune.EncodeToUtf16(new Span<char>(charBuffer, charBufferLength, charBuffer.Length - charBufferLength));
            charBufferLength += length;
        }
        _charBufferLength = charBufferLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCharacters(Vector128<ushort> span)
    {
        EnsureSize(Vector128<ushort>.Count);

        var charBuffer = _charBuffer;
        var charBufferLength = _charBufferLength;
        Unsafe.As<char, Vector128<ushort>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(charBuffer), charBufferLength)) = span;
        _charBufferLength = charBufferLength + Vector128<ushort>.Count;
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


    [DoesNotReturn]
    private void ThrowInvalidEndOfXmlStream()
        => ThrowInvalidXml($"Invalid end of XML stream");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadNext()
    {
        char c;
        if (!_stream.TryReadNext(out c))
            ThrowInvalidEndOfXmlStream();

        _column++;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char ReadLowSurrogate()
    {
        char c;
        if (!_stream.TryReadNext(out c))
            ThrowInvalidEndOfXmlStream();

        if (!char.IsLowSurrogate(c))
            ThrowInvalidXml("Invalid Unicode low surrogate character found");

        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadNext(out char c)
    {
        if (_stream.TryReadNext(out c))
        {
            _column++;
            return true;
        }
        return false;
    }

    [DoesNotReturn]
    private void ThrowInvalidXml(string message)
    {
        throw new InternalXmlException(message, _line, _column);
    }

    [DoesNotReturn]
    private static void ThrowInvalidXml(string message, int line, int column)
    {
        throw new InternalXmlException(message, line, column);
    }

    private class InternalXmlException(string message, int line, int column) : Exception(message)
    {
        public int Line { get; } = line;

        public int Column { get; } = column;
    }
}
