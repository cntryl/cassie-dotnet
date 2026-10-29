using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Types
{
    /// <summary>Represents a PostgreSQL array while retaining its dimensions and lower bounds.</summary>
    /// <remarks>Elements are exposed in row-major order; the indexer addresses that flat sequence.</remarks>
    public sealed class CassieArray : IReadOnlyList<object?>
    {
        private readonly int[] _dimensions;
        private readonly int[] _lowerBounds;
        private readonly object?[] _elements;
        private readonly ReadOnlyCollection<int> _readOnlyDimensions;
        private readonly ReadOnlyCollection<int> _readOnlyLowerBounds;
        private readonly ReadOnlyCollection<object?> _readOnlyElements;

        /// <summary>Creates a PostgreSQL array value with explicit dimensions, lower bounds, and row-major elements.</summary>
        /// <param name="elementTypeOid">The PostgreSQL OID of each array element.</param>
        /// <param name="dimensions">The length of each dimension; nonzero rank requires positive lengths, and rank zero represents an empty array.</param>
        /// <param name="lowerBounds">The PostgreSQL lower bound for each dimension.</param>
        /// <param name="elements">The flattened row-major values, including null entries.</param>
        public CassieArray(int elementTypeOid, int[] dimensions, int[] lowerBounds, object?[] elements)
        {
            ArgumentNullException.ThrowIfNull(dimensions);
            ArgumentNullException.ThrowIfNull(lowerBounds);
            ArgumentNullException.ThrowIfNull(elements);
            if (dimensions.Length != lowerBounds.Length)
            {
                throw new ArgumentException("Array dimensions and lower bounds must have the same rank.", nameof(lowerBounds));
            }

            if (dimensions.Length > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), "Cassie supports arrays with at most six dimensions.");
            }

            long expectedElements = dimensions.Length == 0 ? 0 : 1;
            for (int index = 0; index < dimensions.Length; index++)
            {
                int dimension = dimensions[index];
                if (dimension <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(dimensions), "Nonempty array dimensions must have positive lengths; use rank zero for an empty array.");
                }

                if ((long)lowerBounds[index] + dimension - 1 > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(lowerBounds), "Array upper bounds must fit in a signed 32-bit value.");
                }

                expectedElements = checked(expectedElements * dimension);
            }

            if (expectedElements != elements.LongLength)
            {
                throw new ArgumentException("The flattened element count does not match the array dimensions.", nameof(elements));
            }

            ElementTypeOid = elementTypeOid;
            _dimensions = [.. dimensions];
            _lowerBounds = [.. lowerBounds];
            _elements = [.. elements];
            _readOnlyDimensions = Array.AsReadOnly(_dimensions);
            _readOnlyLowerBounds = Array.AsReadOnly(_lowerBounds);
            _readOnlyElements = Array.AsReadOnly(_elements);
        }

        /// <summary>Gets the PostgreSQL OID of each array element.</summary>
        public int ElementTypeOid { get; }

        /// <summary>Gets the number of array dimensions; an empty PostgreSQL array has rank zero.</summary>
        public int Rank => _dimensions.Length;

        /// <summary>Gets each dimension length in PostgreSQL order.</summary>
        public IReadOnlyList<int> Dimensions => _readOnlyDimensions;

        /// <summary>Gets each PostgreSQL lower bound in dimension order.</summary>
        public IReadOnlyList<int> LowerBounds => _readOnlyLowerBounds;

        /// <summary>Gets the flattened row-major elements.</summary>
        public IReadOnlyList<object?> Elements => _readOnlyElements;

        /// <inheritdoc />
        public int Count => _elements.Length;

        /// <inheritdoc />
        public object? this[int index] => _elements[index];

        /// <summary>Gets an element using its PostgreSQL subscripts.</summary>
        /// <param name="subscripts">One subscript for each dimension, including PostgreSQL lower bounds.</param>
        /// <returns>The element at the requested subscripts.</returns>
        public object? GetValue(params int[] subscripts)
        {
            ArgumentNullException.ThrowIfNull(subscripts);
            if (subscripts.Length != Rank || Rank == 0)
            {
                throw new ArgumentException("The subscript count must match the array rank.", nameof(subscripts));
            }

            int flatIndex = 0;
            for (int dimension = 0; dimension < Rank; dimension++)
            {
                long relativeIndex = (long)subscripts[dimension] - _lowerBounds[dimension];
                if (relativeIndex < 0 || relativeIndex >= _dimensions[dimension])
                {
                    throw new ArgumentOutOfRangeException(nameof(subscripts), "A subscript is outside the array bounds.");
                }

                flatIndex = checked((flatIndex * _dimensions[dimension]) + (int)relativeIndex);
            }

            return _elements[flatIndex];
        }

        /// <summary>Creates a CLR object array with the same dimensions and lower bounds.</summary>
        /// <returns>A row-major CLR array; an empty PostgreSQL array becomes an empty one-dimensional CLR array.</returns>
        [RequiresDynamicCode("Creating a CLR array with a runtime-selected rank requires dynamic code.")]
        public Array ToArray()
        {
            if (Rank == 0)
            {
                return Array.Empty<object?>();
            }

            Array result = Array.CreateInstance(typeof(object), _dimensions, _lowerBounds);
            int[] indices = [.. _lowerBounds];
            for (int flatIndex = 0; flatIndex < _elements.Length; flatIndex++)
            {
                result.SetValue(_elements[flatIndex], indices);
                IncrementIndices(indices);
            }

            return result;
        }

        /// <inheritdoc />
        public IEnumerator<object?> GetEnumerator()
        {
            return ((IEnumerable<object?>)_readOnlyElements).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private void IncrementIndices(int[] indices)
        {
            for (int dimension = Rank - 1; dimension >= 0; dimension--)
            {
                long nextIndex = (long)indices[dimension] + 1;
                if (nextIndex < (long)_lowerBounds[dimension] + _dimensions[dimension])
                {
                    indices[dimension] = (int)nextIndex;
                    return;
                }

                indices[dimension] = _lowerBounds[dimension];
            }
        }
    }
}
