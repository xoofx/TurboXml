// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.Intrinsics;

namespace TurboXml;

/// <summary>
/// Internal interface to provide characters to the parser.
/// </summary>
internal interface ICharProvider
{
    /// <summary>
    /// Tries to read the next character from the input.
    /// </summary>
    /// <param name="c">The character read if true is returned.</param>
    /// <returns><c>true</c> if a character was successfully read; otherwise <c>false</c></returns>
    bool TryReadNext(out char c);

    /// <summary>
    /// Tries to read a batch of 8 characters from the input.
    /// </summary>
    /// <param name="data">8 characters if true is returned.</param>
    /// <returns><c>true</c> if a batch of 8 characters was successfully read; otherwise <c>false</c></returns>
    bool TryPreviewChar128(out Vector128<ushort> data);

    /// <summary>
    /// Tries to read a batch of 8 characters from the input.
    /// </summary>
    /// <param name="data">8 characters if true is returned.</param>
    /// <returns><c>true</c> if a batch of 8 characters was successfully read; otherwise <c>false</c></returns>
    bool TryPreviewChar256(out Vector256<ushort> data);

    /// <summary>
    /// Advance the current position by the specified number of characters.
    /// </summary>
    void Advance(int countChars);
}