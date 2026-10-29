using System.Collections.ObjectModel;

namespace Cntryl.Cassie.Types
{
    /// <summary>A Cassie vector with a fixed dimension and finite FLOAT4 components.</summary>
    public sealed class CassieVector : IReadOnlyList<float>
    {
        private readonly float[] _values;
        private readonly ReadOnlyCollection<float> _readOnlyValues;

        /// <summary>Creates a vector from its components.</summary>
        /// <param name="values">One through 32767 finite FLOAT4 values.</param>
        public CassieVector(params float[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Length is < 1 or > short.MaxValue || values.Any(static value => !float.IsFinite(value)))
            {
                throw new ArgumentException("A Cassie vector requires one through 32767 finite FLOAT4 components.", nameof(values));
            }

            _values = [.. values];
            _readOnlyValues = Array.AsReadOnly(_values);
        }

        /// <summary>Gets the vector dimension.</summary>
        public int Dimension => _values.Length;

        /// <inheritdoc />
        public int Count => _values.Length;

        /// <inheritdoc />
        public float this[int index] => _values[index];

        /// <summary>Gets the components in order.</summary>
        public IReadOnlyList<float> Values => _readOnlyValues;

        /// <summary>Creates a copy of the components.</summary>
        public float[] ToArray()
        {
            return [.. _values];
        }

        /// <inheritdoc />
        public IEnumerator<float> GetEnumerator()
        {
            return ((IEnumerable<float>)_readOnlyValues).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
