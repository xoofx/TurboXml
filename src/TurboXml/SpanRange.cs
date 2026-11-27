// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;

namespace TurboXml;

/// <summary>
/// Range test for the vector loads and stores.
/// </summary>
internal static class SpanRange
{
    /// <summary>
    /// Tests if <paramref name="count"/> elements from <paramref name="start"/> fit in <paramref name="length"/> elements.
    /// </summary>
    /// <remarks>
    /// This is the comparison that <c>AsSpan(start, count)</c> makes in a 64-bit process before it throws. When
    /// this method guards such a call, the JIT sees that the second comparison has the same result and removes
    /// it. The checked load or store then costs one comparison, like an unchecked one behind a length test.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Fits(int start, int count, int length) => (ulong)(uint)start + (ulong)(uint)count <= (ulong)(uint)length;
}
