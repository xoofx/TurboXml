// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System;
using System.Xml.Linq;

namespace TurboXml;

internal enum XmlThrow
{
    InvalidBeginTag,
    InvalidEndTagEmpty,
    InvalidCharacterExpectingWhiteSpace,
    InvalidAttributeName,
    InvalidCharacterExpectingEqual,
    InvalidAttributeValueExpectingQuoteOrDoubleQuote,
    InvalidCharacterLessThanInAttributeValue,
    InvalidCharacterInAttributeValue,
    InvalidCharacterFound,
    InvalidHexDigit,
    InvalidDigit,
    InvalidCharacterFoundExpectingSemiComma,
    InvalidEntityName,
    InvalidCharacterFoundExpectingWhitespace,
    InvalidEndTagName,
    InvalidCharacterFoundExpectingClosingLessThan,
    InvalidEndTagNameFoundNotMatchingBeginTag,
    UnsupportedXmlDirective,
    InvalidCDATAStart,
    InvalidCommentStart,
    InvalidDoubleDashFoundExpectingClosingLessThan,
    InvalidXMLDeclarationMustBeFirst,
    InvalidProcessingInstructionExpectingXml,
    InvalidProcessingInstructionExpectingVersionAttribute,
    InvalidCharacterFoundExpectingEqual,
    InvalidCharacterAfterVersionExpectingWhitespace,
    InvalidProcessingInstructionExpectingEncodingAttribute,
    InvalidCharacterFoundAfterEncodingExpectingEqual,
    InvalidInstructionExpectingStandaloneAttribute,
    InvalidCharacterFoundAfterStandaloneExpectingEqual,
    InvalidCharacterFoundExpectingQuestionGreaterThan,
    InvalidEndOfXMLStream,
    InvalidCharacterFoundExpectingLowSurrogate
}

internal static class XmlThrowHelper
{
    [DoesNotReturn]
    public static void ThrowInvalidXml(XmlThrow xmlThrow, int line, int column)
    {
        throw new InternalXmlException(GetMessage(xmlThrow), line, column);
    }

    public class InternalXmlException(string message, int line, int column) : Exception(message)
    {
        public int Line { get; } = line;

        public int Column { get; } = column;
    }

    private static string GetMessage(XmlThrow xmlThrow)
    {
        return xmlThrow switch
        {
            XmlThrow.InvalidBeginTag => "Invalid begin tag",
            XmlThrow.InvalidEndTagEmpty => "Invalid character found after /. Expecting >",
            XmlThrow.InvalidCharacterExpectingWhiteSpace => "Invalid character found. Expecting a whitespace or />",
            XmlThrow.InvalidAttributeName => "Invalid attribute name",
            XmlThrow.InvalidCharacterExpectingEqual => "Invalid character found. Expecting =",
            XmlThrow.InvalidAttributeValueExpectingQuoteOrDoubleQuote => "Invalid attribute value character after =. Expecting a simple quote ' or double quote \"",
            XmlThrow.InvalidCharacterLessThanInAttributeValue => $"Invalid character < in attribute value",
            XmlThrow.InvalidCharacterInAttributeValue => "Invalid character found in attribute value",
            XmlThrow.InvalidCharacterFound => "Invalid character found",
            XmlThrow.InvalidHexDigit => "Invalid hex digit",
            XmlThrow.InvalidDigit => "Invalid digit",
            XmlThrow.InvalidCharacterFoundExpectingSemiComma => "Invalid character found. Expecting a ;",
            XmlThrow.InvalidEntityName => "Invalid entity name. Only &lt; or &gt; or &amp; or &apos; or &quot; are supported",
            XmlThrow.InvalidCharacterFoundExpectingWhitespace => "Invalid character found. Expecting a whitespace",
            XmlThrow.InvalidEndTagName => "Invalid end tag name",
            XmlThrow.InvalidCharacterFoundExpectingClosingLessThan => "Invalid character found. Expecting a closing <",
            XmlThrow.InvalidEndTagNameFoundNotMatchingBeginTag => "Invalid end tag. No matching start tag found",
            XmlThrow.UnsupportedXmlDirective => "Unsupported XML directive starting with !",
            XmlThrow.InvalidCDATAStart => "Invalid CDATA start",
            XmlThrow.InvalidCommentStart => "Invalid comment start",
            XmlThrow.InvalidDoubleDashFoundExpectingClosingLessThan => "Invalid character found after --. Expecting a closing >",
            XmlThrow.InvalidXMLDeclarationMustBeFirst => "Invalid XML declaration. It must be the first node in the document",
            XmlThrow.InvalidProcessingInstructionExpectingXml => "Invalid processing instruction name. Expecting <?xml",
            XmlThrow.InvalidProcessingInstructionExpectingVersionAttribute => "Invalid processing instruction name. Expecting XML version attribute.",
            XmlThrow.InvalidCharacterFoundExpectingEqual => "Invalid character found. Expecting =",
            XmlThrow.InvalidCharacterAfterVersionExpectingWhitespace => "Invalid character after version. Expecting a whitespace",
            XmlThrow.InvalidProcessingInstructionExpectingEncodingAttribute => "Invalid processing instruction name. Expecting XML encoding attribute",
            XmlThrow.InvalidCharacterFoundAfterEncodingExpectingEqual => "Invalid character after encoding. Expecting =",
            XmlThrow.InvalidInstructionExpectingStandaloneAttribute => "Invalid processing instruction name. Expecting XML standalone attribute ",
            XmlThrow.InvalidCharacterFoundAfterStandaloneExpectingEqual => "Invalid character after standalone. Expecting =",
            XmlThrow.InvalidCharacterFoundExpectingQuestionGreaterThan => "Invalid character after processing instruction attributes. Expecting ?>",
            XmlThrow.InvalidEndOfXMLStream => "Invalid end of XML stream",
            XmlThrow.InvalidCharacterFoundExpectingLowSurrogate => "Invalid character found. Expecting a low surrogate",
            _ => "Unexpected XML parsing error"
        };
    }
}
