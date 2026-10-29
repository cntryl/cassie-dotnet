using System.Collections;
using System.Data.Common;

namespace Cntryl.Cassie
{
    /// <summary>Stores parameters for a Cassie command.</summary>
    public sealed class CassieParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;
        /// <inheritdoc />
        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        /// <inheritdoc />
        public override int Add(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value is not DbParameter parameter)
            {
                throw new ArgumentException("Only DbParameter instances can be added.", nameof(value));
            }

            _items.Add(parameter);
            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(Array values)
        {
            ArgumentNullException.ThrowIfNull(values);
            foreach (object? value in values)
            {
                _ = Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear()
        {
            _items.Clear();
        }

        /// <inheritdoc />
        public override bool Contains(object value)
        {
            return value is DbParameter parameter && _items.Contains(parameter);
        }

        /// <inheritdoc />
        public override bool Contains(string value)
        {
            return IndexOf(value) >= 0;
        }

        /// <inheritdoc />
        public override void CopyTo(Array array, int index)
        {
            ((ICollection)_items).CopyTo(array, index);
        }

        /// <inheritdoc />
        public override IEnumerator GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        /// <inheritdoc />
        public override int IndexOf(object value)
        {
            return value is DbParameter parameter ? _items.IndexOf(parameter) : -1;
        }

        /// <inheritdoc />
        public override int IndexOf(string parameterName)
        {
            return _items.FindIndex(item => string.Equals(item.ParameterName.TrimStart('@'), parameterName.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public override void Insert(int index, object value)
        {
            if (value is not DbParameter parameter)
            {
                throw new ArgumentException("Only DbParameter instances can be inserted.", nameof(value));
            }

            _items.Insert(index, parameter);
        }

        /// <inheritdoc />
        public override void Remove(object value)
        {
            if (value is DbParameter parameter)
            {
                _ = _items.Remove(parameter);
            }
        }
        /// <inheritdoc />
        public override void RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        /// <inheritdoc />
        public override void RemoveAt(string parameterName)
        {
            RemoveAt(IndexOf(parameterName));
        }

        /// <inheritdoc />
        protected override DbParameter GetParameter(int index)
        {
            return _items[index];
        }

        /// <inheritdoc />
        protected override DbParameter GetParameter(string parameterName)
        {
            return IndexOf(parameterName) is int index and >= 0 ? GetParameter(index) : throw new ArgumentException("The parameter name was not found.", nameof(parameterName));
        }

        /// <inheritdoc />
        protected override void SetParameter(int index, DbParameter value)
        {
            _items[index] = value;
        }

        /// <inheritdoc />
        protected override void SetParameter(string parameterName, DbParameter value)
        {
            int index = IndexOf(parameterName);
            if (index < 0)
            {
                _items.Add(value);
            }
            else
            {
                _items[index] = value;
            }
        }
    }
}
