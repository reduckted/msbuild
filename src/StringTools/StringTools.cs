// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

#if !NET35
using Microsoft.Extensions.ObjectPool;
#endif

namespace Microsoft.StringTools
{
    public static class Strings

    {
#if !NET35
        /// <summary>
        /// IPooledObjectPolicy used by <cref see="s_stringBuilderPool"/>.
        /// </summary>
        private class PooledObjectPolicy : IPooledObjectPolicy<SpanBasedStringBuilder>
        {
            /// <summary>
            /// No need to retain excessively long builders forever.
            /// </summary>
            private const int MAX_RETAINED_BUILDER_CAPACITY = 1000;

            /// <summary>
            /// Creates a new SpanBasedStringBuilder with the default capacity.
            /// </summary>
            public SpanBasedStringBuilder Create()
            {
                return new SpanBasedStringBuilder();
            }

            /// <summary>
            /// Returns a builder to the pool unless it's excessively long.
            /// </summary>
            public bool Return(SpanBasedStringBuilder stringBuilder)
            {
                if (stringBuilder.Capacity <= MAX_RETAINED_BUILDER_CAPACITY)
                {
                    stringBuilder.Clear();
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// A pool of SpanBasedStringBuilders as we don't want to be allocating every time a new one is requested.
        /// </summary>
        private static DefaultObjectPool<SpanBasedStringBuilder> s_stringBuilderPool =
            new DefaultObjectPool<SpanBasedStringBuilder>(new PooledObjectPolicy(), Environment.ProcessorCount);
#endif

        #region Public methods

        /// <summary>
        /// Interns the given string if possible.
        /// </summary>
        /// <param name="str">The string to intern.</param>
        /// <returns>A string equal to <paramref name="str"/>, could be the same object as <paramref name="str"/>.</returns>
        public static string Intern(string str)
        {
            InternableString internableString = new InternableString(str);
            return WeakStringCacheInterner.Instance.InternableToString(ref internableString);
        }

#if !NET35
        /// <summary>
        /// Interns the given readonly character span if possible.
        /// </summary>
        /// <param name="str">The character span to intern.</param>
        /// <returns>A string equal to <paramref name="str"/>, could be the result of calling ToString() on <paramref name="str"/>.</returns>
        public static string Intern(ReadOnlySpan<char> str)
        {
            InternableString internableString = new InternableString(str);
            return WeakStringCacheInterner.Instance.InternableToString(ref internableString);
        }
#endif

        /// <summary>
        /// Returns a new or recycled <see cref="SpanBasedStringBuilder"/>.
        /// </summary>
        /// <returns>The SpanBasedStringBuilder.</returns>
        /// <remarks>
        /// Call <see cref="IDisposable.Dispose"/> on the returned instance to recycle it.
        /// </remarks>
        public static SpanBasedStringBuilder GetSpanBasedStringBuilder()
        {
#if NET35
            return new SpanBasedStringBuilder();
#else
            return s_stringBuilderPool.Get();
#endif
        }

        /// <summary>
        /// Enables diagnostics in the interner. Call <see cref="CreateDiagnosticReport"/> to retrieve the diagnostic data.
        /// </summary>
        public static void EnableDiagnostics()
        {
            WeakStringCacheInterner.Instance.EnableStatistics();
        }

        /// <summary>
        /// Retrieves the diagnostic data describing the current state of the interner. Make sure to call <see cref="EnableDiagnostics"/> beforehand.
        /// </summary>
        public static string CreateDiagnosticReport()
        {
            return WeakStringCacheInterner.Instance.FormatStatistics();
        }

        #endregion

        /// <summary>
        /// Returns a <see cref="SpanBasedStringBuilder"/> instance back to the pool if possible.
        /// </summary>
        /// <param name="stringBuilder">The instance to return.</param>
        internal static void ReturnSpanBasedStringBuilder(SpanBasedStringBuilder stringBuilder)
        {
            if (stringBuilder == null)
            {
                throw new ArgumentNullException(nameof(stringBuilder));
            }
#if !NET35
            s_stringBuilderPool.Return(stringBuilder);
#endif
        }
    }
}
