// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;

namespace TurboXml;

/// <summary>
/// Internal interface to statically configure the parser.
/// </summary>
internal interface IXmlConfigItem
{
    /// <summary>
    /// Gets a value indicating whether this configuration item is enabled.
    /// </summary>
    static abstract bool Enabled { get; }

    /// <summary>
    /// An active configuration item.
    /// </summary>
    public readonly struct Active : IXmlConfigItem
    {
        /// <inheritdoc />
        public static bool Enabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => true;
        }
    }

    /// <summary>
    /// An inactive configuration item.
    /// </summary>
    public readonly struct Inactive : IXmlConfigItem
    {
        /// <inheritdoc />
        public static bool Enabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }
    }
}